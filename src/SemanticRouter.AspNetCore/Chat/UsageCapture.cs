using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Collects the token usage of the attempt being served. The dispatcher puts one in the request's features before
/// each attempt; response writers (translated replies) and the pass-through scanner (untranslated replies) fill it.
/// </summary>
internal sealed class UsageCapture
{
    private readonly Lock _gate = new();
    private ChatUsage _usage;
    private bool _seen;

    /// <summary>The usage the backend reported, or <see langword="null"/> when it reported none.</summary>
    public ChatUsage? Usage
    {
        get
        {
            lock (_gate)
            {
                return _seen ? _usage : null;
            }
        }
    }

    /// <summary>Merges a report: later non-null values win (streams report input first and output last).</summary>
    public void Observe(ChatUsage usage)
    {
        if (usage.InputTokens is null && usage.OutputTokens is null && usage.CacheReadTokens is null && usage.CacheCreationTokens is null)
        {
            return;
        }

        lock (_gate)
        {
            _usage = new ChatUsage
            {
                InputTokens = usage.InputTokens ?? _usage.InputTokens,
                OutputTokens = usage.OutputTokens ?? _usage.OutputTokens,
                CacheReadTokens = usage.CacheReadTokens ?? _usage.CacheReadTokens,
                CacheCreationTokens = usage.CacheCreationTokens ?? _usage.CacheCreationTokens,
            };
            _seen = true;
        }
    }

    public static UsageCapture? Of(HttpContext context) => context.Features.Get<UsageCapture>();

    /// <summary>Reads an OpenAI <c>usage</c> object; <c>prompt_tokens</c> includes cached tokens, which are split out.</summary>
    public static ChatUsage FromOpenAI(JsonElement usage)
    {
        int? cached = usage.TryGetObject("prompt_tokens_details", out JsonElement details) ? details.GetInt("cached_tokens") : null;
        int? prompt = usage.GetInt("prompt_tokens");
        return new ChatUsage
        {
            InputTokens = prompt is { } p && cached is { } c ? p - c : prompt,
            OutputTokens = usage.GetInt("completion_tokens"),
            CacheReadTokens = cached,
        };
    }

    /// <summary>Reads an Anthropic <c>usage</c> object.</summary>
    public static ChatUsage FromAnthropic(JsonElement usage) => new ChatUsage
    {
        InputTokens = usage.GetInt("input_tokens"),
        OutputTokens = usage.GetInt("output_tokens"),
        CacheReadTokens = usage.GetInt("cache_read_input_tokens"),
        CacheCreationTokens = usage.GetInt("cache_creation_input_tokens"),
    };

    /// <summary>Finds usage in one response document or stream event of <paramref name="protocol"/>.</summary>
    public static ChatUsage? Find(JsonElement root, ChatProtocol protocol)
    {
        if (protocol == ChatProtocol.Responses)
        {
            // response.completed carries the response; a JSON reply is the response itself.
            JsonElement response = root.TryGetObject("response", out JsonElement inner) ? inner : root;
            return response.TryGetObject("usage", out JsonElement responsesUsage) ? ResponsesResponseReader.Usage(responsesUsage) : null;
        }

        if (protocol == ChatProtocol.Anthropic)
        {
            if (root.TryGetObject("message", out JsonElement message) && message.TryGetObject("usage", out JsonElement started))
            {
                return FromAnthropic(started); // message_start
            }

            return root.TryGetObject("usage", out JsonElement usage) ? FromAnthropic(usage) : null; // message, message_delta
        }

        return root.TryGetObject("usage", out JsonElement openAI) ? FromOpenAI(openAI) : null;
    }
}

/// <summary>Passes everything through to the client's writer and records the usage it completes with.</summary>
internal sealed class UsageRecordingWriter(IChatResponseWriter inner, UsageCapture capture) : IChatResponseWriter
{
    public bool HasStarted => inner.HasStarted;

    public Task WriteTextAsync(string delta, CancellationToken cancellationToken) => inner.WriteTextAsync(delta, cancellationToken);

    public Task WriteThinkingAsync(string delta, CancellationToken cancellationToken) => inner.WriteThinkingAsync(delta, cancellationToken);

    public Task StartToolCallAsync(string id, string name, CancellationToken cancellationToken) => inner.StartToolCallAsync(id, name, cancellationToken);

    public Task WriteToolCallArgumentsAsync(string delta, CancellationToken cancellationToken) => inner.WriteToolCallArgumentsAsync(delta, cancellationToken);

    public Task EndToolCallAsync(CancellationToken cancellationToken) => inner.EndToolCallAsync(cancellationToken);

    public Task CompleteAsync(ChatStopReason stopReason, ChatUsage usage, CancellationToken cancellationToken)
    {
        capture.Observe(usage);
        return inner.CompleteAsync(stopReason, usage, cancellationToken);
    }
}
