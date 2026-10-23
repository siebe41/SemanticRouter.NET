using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// What a person asked for with an inline callout: a model, a strategy, a tier, local only, or back to automatic.
/// </summary>
/// <param name="Tag">The callout as typed, with its <c>@</c>.</param>
/// <param name="Model">The catalog model to use (<c>@claude-opus</c>, or a family such as <c>@opus</c>).</param>
/// <param name="Strategy">A selection strategy (<c>@best</c>, <c>@cheap</c>, <c>@priority</c>).</param>
/// <param name="Tier">The tier to route at, up or down (<c>@tier3</c>).</param>
/// <param name="Local">Only local models (<c>@local</c>).</param>
/// <param name="Clear">Back to automatic routing (<c>@auto</c>).</param>
internal sealed record Callout(string Tag, string? Model = null, SelectionStrategy? Strategy = null, int? Tier = null, bool Local = false, bool Clear = false)
{
    /// <summary>The tier to route at, when the callout names one (<c>@auto</c> names none).</summary>
    public int? ExplicitTier => Clear ? null : Tier;

    /// <summary>Whether the callout keeps the request on local models.</summary>
    public bool LocalOnly => !Clear && Local;

    /// <summary>The model and strategy to route with: the callout's model (with no strategy) or its strategy, over the ones the request asked for.</summary>
    public (string? Model, SelectionStrategy? Strategy) Choose(string? model, SelectionStrategy? strategy) => this switch
    {
        { Clear: true } => (model, strategy),
        { Model: { } named } => (named, null),
        { Strategy: { } chosen } => (model, chosen),
        _ => (model, strategy),
    };
}

/// <summary>
/// Inline callouts (<see cref="CalloutOptions"/>): a message that starts with <c>@opus</c>, <c>@local</c>,
/// <c>@best</c>, <c>@tier3</c> or a catalog model name routes as asked, over the router's own judgement, in any client.
/// The tag is taken out of the request before it is forwarded, so the model never sees it.
/// </summary>
internal static partial class Callouts
{
    /// <summary>
    /// The <c>@tag</c> a message starts with (after whitespace and any injected context blocks such as <c>&lt;system-reminder&gt;</c>),
    /// without the <c>@</c>; <see langword="null"/> when it does not start with one.
    /// </summary>
    public static string? LeadingTag(string text) => Leading(text) is { } found ? found.Tag : null;

    /// <summary>
    /// The callout <paramref name="prompt"/> starts with, or <see langword="null"/> when callouts are off or it starts
    /// with none (or with a tag that is not a callout).
    /// </summary>
    public static Callout? Find(string prompt, RouterRuntime runtime) =>
        runtime.Configuration.Middleware.Callouts.Enabled && LeadingTag(prompt) is { } tag ? Resolve(tag, runtime) : null;

    /// <summary>
    /// Routes a prompt the way the gateway routes the first turn of a conversation that sends it: its leading callout
    /// applied and taken out of the prompt. Returns the plan and the callout note the gateway would record with it.
    /// </summary>
    public static async Task<(RoutingPlan Plan, string? Note)> RouteAsync(
        RouterRuntime runtime, IPromptRouter router, PromptRoutingRequest request, CancellationToken cancellationToken)
    {
        if (Find(request.Prompt, runtime) is not { } callout)
        {
            return (await router.RouteAsync(request, cancellationToken).ConfigureAwait(false), null);
        }

        (string? model, SelectionStrategy? strategy) = callout.Choose(request.RequestedModel, request.Strategy);
        bool localOnly = request.LocalOnly || callout.LocalOnly;
        RoutingPlan plan = await router.RouteAsync(
            request with
            {
                Prompt = StripText(request.Prompt, runtime) ?? request.Prompt,
                RequestedModel = model,
                Strategy = strategy,
                ExplicitTier = callout.ExplicitTier,
                LocalOnly = localOnly,
                EmbeddingsAllowed = request.EmbeddingsAllowed && (!localOnly || runtime.Configuration.Embeddings.KeepsPromptsLocal),
            },
            cancellationToken).ConfigureAwait(false);
        return Settle(callout, plan);
    }

    /// <summary>
    /// The plan once <paramref name="choice"/> applied, and the note shown with the decision: the tag, with "(earlier in
    /// the conversation)" when it was made on an earlier turn. A plan that used the callout's model gets the reason
    /// <see cref="RoutingReason.ExplicitCallout"/>; when the routing profile does not allow that model, the note says so.
    /// </summary>
    public static (RoutingPlan Plan, string? Note) Settle(Callout? choice, RoutingPlan plan, bool remembered = false)
    {
        if (choice is null or { Clear: true })
        {
            return (plan, choice?.Tag);
        }

        string note = choice.Tag + (remembered ? " (earlier in the conversation)" : string.Empty);
        if (choice.Model is null)
        {
            return (plan, note);
        }

        return plan.Classification.Reason == RoutingReason.ModelOverride
            ? (plan with { Classification = plan.Classification with { Reason = RoutingReason.ExplicitCallout } }, note)
            : (plan, note + " (not allowed here; routed automatically)");
    }

    /// <summary>What <paramref name="tag"/> asks for, or <see langword="null"/> when it is not a callout (a file mention, say).</summary>
    public static Callout? Resolve(string tag, RouterRuntime runtime, int depth = 0)
    {
        string name = tag.Trim().TrimStart('@');
        if (name.Length == 0 || depth > 3)
        {
            return null;
        }

        string display = "@" + name;
        CalloutOptions options = runtime.Configuration.Middleware.Callouts;
        if (options.Names.TryGetValue(name, out string? target))
        {
            Callout? named = ResolveTarget(target, runtime, depth);
            return named is null ? null : named with { Tag = display };
        }

        switch (name.ToLowerInvariant())
        {
            case "auto":
                return new Callout(display, Clear: true);
            case "best":
                return new Callout(display, Strategy: SelectionStrategy.MostCapable);
            case "cheap" or "cheapest":
                return new Callout(display, Strategy: SelectionStrategy.CheapestCapable);
            case "priority":
                return new Callout(display, Strategy: SelectionStrategy.Priority);
            case "local":
                return new Callout(display, Local: true);
        }

        if (TierTag().Match(name) is { Success: true } tier)
        {
            return new Callout(display, Tier: int.Parse(tier.Groups["tier"].Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        IReadOnlyList<ModelDefinition> models = runtime.Router.Models;
        if (models.FirstOrDefault(m => m.Enabled && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) is { } exact)
        {
            return new Callout(display, Model: exact.Name);
        }

        return ModelFamilies.Resolve(models, name) is { } family ? new Callout(display, Model: family.Name) : null;
    }

    /// <summary>A configured name's target: a catalog model, <c>auto:…</c>, <c>family:…</c>, <c>tierN</c> or <c>local</c>.</summary>
    private static Callout? ResolveTarget(string target, RouterRuntime runtime, int depth)
    {
        string trimmed = target.Trim();
        (SelectionStrategy? strategy, string? model) = ModelAliases.Resolve(trimmed);
        if (strategy is not null)
        {
            return new Callout(trimmed, Strategy: strategy);
        }

        if (model is not null && model.StartsWith(ModelFamilies.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return ModelFamilies.Resolve(runtime.Router.Models, model[ModelFamilies.Prefix.Length..]) is { } family ? new Callout(trimmed, Model: family.Name) : null;
        }

        return Resolve(trimmed, runtime, depth + 1);
    }

    /// <summary>
    /// The request with every user message's leading callout removed (they stay in the history clients resend), or
    /// <see langword="null"/> when there was none. Only recognised callouts are removed, and never when the message
    /// would be left empty.
    /// </summary>
    public static byte[]? Strip(ReadOnlyMemory<byte> body, ChatProtocol protocol, RouterRuntime runtime)
    {
        JsonNode? root;
        try
        {
            root = JsonHelpers.ParseEditable(body);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject request)
        {
            return null;
        }

        bool changed = false;
        if (protocol == ChatProtocol.Responses)
        {
            if (request["input"] is JsonValue text && text.TryGetValue(out string? input))
            {
                if (StripText(input, runtime) is { } stripped)
                {
                    request["input"] = stripped;
                    changed = true;
                }
            }
            else if (request["input"] is JsonArray items)
            {
                changed = StripMessages(items, runtime);
            }
        }
        else if (request["messages"] is JsonArray messages)
        {
            changed = StripMessages(messages, runtime);
        }

        return changed ? JsonSerializer.SerializeToUtf8Bytes(request) : null;
    }

    private static bool StripMessages(JsonArray messages, RouterRuntime runtime)
    {
        bool changed = false;
        foreach (JsonObject message in messages.OfType<JsonObject>())
        {
            if (Text(message["role"]) != "user")
            {
                continue;
            }

            switch (message["content"])
            {
                case JsonValue value when value.TryGetValue(out string? text):
                    if (StripText(text, runtime) is { } stripped)
                    {
                        message["content"] = stripped;
                        changed = true;
                    }

                    break;
                case JsonArray parts:
                    // The first text part with something besides injected reminders decides; images, tool results
                    // and reminder-only parts are skipped.
                    foreach (JsonObject part in parts.OfType<JsonObject>())
                    {
                        if (Text(part["type"]) is not ("text" or "input_text")
                            || part["text"] is not JsonValue partText || !partText.TryGetValue(out string? partContent))
                        {
                            continue;
                        }

                        if (Leading(partContent, runtime.Configuration.Middleware.InjectedTags) is { } leading)
                        {
                            if (StripText(partContent, runtime, leading) is { } strippedPart)
                            {
                                part["text"] = strippedPart;
                                changed = true;
                            }

                            break;
                        }

                        if (InjectedBlocks.Remove(partContent, runtime.Configuration.Middleware.InjectedTags).Trim().Length > 0)
                        {
                            break;
                        }
                    }

                    break;
            }
        }

        return changed;
    }

    /// <summary>The node's string value, or <see langword="null"/> when it is missing or not a string.</summary>
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary><paramref name="text"/> without its leading callout, or <see langword="null"/> to leave it as it is.</summary>
    private static string? StripText(string text, RouterRuntime runtime, (string Tag, int Start, int End)? found = null)
    {
        if ((found ?? Leading(text, runtime.Configuration.Middleware.InjectedTags)) is not { } leading || Resolve(leading.Tag, runtime) is null)
        {
            return null;
        }

        string stripped = text[..leading.Start] + text[leading.End..];
        return InjectedBlocks.Remove(stripped, runtime.Configuration.Middleware.InjectedTags).Trim().Length == 0 ? null : stripped;
    }

    /// <summary>Where a leading <c>@tag</c> is: its name and the span to remove (the tag and the whitespace after it).</summary>
    private static (string Tag, int Start, int End)? Leading(string text, IEnumerable<string>? injectedTags = null)
    {
        int position = InjectedBlocks.SkipLeading(text, injectedTags);
        if (position < 0)
        {
            return null;
        }

        if (position >= text.Length || text[position] != '@')
        {
            return null;
        }

        int nameEnd = position + 1;
        while (nameEnd < text.Length && !char.IsWhiteSpace(text[nameEnd]))
        {
            nameEnd++;
        }

        if (nameEnd == position + 1)
        {
            return null;
        }

        int removeEnd = nameEnd;
        while (removeEnd < text.Length && char.IsWhiteSpace(text[removeEnd]))
        {
            removeEnd++;
        }

        return (text[(position + 1)..nameEnd], position, removeEnd);
    }

    [GeneratedRegex(@"^tier(?<tier>[1-9])$", RegexOptions.IgnoreCase)]
    private static partial Regex TierTag();
}

/// <summary>Finds a catalog model by family, such as <c>opus</c> or <c>qwen</c>, for callouts and <c>family:</c> aliases.</summary>
internal static class ModelFamilies
{
    /// <summary>The alias target prefix: <c>"opus-pinned": "family:opus"</c> prefers the catalog's Opus model.</summary>
    public const string Prefix = "family:";

    /// <summary>
    /// The enabled catalog model whose name or backend model id contains <paramref name="family"/>: the most capable
    /// one, then the cheapest. Families shorter than three characters match nothing (too ambiguous).
    /// </summary>
    public static ModelDefinition? Resolve(IReadOnlyList<ModelDefinition> models, string family)
    {
        string name = family.Trim();
        if (name.Length < 3)
        {
            return null;
        }

        return models
            .Where(m => m.Enabled
                && (m.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || (m.Model?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false)))
            .OrderByDescending(m => m.Tier)
            .ThenBy(m => m.InputCostPerMillionTokens + m.OutputCostPerMillionTokens)
            .FirstOrDefault();
    }
}
