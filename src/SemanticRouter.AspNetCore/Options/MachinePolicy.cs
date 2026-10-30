using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Settings an administrator sets for the whole machine, which the user's configuration (the file, the dashboard, the
/// desktop app's preferences) cannot override. Read once at start-up from a place only administrators can write
/// (<see cref="MachinePolicyLoader"/>): the registry under <c>HKLM\Software\Policies\SemanticRouter</c> on Windows, the
/// managed preferences domain <c>io.github.siebe41.semantic-router</c> on macOS, <c>/etc/semantic-router/policy.json</c>
/// on Linux and in containers. Every setting is optional; an empty policy changes nothing.
/// </summary>
/// <remarks>
/// A configuration that breaks the policy is not refused at start-up: what breaks it is turned off, and each change is
/// listed with the runtime's warnings and logged. A configuration change that would break it (the dashboard, the admin
/// API, a rollback) is refused.
/// </remarks>
[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public sealed class MachinePolicy
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter(), new SwitchConverter() },
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>No policy: nothing is restricted.</summary>
    public static MachinePolicy None { get; } = new();

    /// <summary>
    /// Connector types that may not be used (case-insensitive), e.g. <c>["Cli", "Copilot"]</c> to keep subscription logins
    /// out. Any <see cref="ConnectorOptions.Type"/> can be named, including types from connector packages.
    /// </summary>
    public IList<string> DisabledConnectorTypes { get; } = new List<string>();

    /// <summary>CLI presets that may not be used, e.g. <c>["ClaudeCode", "Codex"]</c>.</summary>
    public IList<CliPreset> DisabledPresets { get; } = new List<CliPreset>();

    /// <summary>
    /// The only hosts that connectors, the embeddings endpoint and webhooks may reach: a host name (<c>api.anthropic.com</c>),
    /// a domain and its subdomains (<c>*.corp.example</c>) or an IP address, optionally with a port (<c>gateway:8443</c>).
    /// This machine (loopback) is always allowed. Empty: any host.
    /// </summary>
    public IList<string> AllowedEndpoints { get; } = new List<string>();

    /// <summary>
    /// When set, the only hosts a model or the embeddings endpoint may be marked local on (<see cref="ModelDefinition.IsLocal"/>),
    /// in the same form as <see cref="AllowedEndpoints"/>; this machine always counts. A model on any other host, a CLI
    /// or Copilot, is treated as not local, so local-only requests never go there. <see langword="null"/>: not checked.
    /// </summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Replace)]
    public IList<string>? LocalHosts { get; set; }

    /// <summary>Only local models may be used, and only embeddings that keep prompts local.</summary>
    public bool RequireLocalOnly { get; set; }

    /// <summary>
    /// The most prompt text the activity log may keep (<see cref="StateOptions.StorePrompts"/>): <c>Off</c> or
    /// <c>Redacted</c> caps the user's setting. <see langword="null"/>: not limited.
    /// </summary>
    public PromptStorage? StorePrompts { get; set; }

    /// <summary>The most days activity (with its prompt previews) and feedback are kept. <see langword="null"/>: not limited.</summary>
    public int? MaxRetentionDays { get; set; }

    /// <summary>The dashboard: <c>Default</c>, <c>ReadOnly</c> (nobody can change the router from it) or <c>Off</c>.</summary>
    public DashboardPolicy Dashboard { get; set; }

    /// <summary>Desktop app updates.</summary>
    public UpdatePolicy Updates { get; set; } = new();

    /// <summary>
    /// Offline mode: the router reaches only this machine and the hosts in <see cref="AllowedEndpoints"/>; CLI and
    /// Copilot connectors, which always reach their vendor, are off, the ONNX model is never downloaded and the desktop
    /// app does not update. The user's own <c>Network:Offline</c> does the same, but cannot be turned off here.
    /// </summary>
    public bool Offline { get; set; }

    /// <summary>For the user's offline mode (<c>Network:AllowedHosts</c>): hosts it may reach besides this machine.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> OfflineHosts { get; private set; } = [];

    /// <summary>Where offline mode comes from, for messages: <c>Network:Offline</c> or the machine policy.</summary>
    private string OfflineSetting => _offlineByPolicy ? "your organization's policy" : "Network:Offline";

    private bool _offlineByPolicy = true;

    /// <summary>Features that can be switched off.</summary>
    public FeaturePolicy Features { get; set; } = new();

    /// <summary>Where the policy was read from, e.g. <c>/etc/semantic-router/policy.json</c>; <see langword="null"/> for none.</summary>
    [JsonIgnore]
    public string? Source { get; set; }

    /// <summary>Whether the policy restricts anything.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        DisabledConnectorTypes.Count == 0 && DisabledPresets.Count == 0 && AllowedEndpoints.Count == 0 && LocalHosts is null
        && !RequireLocalOnly && StorePrompts is null && MaxRetentionDays is null && Dashboard == DashboardPolicy.Default
        && Updates.IsDefault && Features.IsDefault && !Offline;

    /// <summary>
    /// The rules in force: <paramref name="policy"/>, plus the user's own offline mode from <paramref name="network"/>
    /// (<c>Network:Offline</c> and <c>Network:AllowedHosts</c>). The user's list of hosts does not widen an offline mode
    /// the machine policy sets.
    /// </summary>
    public static MachinePolicy Effective(MachinePolicy? policy, NetworkOptions? network)
    {
        policy ??= None;
        if (network is not { Offline: true } || policy.Offline)
        {
            return policy;
        }

        MachinePolicy effective = FromJson(policy.ToJson(), policy.Source);
        effective.Offline = true;
        effective._offlineByPolicy = false;
        effective.OfflineHosts = [.. network.AllowedHosts];
        return effective;
    }

    /// <summary>Whether <paramref name="pattern"/> is a host name, <c>*.domain</c> or IP address, with an optional port.</summary>
    public static bool IsHostPattern(string pattern) => HostPattern.TryParse(pattern, out _);

    /// <summary>Reads a policy document. Unknown settings are an error, so a misspelt one cannot silently do nothing.</summary>
    /// <exception cref="InvalidDataException">The document is not a valid policy.</exception>
    public static MachinePolicy FromJson(string json, string? source = null)
    {
        MachinePolicy? policy;
        try
        {
            policy = JsonSerializer.Deserialize<MachinePolicy>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The machine policy{(source is null ? string.Empty : " in " + source)} is not valid: {ex.Message}", ex);
        }

        policy ??= new MachinePolicy();
        if (policy.Errors().FirstOrDefault() is { } error)
        {
            throw new InvalidDataException($"The machine policy{(source is null ? string.Empty : " in " + source)} is not valid: {error}");
        }

        policy.Source = source;
        return policy;
    }

    /// <summary>The policy as JSON (what <c>doctor</c> prints).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    private IEnumerable<string> Errors()
    {
        if (DisabledConnectorTypes.Any(string.IsNullOrWhiteSpace))
        {
            yield return "DisabledConnectorTypes may not hold an empty name.";
        }

        foreach (string pattern in AllowedEndpoints.Concat(LocalHosts ?? []))
        {
            if (!HostPattern.TryParse(pattern, out _))
            {
                yield return $"'{pattern}' is not a host name, *.domain or IP address (with an optional :port).";
            }
        }

        if (MaxRetentionDays is < 1)
        {
            yield return "MaxRetentionDays must be at least 1.";
        }

        if (Updates.Mode == UpdateMode.Feed && !Uri.TryCreate(Updates.Feed, UriKind.Absolute, out _))
        {
            yield return "Updates:Feed must be an absolute URL or folder path when Updates:Mode is Feed.";
        }

        if (Updates.Publisher is { } publisher && UpdatePolicy.PublisherError(publisher) is { } wrong)
        {
            yield return wrong;
        }
    }

    /// <summary>Why <paramref name="connector"/> may not be used, or <see langword="null"/> when it may.</summary>
    public string? Blocks(ConnectorOptions connector)
    {
        ArgumentNullException.ThrowIfNull(connector);
        if (DisabledConnectorTypes.Any(t => string.Equals(t?.Trim(), connector.Type, StringComparison.OrdinalIgnoreCase)))
        {
            return $"{connector.Type} connectors are turned off by your organization's policy";
        }

        if (connector.Type == ConnectorTypes.Cli && DisabledPresets.Contains(connector.Preset))
        {
            return $"the {connector.Preset} CLI is turned off by your organization's policy";
        }

        if (Offline && connector.RunsProgram)
        {
            return $"{connector.Type} connectors reach their vendor, so they are off in offline mode ({OfflineSetting})";
        }

        return connector.RunsProgram || connector.BaseAddress is not { } address ? null : Refuses(address);
    }

    /// <summary>Whether the router may send requests to <paramref name="address"/> (<see cref="AllowedEndpoints"/>, <see cref="Offline"/>).</summary>
    public bool AllowsEndpoint(Uri address) => Refuses(address) is null;

    /// <summary>Why the router may not send requests to <paramref name="address"/>, or <see langword="null"/> when it may.</summary>
    public string? Refuses(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (AllowedEndpoints.Count > 0 && !Matches(address, AllowedEndpoints))
        {
            return $"{address.Authority} is not an allowed endpoint in your organization's policy";
        }

        return Offline && !Matches(address, [.. AllowedEndpoints, .. OfflineHosts])
            ? $"{address.Authority} is not reachable in offline mode ({OfflineSetting})"
            : null;
    }

    /// <summary>Whether <see cref="LocalHosts"/> lets a model at <paramref name="address"/> be local (<see langword="null"/>: a connector that runs a program, such as a CLI or Copilot).</summary>
    public bool AllowsLocal(Uri? address) =>
        LocalHosts is null || (address is not null && Matches(address, LocalHosts));

    private static bool Matches(Uri address, IEnumerable<string> patterns) =>
        address.IsAbsoluteUri && (address.IsLoopback || patterns.Any(p => HostPattern.TryParse(p, out HostPattern pattern) && pattern.Matches(address)));

    /// <summary>
    /// Brings <paramref name="configuration"/> within the policy, in place, and says what it changed (nothing when it
    /// already was). Models whose connector is not allowed are disabled, models and embeddings on hosts that may not be
    /// local stop counting as local, and switched-off features are turned off.
    /// </summary>
    public IReadOnlyList<string> Enforce(RouterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var changes = new List<string>();
        if (IsEmpty)
        {
            return changes;
        }

        foreach ((string name, ConnectorOptions connector) in configuration.Connectors)
        {
            if (Blocks(connector) is { } reason)
            {
                foreach (ModelDefinition model in configuration.Models.Where(m => m.Enabled && Same(m.Connector, name)))
                {
                    model.Enabled = false;
                    changes.Add($"Model '{model.Name}' is turned off: connector '{name}': {reason}.");
                }
            }
        }

        foreach (ModelDefinition model in configuration.Models.Where(m => m.IsLocal))
        {
            ConnectorOptions? connector = configuration.Connectors.FirstOrDefault(c => Same(c.Key, model.Connector)).Value;
            Uri? address = connector is null || connector.RunsProgram ? null : connector.BaseAddress;
            if (!AllowsLocal(address))
            {
                model.IsLocal = false;
                changes.Add($"Model '{model.Name}' does not count as local: {(address is null ? "its connector is not a server" : address.Authority + " is not a local host")} in your organization's policy.");
            }
        }

        if (RequireLocalOnly)
        {
            foreach (ModelDefinition model in configuration.Models.Where(m => m.Enabled && !m.IsLocal))
            {
                model.Enabled = false;
                changes.Add($"Model '{model.Name}' is turned off: your organization's policy allows local models only.");
            }
        }

        EmbeddingProviderOptions embeddings = configuration.Embeddings;
        if (!EmbeddingProviderKinds.IsBuiltIn(embeddings.Provider) && !EmbeddingProviderKinds.RunsInProcess(embeddings.Provider))
        {
            EnforcePackagedEmbeddings(embeddings, changes);
        }
        else if (embeddings.Provider == EmbeddingProviderKinds.OpenAICompatible && embeddings.Endpoint is { } endpoint)
        {
            if (embeddings.IsLocal == true && !AllowsLocal(endpoint))
            {
                embeddings.IsLocal = false;
                changes.Add($"The embeddings endpoint {endpoint.Authority} does not count as local in your organization's policy.");
            }

            if (Refuses(endpoint) is { } refused)
            {
                embeddings.Provider = EmbeddingProviderKinds.Hashing;
                changes.Add($"The embeddings endpoint is not used ({refused}): routing uses the in-process hashing embeddings.");
            }
            else if (RequireLocalOnly && !embeddings.KeepsPromptsLocal)
            {
                embeddings.Provider = EmbeddingProviderKinds.Hashing;
                changes.Add($"The embeddings endpoint {endpoint.Authority} is not allowed by your organization's policy: routing uses the in-process hashing embeddings.");
            }
        }

        if (StorePrompts is { } most && Rank(configuration.State.StorePrompts) > Rank(most))
        {
            changes.Add($"Prompt previews are stored as '{most}' (not '{configuration.State.StorePrompts}'): your organization's policy.");
            configuration.State.StorePrompts = most;
        }

        if (MaxRetentionDays is { } days && (configuration.State.Retention.ActivityDays ?? 30) > days)
        {
            configuration.State.Retention.ActivityDays = days;
            changes.Add($"Activity is kept for at most {days} day{(days == 1 ? string.Empty : "s")}: your organization's policy.");
        }

        Features.Enforce(configuration, changes, this);
        return changes;

        static int Rank(PromptStorage storage) => storage switch
        {
            PromptStorage.Off => 0,
            PromptStorage.Redacted => 1,
            _ => 2,
        };
    }

    /// <summary>
    /// Holds a packaged embedding provider that does not run in this process (it may send text anywhere) to the policy:
    /// offline mode and local-only models replace it with Hashing, as do allowed endpoints that do not include its
    /// <see cref="EmbeddingProviderOptions.Endpoint"/>, and local hosts that do not include it make it not local.
    /// </summary>
    private void EnforcePackagedEmbeddings(EmbeddingProviderOptions embeddings, List<string> changes)
    {
        string provider = embeddings.Provider;
        if (embeddings.IsLocal == true && !AllowsLocal(embeddings.Endpoint))
        {
            embeddings.IsLocal = false;
            changes.Add($"The {provider} embeddings do not count as local in your organization's policy.");
        }

        string? refused = Offline ? $"{provider} embeddings may reach their vendor, so they are off in offline mode ({OfflineSetting})"
            : AllowedEndpoints.Count == 0 ? null
            : embeddings.Endpoint is { } endpoint ? Refuses(endpoint)
            : $"{provider} embeddings name no Endpoint, and your organization's policy allows only listed endpoints";
        if (refused is not null)
        {
            embeddings.Provider = EmbeddingProviderKinds.Hashing;
            changes.Add($"The {provider} embeddings are not used ({refused}): routing uses the in-process hashing embeddings.");
        }
        else if (RequireLocalOnly && !embeddings.KeepsPromptsLocal)
        {
            embeddings.Provider = EmbeddingProviderKinds.Hashing;
            changes.Add($"The {provider} embeddings are not allowed by your organization's policy, which allows local models only: routing uses the in-process hashing embeddings.");
        }
    }

    /// <summary>What a configuration change would have to give up to keep within the policy; empty when it keeps within it.</summary>
    public IReadOnlyList<string> Violations(RouterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return IsEmpty ? [] : Enforce(configuration.Clone());
    }

    /// <summary>Webhooks whose (resolved) address the policy does not allow, with why.</summary>
    internal IEnumerable<(string Name, string Reason)> BlockedWebhooks(NotificationOptions notifications)
    {
        foreach ((string name, WebhookOptions hook) in notifications.Webhooks)
        {
            if (!Features.Webhooks)
            {
                yield return (name, "webhooks are turned off by your organization's policy");
            }
            else if (Uri.TryCreate(hook.Url, UriKind.Absolute, out Uri? url) && Refuses(url) is { } refused)
            {
                yield return (name, refused);
            }
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A switch as <c>true</c>/<c>false</c>, a registry DWORD (0 or 1) or the text of either.</summary>
    private sealed class SwitchConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number when reader.TryGetInt64(out long number) && number is 0 or 1 => number == 1,
            JsonTokenType.String when bool.TryParse(reader.GetString(), out bool value) => value,
            JsonTokenType.String when reader.GetString() is "0" or "1" => reader.GetString() == "1",
            _ => throw new JsonException("Expected true or false (or 1 or 0)."),
        };

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
    }

    /// <summary>A host pattern: a name, <c>*.domain</c> or an IP address, with an optional port.</summary>
    private readonly record struct HostPattern(string Host, bool Subdomains, int? Port)
    {
        public static bool TryParse(string text, out HostPattern pattern)
        {
            pattern = default;
            string value = text.Trim();
            bool subdomains = value.StartsWith("*.", StringComparison.Ordinal);
            if (subdomains)
            {
                value = value[2..];
            }

            int? port = null;
            int colon = value.LastIndexOf(':');
            // A port follows a bracketed IPv6 address ([::1]:8080) or the only colon of a name or IPv4 address.
            if (colon > 0 && (value.StartsWith('[') ? value[colon - 1] == ']' : value.Count(c => c == ':') == 1))
            {
                if (!int.TryParse(value[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int p) || p is < 1 or > 65535)
                {
                    return false;
                }

                (value, port) = (value[..colon], p);
            }

            string host = value.Trim('[', ']');
            if (host.Length == 0 || (Uri.CheckHostName(host) == UriHostNameType.Unknown) || (subdomains && IPAddress.TryParse(host, out _)))
            {
                return false;
            }

            pattern = new HostPattern(host, subdomains, port);
            return true;
        }

        public bool Matches(Uri address)
        {
            string host = address.IdnHost.Trim('[', ']');
            bool hostMatches = string.Equals(host, Host, StringComparison.OrdinalIgnoreCase)
                || (Subdomains && host.EndsWith("." + Host, StringComparison.OrdinalIgnoreCase));
            return hostMatches && (Port is null || Port == address.Port);
        }
    }
}

/// <summary>How the machine policy limits the dashboard (<see cref="MachinePolicy.Dashboard"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DashboardPolicy>))]
public enum DashboardPolicy
{
    /// <summary>As configured.</summary>
    Default = 0,

    /// <summary>Everyone gets the read-only role: the router can be watched but not changed from the dashboard or admin API.</summary>
    ReadOnly = 1,

    /// <summary>The dashboard and its API answer 403.</summary>
    Off = 2,
}

/// <summary>Where the desktop app's updates come from, if anywhere (<see cref="UpdatePolicy.Mode"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpdateMode>))]
public enum UpdateMode
{
    /// <summary>From GitHub Releases, as the user's "Check for updates" preference says.</summary>
    Default = 0,

    /// <summary>Never: no update check, and the menu item is hidden.</summary>
    Off = 1,

    /// <summary>Only from <see cref="UpdatePolicy.Feed"/> (an internal mirror), as the user's preference says.</summary>
    Feed = 2,
}

/// <summary>When the desktop app applies an update it found (<see cref="UpdatePolicy.Apply"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpdateApply>))]
public enum UpdateApply
{
    /// <summary>As the user's preference says: by default downloaded in the background and applied when the app quits.</summary>
    Default = 0,

    /// <summary>
    /// Only when the person chooses it: the app says an update is available (in the tray menu and a notification), and
    /// downloads and applies it on "Restart to update"; never when it quits. The user cannot turn this back to automatic.
    /// </summary>
    Notify = 1,
}

/// <summary>Desktop app updates (<see cref="MachinePolicy.Updates"/>).</summary>
public sealed class UpdatePolicy
{
    /// <summary><c>Default</c>, <c>Off</c> or <c>Feed</c>.</summary>
    public UpdateMode Mode { get; set; }

    /// <summary>For <see cref="UpdateMode.Feed"/>: the update feed (a URL or a folder of Velopack packages).</summary>
    public string? Feed { get; set; }

    /// <summary><c>Default</c> (the user's preference) or <c>Notify</c> (only when the person chooses to), with any <see cref="Mode"/>.</summary>
    public UpdateApply Apply { get; set; }

    /// <summary>
    /// When set, an update is applied only when the app in it is signed by this publisher: on Windows the Authenticode
    /// signer's certificate subject (<c>CN=…, O=…</c>), its common name, or its SHA-1 or SHA-256 thumbprint; on macOS the
    /// Developer ID team ID. Linux packages are not signed, so there every update is refused while this is set.
    /// </summary>
    public string? Publisher { get; set; }

    /// <summary>
    /// Whether the <c>SEMANTICROUTER_UPDATE_SOURCE</c> environment variable may point the desktop app at another feed
    /// (to try a build before it is released). A release build ignores the variable unless this is set; a feed in
    /// <see cref="Feed"/> always wins over it.
    /// </summary>
    public bool AllowCustomFeed { get; set; }

    internal bool IsDefault => Mode == UpdateMode.Default && Apply == UpdateApply.Default && Publisher is null && !AllowCustomFeed;

    /// <summary>Why <paramref name="publisher"/> cannot name a publisher, or <see langword="null"/> when it can.</summary>
    internal static string? PublisherError(string publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return "Updates:Publisher must name the publisher (a certificate subject, common name or thumbprint, or a team ID), or be left out.";
        }

        if (publisher.Contains('=', StringComparison.Ordinal))
        {
            try
            {
                _ = new System.Security.Cryptography.X509Certificates.X500DistinguishedName(publisher);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return $"Updates:Publisher '{publisher}' is not a certificate subject such as 'CN=Contoso Ltd, O=Contoso Ltd, C=US'.";
            }
        }

        return null;
    }
}

/// <summary>
/// Features the machine policy can switch off (<see cref="MachinePolicy.Features"/>). Each is on unless set to
/// <see langword="false"/>; the policy cannot switch on what the user's configuration leaves off.
/// </summary>
public sealed class FeaturePolicy
{
    /// <summary>Memory sources (notes read from disk to recognise shared memory). <see langword="false"/>: none are read.</summary>
    public bool Memory { get; set; } = true;

    /// <summary>The judge, which sends a sample of answers to another model. <see langword="false"/>: off.</summary>
    public bool Judge { get; set; } = true;

    /// <summary>Trials of cheaper models per route. <see langword="false"/>: off.</summary>
    public bool ModelTrials { get; set; } = true;

    /// <summary>Automatic tuning applying its own changes. <see langword="false"/>: it only suggests them.</summary>
    public bool TuningApply { get; set; } = true;

    /// <summary>Webhooks for the digest and notices. <see langword="false"/>: none are sent.</summary>
    public bool Webhooks { get; set; } = true;

    internal bool IsDefault => Memory && Judge && ModelTrials && TuningApply && Webhooks;

    internal void Enforce(RouterConfiguration configuration, List<string> changes, MachinePolicy policy)
    {
        if (!Memory)
        {
            foreach (MemorySourceOptions source in configuration.Memory.Sources.Where(s => s.Enabled))
            {
                source.Enabled = false;
                changes.Add($"Memory source '{source.Name}' is turned off: your organization's policy.");
            }
        }

        if (!Judge && configuration.Middleware.Judge.Enabled)
        {
            configuration.Middleware.Judge.Enabled = false;
            changes.Add("The judge is turned off: your organization's policy.");
        }

        if (!ModelTrials && configuration.Tuning.ModelTrialRate > 0)
        {
            configuration.Tuning.ModelTrialRate = 0;
            changes.Add("Model trials are turned off: your organization's policy.");
        }

        if (!TuningApply && configuration.Tuning.Mode == TuningMode.Apply)
        {
            configuration.Tuning.Mode = TuningMode.Suggest;
            changes.Add("Automatic tuning only suggests changes: your organization's policy.");
        }

        // Webhook addresses are often secret references, so the allowed endpoints are checked once they are resolved;
        // here only a literal address can be.
        foreach ((string name, string reason) in policy.BlockedWebhooks(configuration.Notifications).ToArray())
        {
            configuration.Notifications.Webhooks.Remove(name);
            changes.Add($"Webhook '{name}' is removed: {reason}.");
        }
    }
}
