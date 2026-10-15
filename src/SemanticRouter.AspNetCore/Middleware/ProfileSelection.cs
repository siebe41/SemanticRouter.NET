using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Decides which routing profile a request runs under, and which content signals keep it local. Precedence: the
/// gateway client's own profile (it cannot be left), then the profile header, then the model name
/// (<c>auto@private</c> or a virtual model), then the first matching rule.
/// </summary>
internal static partial class ProfileSelection
{
    /// <summary>How a client's model name resolves.</summary>
    /// <param name="Strategy">A strategy the name asks for.</param>
    /// <param name="RequestedModel">A catalog model the name asks for.</param>
    /// <param name="Profile">A profile the name asks for (<c>model@profile</c> or a virtual model's).</param>
    public sealed record ModelChoice(SelectionStrategy? Strategy, string? RequestedModel, string? Profile);

    /// <summary>The profile and signals for one request.</summary>
    public sealed record Choice(string? Profile, bool LocalOnly, IReadOnlyList<string> Signals, string? Project, string? Error);

    /// <summary>Resolves <c>name</c>, <c>name@profile</c>, virtual models and the alias globs.</summary>
    public static ModelChoice ResolveModel(RouterRuntime runtime, string? model)
    {
        string? name = model?.Trim();
        string? profile = null;
        if (name is not null && name.LastIndexOf('@') is > 0 and var at)
        {
            profile = name[(at + 1)..];
            name = name[..at];
        }

        SelectionStrategy? virtualStrategy = null;
        if (name is not null && runtime.Configuration.Middleware.VirtualModels.TryGetValue(name, out VirtualModel? virtualModel))
        {
            profile ??= virtualModel.Profile;
            virtualStrategy = virtualModel.Strategy;
            name = ModelAliases.Auto;
        }

        (SelectionStrategy? strategy, string? requested) = ModelAliases.Resolve(PromptRoutingMiddleware.MapModel(runtime, name));
        if (requested is not null && requested.StartsWith(ModelFamilies.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            // "family:opus": the catalog's Opus model when it has one, otherwise the most capable model available.
            requested = ModelFamilies.Resolve(runtime.Router.Models, requested[ModelFamilies.Prefix.Length..])?.Name;
            strategy = requested is null ? SelectionStrategy.MostCapable : null;
        }

        return new ModelChoice(strategy ?? virtualStrategy, requested, profile);
    }

    /// <summary>Chooses the profile and local-only signals for a parsed request.</summary>
    public static Choice Choose(HttpRequest request, RouterRuntime runtime, ChatCompletionRequest chat, string? client, string? modelProfile)
    {
        PromptRouterMiddlewareOptions options = runtime.Configuration.Middleware;
        IDictionary<string, RoutingProfile> profiles = runtime.Configuration.Profiles;
        string? project = request.Headers[options.ProjectHeader].FirstOrDefault() is { Length: > 0 } header ? header : UserProject(chat.UserId);

        string? profile = null;
        if (client is not null && options.ClientProfiles.TryGetValue(client, out string? enforced))
        {
            profile = enforced;
        }
        else if (request.Headers[options.ProfileHeader].FirstOrDefault() is { Length: > 0 } named)
        {
            profile = named.Trim();
        }
        else if (!string.IsNullOrEmpty(modelProfile))
        {
            profile = modelProfile;
        }
        else
        {
            profile = options.ProfileRules.FirstOrDefault(rule => Matches(rule, client, project, chat))?.Profile;
        }

        if (profile is not null && !profiles.ContainsKey(profile))
        {
            string known = profiles.Count == 0 ? "none are configured" : "known: " + string.Join(", ", profiles.Keys);
            return new Choice(null, false, [], project, $"Unknown routing profile '{profile}' ({known}).");
        }

        var signals = new List<string>();
        if (options.LocalOnlyWhen.Secrets || options.LocalOnlyWhen.FilePaths)
        {
            foreach (string text in Texts(chat))
            {
                if (options.LocalOnlyWhen.Secrets && !signals.Contains("secret") && Finds(SecretPattern(), text))
                {
                    signals.Add("secret");
                }

                if (options.LocalOnlyWhen.FilePaths && !signals.Contains("file path") && Finds(FilePathPattern(), text))
                {
                    signals.Add("file path");
                }
            }
        }

        return new Choice(profile, signals.Count > 0, signals, project, null);
    }

    /// <summary>
    /// The project label for a client's own user id: <c>user:</c> and the first 12 hex digits of its SHA-256, stable
    /// but never the id itself, which can name an account or a session.
    /// </summary>
    internal static string? UserProject(string? userId) =>
        string.IsNullOrEmpty(userId) ? null : "user:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..12];

    /// <summary>A scan that runs out of time counts as a match: when in doubt, stay local.</summary>
    private static bool Finds(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    private static bool Matches(ProfileRule rule, string? client, string? project, ChatCompletionRequest chat) =>
        (rule.Clients.Count == 0 || (client is not null && rule.Clients.Contains(client, StringComparer.OrdinalIgnoreCase)))
        && (rule.Projects.Count == 0 || (project is not null && rule.Projects.Any(p => Glob.IsMatch(p, project))))
        && (rule.ToolsInclude.Count == 0 || chat.Tools.Any(t => rule.ToolsInclude.Any(p => Glob.IsMatch(p, t.OriginalName ?? t.Name))))
        && !chat.Tools.Any(t => rule.ToolsExclude.Any(p => Glob.IsMatch(p, t.OriginalName ?? t.Name)));

    private static IEnumerable<string> Texts(ChatCompletionRequest chat)
    {
        foreach (ChatTurn turn in chat.Turns)
        {
            foreach (ContentBlock block in turn.Blocks)
            {
                switch (block)
                {
                    case TextBlock text:
                        yield return text.Text;
                        break;
                    case ToolResultBlock result:
                        yield return result.Content;
                        break;
                    case ToolUseBlock use:
                        yield return use.InputJson;
                        break;
                }
            }
        }
    }

    // API keys (OpenAI, Anthropic, GitHub, AWS, Slack, Google), private key blocks and JWTs.
    [GeneratedRegex(
        @"\bsk-(?:ant-|proj-)?[A-Za-z0-9_\-]{20,}|\bgh[pousr]_[A-Za-z0-9]{36,}|\bgithub_pat_[A-Za-z0-9_]{22,}|\bAKIA[0-9A-Z]{16}\b|\bxox[abprs]-[A-Za-z0-9\-]{10,}|\bAIza[0-9A-Za-z_\-]{35}|-----BEGIN [A-Z ]*PRIVATE KEY-----|\beyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    internal static partial Regex SecretPattern();

    // Absolute paths: /home/…, /Users/…, ~/…, C:\…
    [GeneratedRegex(
        @"(?:^|[\s""'(=])(?:/(?:home|Users|etc|var|opt|srv|usr|tmp|root|mnt|workspace)/[^\s""'<>]+|~/[^\s""'<>]+|[A-Za-z]:\\[^\s""'<>]+)",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex FilePathPattern();
}
