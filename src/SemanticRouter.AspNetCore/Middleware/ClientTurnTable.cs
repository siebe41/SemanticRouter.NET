using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.AspNetCore;

/// <summary>Where the local client-turn patterns are (<c>UseClientTurnFile</c>).</summary>
/// <param name="Path">The file.</param>
internal sealed record ClientTurnFile(string Path);

/// <summary>One pattern of a <see cref="ClientTurnTable"/>: which client says it, what kind of turn it marks, and the wording.</summary>
internal sealed class ClientTurnPattern
{
    /// <summary>The entry's id, e.g. <c>claude-code/title</c>. A local entry with the same id replaces the built-in one.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The client that sends it, for people reading the file.</summary>
    public string? Client { get; set; }

    /// <summary>The kind of turn it marks.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ClientTurnKind>))]
    public ClientTurnKind Kind { get; set; }

    /// <summary>A .NET regular expression, matched case-insensitively.</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary><see langword="false"/> in a local file turns off the built-in entry with the same id.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// The wording <see cref="ClientTurns"/> recognises agentic clients' own requests by. A versioned table is built in
/// (<c>client-turns.json</c>); a <see cref="FileName"/> file in the configuration directory replaces entries by id, adds
/// new ones or turns one off, so a client that changes its wording needs no new build.
/// </summary>
internal sealed class ClientTurnTable
{
    /// <summary>The local table's file name, next to the configuration file.</summary>
    public const string FileName = "client-turns.json";

    /// <summary>The order kinds are tried in: a warm-up and a resumed session first, as before the table existed.</summary>
    private static readonly ClientTurnKind[] Order =
        [ClientTurnKind.Warmup, ClientTurnKind.Resumed, ClientTurnKind.Title, ClientTurnKind.Topic, ClientTurnKind.Summary, ClientTurnKind.CommandCheck];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private ClientTurnTable(int version, IReadOnlyList<(ClientTurnPattern Entry, Regex Pattern)> patterns, string source, string? warning)
    {
        Version = version;
        Patterns = patterns;
        Source = source;
        Warning = warning;
    }

    /// <summary>The built-in table, as shipped.</summary>
    public static ClientTurnTable BuiltIn { get; } = LoadBuiltIn();

    /// <summary>The built-in table's version.</summary>
    public int Version { get; }

    /// <summary>The enabled patterns, in the order they are tried.</summary>
    public IReadOnlyList<(ClientTurnPattern Entry, Regex Pattern)> Patterns { get; }

    /// <summary>Where the table came from: <c>built-in v1</c>, or that plus the local file.</summary>
    public string Source { get; }

    /// <summary>Why the local file was not (fully) used, when it was not.</summary>
    public string? Warning { get; }

    /// <summary>The local table's path next to a configuration file.</summary>
    public static string FileNextTo(string configurationFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationFile);
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configurationFile))!, FileName);
    }

    /// <summary>The built-in table with the local file's entries on top, when <paramref name="localFile"/> exists.</summary>
    public static ClientTurnTable Load(string? localFile)
    {
        if (localFile is null || !File.Exists(localFile))
        {
            return BuiltIn;
        }

        try
        {
            (int _, List<(ClientTurnPattern Entry, Regex? Pattern)> local, List<string> problems) = Parse(File.ReadAllText(localFile));
            var ids = new HashSet<string>(local.Select(l => l.Entry.Id), StringComparer.OrdinalIgnoreCase);
            IEnumerable<(ClientTurnPattern, Regex)> merged = local
                .Where(l => l.Entry.Enabled && l.Pattern is not null)
                .Select(l => (l.Entry, l.Pattern!))
                .Concat(BuiltIn.Patterns.Where(b => !ids.Contains(b.Entry.Id)));
            return new ClientTurnTable(
                BuiltIn.Version,
                Ordered(merged),
                $"{BuiltIn.Source} + {localFile} ({local.Count} {(local.Count == 1 ? "entry" : "entries")})",
                problems.Count > 0 ? $"{localFile}: {string.Join("; ", problems)}" : null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ClientTurnTable(BuiltIn.Version, BuiltIn.Patterns, BuiltIn.Source, $"{localFile} could not be read ({ex.Message}); using the built-in patterns.");
        }
    }

    private static ClientTurnTable LoadBuiltIn()
    {
        using Stream stream = typeof(ClientTurnTable).Assembly.GetManifestResourceStream("SemanticRouter.AspNetCore.client-turns.json")
            ?? throw new InvalidOperationException("The built-in client-turn patterns are missing.");
        using var reader = new StreamReader(stream);
        (int version, List<(ClientTurnPattern Entry, Regex? Pattern)> entries, List<string> problems) = Parse(reader.ReadToEnd());
        return problems.Count > 0
            ? throw new InvalidOperationException("The built-in client-turn patterns are invalid: " + string.Join("; ", problems))
            : new ClientTurnTable(version, Ordered(entries.Where(e => e.Entry.Enabled).Select(e => (e.Entry, e.Pattern!))), $"built-in v{version}", null);
    }

    private static (ClientTurnPattern, Regex)[] Ordered(IEnumerable<(ClientTurnPattern Entry, Regex Pattern)> patterns) =>
        [.. patterns.OrderBy(p => Array.IndexOf(Order, p.Entry.Kind))];

    /// <summary>The entries with their compiled patterns (<see langword="null"/> for one that is turned off), and what was wrong.</summary>
    private static (int Version, List<(ClientTurnPattern Entry, Regex? Pattern)> Entries, List<string> Problems) Parse(string json)
    {
        TableDocument document = JsonSerializer.Deserialize<TableDocument>(json, Json) ?? new TableDocument();
        var entries = new List<(ClientTurnPattern, Regex?)>();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ClientTurnPattern? entry in document.Turns ?? [])
        {
            if (entry is null)
            {
                problems.Add("an empty entry; skipped");
                continue;
            }

            string id = string.IsNullOrWhiteSpace(entry.Id) ? "(no id)" : entry.Id;
            if (string.IsNullOrWhiteSpace(entry.Id) || !seen.Add(entry.Id))
            {
                problems.Add($"entry {id} needs an id of its own; skipped");
            }
            else if (!entry.Enabled)
            {
                entries.Add((entry, null));
            }
            else if (!Order.Contains(entry.Kind))
            {
                problems.Add($"entry {id} has kind {entry.Kind}, which must be one of {string.Join(", ", Order)}; skipped");
            }
            else if (Compile(entry.Pattern, out string? error) is { } pattern)
            {
                entries.Add((entry, pattern));
            }
            else
            {
                problems.Add($"entry {id} has an invalid pattern ({error}); skipped");
            }
        }

        return (document.Version, entries, problems);
    }

    private static Regex? Compile(string pattern, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "it is empty";
            return null;
        }

        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private sealed class TableDocument
    {
        public int Version { get; set; }

        public List<ClientTurnPattern> Turns { get; set; } = [];
    }
}

/// <summary>
/// The current <see cref="ClientTurnTable"/>: the local file registered with <c>UseClientTurnFile</c>, else the one next
/// to the configuration file, read again when it changes (checked at most every few seconds), so an edit applies
/// without a restart.
/// </summary>
internal sealed partial class ClientTurnPatterns
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);

    private readonly string? _file;
    private readonly TimeProvider _time;
    private readonly ILogger<ClientTurnPatterns> _logger;
    private readonly Lock _gate = new();
    private ClientTurnTable _table = ClientTurnTable.BuiltIn;
    private DateTime? _loadedStamp;
    private DateTimeOffset _checked = DateTimeOffset.MinValue;

    public ClientTurnPatterns(ILogger<ClientTurnPatterns> logger, ClientTurnFile? file = null, IRouterConfigurationStore? store = null, TimeProvider? time = null)
    {
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _file = file?.Path ?? (store is JsonFileRouterConfigurationStore json ? ClientTurnTable.FileNextTo(json.Location) : null);
    }

    /// <summary>The table to recognise client turns with.</summary>
    public ClientTurnTable Current
    {
        get
        {
            if (_file is null)
            {
                return ClientTurnTable.BuiltIn;
            }

            DateTimeOffset now = _time.GetUtcNow();
            lock (_gate)
            {
                if (now - _checked < CheckEvery)
                {
                    return _table;
                }

                _checked = now;
                DateTime? stamp = File.Exists(_file) ? File.GetLastWriteTimeUtc(_file) : null;
                if (stamp != _loadedStamp)
                {
                    _loadedStamp = stamp;
                    _table = ClientTurnTable.Load(_file);
                    if (_table.Warning is { } warning)
                    {
                        LogWarning(_logger, warning);
                    }
                    else
                    {
                        LogLoaded(_logger, _table.Source);
                    }
                }

                return _table;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Client-turn patterns: {Source}.")]
    private static partial void LogLoaded(ILogger logger, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client-turn patterns: {Warning}")]
    private static partial void LogWarning(ILogger logger, string warning);
}
