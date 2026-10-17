using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Embeddings.Onnx;

/// <summary>
/// Sentence embeddings from a BERT-style ONNX model (sentence-transformers, BGE) run in-process with ONNX Runtime: the
/// text is tokenized with the model's WordPiece vocabulary, run through the model, pooled (mean or <c>[CLS]</c>) and
/// L2-normalized. Thread-safe; dispose to release the model.
/// </summary>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    /// <summary>Longest input in tokens; longer prompts are truncated (the end of a long prompt rarely changes its intent).</summary>
    public const int DefaultMaxTokens = 256;

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly EmbeddingPooling _pooling;
    private readonly int _maxTokens;
    private readonly bool _hasTokenTypes;
    private readonly string _output;

    // Before the first use of ONNX Runtime, which reads it once.
    static OnnxEmbeddingProvider() => OnnxRuntimeTelemetry.TurnOff();

    /// <summary>Loads a model.</summary>
    /// <param name="modelFile">The ONNX file (inputs <c>input_ids</c>, <c>attention_mask</c>, optionally <c>token_type_ids</c>; output the token vectors).</param>
    /// <param name="vocabFile">The model's <c>vocab.txt</c> (uncased WordPiece).</param>
    /// <param name="pooling">How token vectors become the sentence vector.</param>
    /// <param name="maxTokens">Longest input in tokens.</param>
    public OnnxEmbeddingProvider(string modelFile, string vocabFile, EmbeddingPooling pooling = EmbeddingPooling.Mean, int maxTokens = DefaultMaxTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(vocabFile);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 8);

        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
        };
        // Windows: its ETW telemetry events (the variable covers the uploads on Linux and macOS).
        OrtEnv.Instance().DisableTelemetryEvents();
        _session = new InferenceSession(modelFile, options);
        _tokenizer = BertTokenizer.Create(vocabFile, new BertOptions { LowerCaseBeforeTokenization = true, RemoveNonSpacingMarks = true });
        _pooling = pooling;
        _maxTokens = maxTokens;
        _hasTokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");
        _output = _session.OutputMetadata.ContainsKey("last_hidden_state") ? "last_hidden_state" : _session.OutputMetadata.Keys.First();
        Dimensions = _session.OutputMetadata[_output].Dimensions[^1];
        ModelFile = Path.GetFullPath(modelFile);
    }

    /// <summary>Size of the sentence vectors.</summary>
    public int Dimensions { get; }

    /// <summary>The loaded ONNX file.</summary>
    public string ModelFile { get; }

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ReadOnlyMemory<float>>(Embed([text])[0]);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        // One text per run: the quantized models scale activations over the whole input tensor, so a text batched with
        // others (and their padding) would get a slightly different vector than the same text routed on its own.
        var vectors = new List<ReadOnlyMemory<float>>(texts.Count);
        foreach (string text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            vectors.Add(Embed([text])[0]);
        }

        return ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(vectors);
    }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();

    private float[][] Embed(IReadOnlyList<string> texts)
    {
        IReadOnlyList<int>[] ids = texts.Select(t => _tokenizer.EncodeToIds(t, _maxTokens, addSpecialTokens: true, out _, out _)).ToArray();
        int batch = ids.Length;
        int length = Math.Max(1, ids.Max(x => x.Count));
        var inputIds = new long[batch * length];
        var mask = new long[batch * length];
        for (int b = 0; b < batch; b++)
        {
            for (int t = 0; t < ids[b].Count; t++)
            {
                inputIds[(b * length) + t] = ids[b][t];
                mask[(b * length) + t] = 1;
            }
        }

        long[] shape = [batch, length];
        using OrtValue idsValue = OrtValue.CreateTensorValueFromMemory(inputIds, shape);
        using OrtValue maskValue = OrtValue.CreateTensorValueFromMemory(mask, shape);
        using OrtValue typesValue = OrtValue.CreateTensorValueFromMemory(new long[batch * length], shape);
        var inputs = new Dictionary<string, OrtValue>(StringComparer.Ordinal) { ["input_ids"] = idsValue, ["attention_mask"] = maskValue };
        if (_hasTokenTypes)
        {
            inputs["token_type_ids"] = typesValue;
        }

        using var run = new RunOptions();
        using IDisposableReadOnlyCollection<OrtValue> outputs = _session.Run(run, inputs, [_output]);
        ReadOnlySpan<float> hidden = outputs[0].GetTensorDataAsSpan<float>();
        int dimensions = hidden.Length / (batch * length);

        var result = new float[batch][];
        for (int b = 0; b < batch; b++)
        {
            var vector = new float[dimensions];
            if (_pooling == EmbeddingPooling.Cls)
            {
                hidden.Slice(b * length * dimensions, dimensions).CopyTo(vector);
            }
            else
            {
                int tokens = 0;
                for (int t = 0; t < length; t++)
                {
                    if (mask[(b * length) + t] == 0)
                    {
                        continue;
                    }

                    tokens++;
                    ReadOnlySpan<float> token = hidden.Slice(((b * length) + t) * dimensions, dimensions);
                    for (int d = 0; d < dimensions; d++)
                    {
                        vector[d] += token[d];
                    }
                }

                for (int d = 0; d < dimensions; d++)
                {
                    vector[d] /= Math.Max(1, tokens);
                }
            }

            VectorMath.Normalize(vector);
            result[b] = vector;
        }

        return result;
    }
}
