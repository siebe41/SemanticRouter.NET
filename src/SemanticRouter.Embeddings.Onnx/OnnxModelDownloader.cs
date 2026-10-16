using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace SemanticRouter.Embeddings.Onnx;

/// <summary>
/// Puts a known model's files in a directory: downloaded once, checked against their pinned SHA-256, and written
/// atomically so an interrupted download never leaves a half file behind.
/// </summary>
public static class OnnxModelDownloader
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    /// <summary>The files of <paramref name="model"/> under <paramref name="modelsDirectory"/>, downloading what is missing or corrupt.</summary>
    /// <returns>The paths of <c>model.onnx</c> and <c>vocab.txt</c>.</returns>
    /// <exception cref="InvalidOperationException">A download did not match its checksum.</exception>
    /// <exception cref="HttpRequestException">A download failed.</exception>
    public static Task<(string ModelFile, string VocabFile)> EnsureAsync(
        OnnxModelInfo model, string modelsDirectory, HttpClient http, CancellationToken cancellationToken = default) =>
        EnsureAsync(model, modelsDirectory, http, downloadRefused: null, cancellationToken);

    /// <summary>
    /// As <see cref="EnsureAsync(OnnxModelInfo, string, HttpClient, CancellationToken)"/>, but when
    /// <paramref name="downloadRefused"/> says why it may not be downloaded (offline mode, the machine policy), a missing
    /// or corrupt file is an error saying where to place it instead.
    /// </summary>
    /// <exception cref="InvalidOperationException">A download did not match its checksum, or was refused.</exception>
    public static async Task<(string ModelFile, string VocabFile)> EnsureAsync(
        OnnxModelInfo model, string modelsDirectory, HttpClient http, string? downloadRefused, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        ArgumentNullException.ThrowIfNull(http);

        string directory = Path.Combine(Path.GetFullPath(modelsDirectory), model.Name);
        string modelFile = Path.Combine(directory, "model.onnx");
        string vocabFile = Path.Combine(directory, "vocab.txt");
        SemaphoreSlim gate = Gates.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            await EnsureFileAsync(modelFile, model.ModelUrl, model.ModelSha256, http, downloadRefused, cancellationToken).ConfigureAwait(false);
            await EnsureFileAsync(vocabFile, model.VocabUrl, model.VocabSha256, http, downloadRefused, cancellationToken).ConfigureAwait(false);
            return (modelFile, vocabFile);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task EnsureFileAsync(string path, Uri url, string sha256, HttpClient http, string? refused, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && string.Equals(await HashAsync(path, cancellationToken).ConfigureAwait(false), sha256, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (refused is not null)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} is not on this machine and cannot be downloaded ({refused}). Download {url} elsewhere and " +
                $"place it at {path} (SHA-256 {sha256}), or set Embeddings:ModelPath to a folder holding model.onnx and vocab.txt.");
        }

        string temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".download";
        try
        {
            using (HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (source.ConfigureAwait(false))
                {
                    FileStream target = File.Create(temp);
                    await using (target.ConfigureAwait(false))
                    {
                        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            string actual = await HashAsync(temp, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{url} has SHA-256 {actual}, expected {sha256}: the download is corrupt or the file changed upstream.");
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (HttpRequestException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The usual causes: no internet, a proxy the process does not know about, or one that inspects TLS.
            string reason = ex.InnerException is System.Security.Authentication.AuthenticationException tls
                ? "its TLS certificate is not trusted here: " + tls.Message.TrimEnd('.')
                : ex.Message.TrimEnd('.');
            throw new HttpRequestException(
                $"Could not download {Path.GetFileName(path)} from {url.Host} ({reason}). Behind a proxy, set HTTPS_PROXY; if the proxy inspects TLS, " +
                $"set SemanticRouter:Network:CaBundle to its root certificate. Or download {url} on a machine that can, and copy it to {path} " +
                $"(SHA-256 {sha256}); or set Embeddings:ModelPath to a folder holding model.onnx and vocab.txt.",
                ex, ex.StatusCode);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        FileStream stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
    }
}
