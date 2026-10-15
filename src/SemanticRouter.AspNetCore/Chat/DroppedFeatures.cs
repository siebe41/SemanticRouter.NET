using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// The request features an attempt could not carry to its backend. The dispatcher puts one in the request's features
/// before each attempt; connectors add to it before they write the response, and the dispatcher reports the list in the
/// <c>X-Prompt-Route-Dropped</c> header and the activity entry.
/// </summary>
internal sealed class DroppedFeatures
{
    private readonly List<string> _names = [];

    public IReadOnlyList<string> Names => _names;

    /// <summary>Records <paramref name="names"/> on the attempt being served, if the dispatcher is tracking one.</summary>
    public static void Report(HttpContext context, IEnumerable<string> names)
    {
        if (context.Features.Get<DroppedFeatures>() is not { } dropped)
        {
            return;
        }

        foreach (string name in names)
        {
            if (!dropped._names.Contains(name, StringComparer.Ordinal))
            {
                dropped._names.Add(name);
            }
        }
    }

    /// <summary>What translating <paramref name="request"/> to a <paramref name="target"/> backend loses.</summary>
    /// <param name="request">The parsed request.</param>
    /// <param name="target">The backend's protocol.</param>
    /// <param name="reasoning">The backend model declares the <c>reasoning</c> capability.</param>
    public static List<string> ForTranslation(ChatCompletionRequest request, ChatProtocol target, bool reasoning)
    {
        var dropped = new List<string>();
        if (request.Protocol == target)
        {
            return dropped;
        }

        // Web search, file search, … run inside the provider: Codex declares web_search on every turn, so a backend of
        // another kind answers without it rather than not at all.
        dropped.AddRange(request.HostedTools);

        // Only the Messages API takes cache markers; the OpenAI APIs cache by prefix on their own.
        if (request.HasCacheMarkers && target != ChatProtocol.Anthropic)
        {
            dropped.Add("cache_control");
        }

        dropped.AddRange(request.ProtocolOnlyFeatures);
        if (request.Reasoning is not null)
        {
            bool carried = reasoning && (target != ChatProtocol.Anthropic || AnthropicRequestBuilder.CarriesThinking(request));
            if (!carried)
            {
                dropped.Add("reasoning");
            }
        }

        return dropped;
    }

    /// <summary>What a command-line backend loses: it takes plain prompt text.</summary>
    public static List<string> ForCli(ChatCompletionRequest request)
    {
        var dropped = new List<string>(request.HostedTools);
        if (request.HasCacheMarkers)
        {
            dropped.Add("cache_control");
        }

        dropped.AddRange(request.ProtocolOnlyFeatures);
        if (request.Reasoning is not null)
        {
            dropped.Add("reasoning");
        }

        if (request.ResponseFormat is not null)
        {
            dropped.Add("response_format");
        }

        if (request.ParallelToolCalls == false && request.HasTools)
        {
            dropped.Add("parallel_tool_calls");
        }

        return dropped;
    }
}
