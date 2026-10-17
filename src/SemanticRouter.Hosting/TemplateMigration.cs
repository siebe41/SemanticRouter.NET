using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Host;

/// <summary>What <see cref="TemplateMigration.Upgrade"/> did to a document.</summary>
/// <param name="From">The template the document started from.</param>
/// <param name="Document">The document on the current template, stamped with it.</param>
/// <param name="Changes">What changed, one entry per setting, in the order of the document.</param>
/// <param name="Kept">Settings the template changed that the document had changed too, and so kept as they were.</param>
internal sealed record TemplateUpgrade(int From, RouterConfiguration Document, IReadOnlyList<string> Changes, IReadOnlyList<string> Kept)
{
    /// <summary>The changes and what was kept, for the log and the configuration history.</summary>
    public string Describe()
    {
        string what = Changes.Count == 0 ? "nothing to change" : string.Join("; ", Changes);
        string kept = Kept.Count == 0 ? string.Empty : $". Kept as you had them: {string.Join("; ", Kept)}";
        return $"template {From.ToString(CultureInfo.InvariantCulture)} → {TemplateMigration.Current.ToString(CultureInfo.InvariantCulture)}: {what}{kept}";
    }
}

/// <summary>
/// Brings a configuration that started from an earlier built-in template up to the current one. <c>init</c> and the
/// first start write the whole template into <c>semantic-router.json</c>, so without this, a fix made in the template
/// (a route, an utterance, a model alias) would reach new installs only. Each released template is kept
/// (<c>Templates/Previous</c>); what changed from one to the next is applied to the document wherever the document still
/// has the earlier template's value, and nowhere else: a setting someone changed is kept, even when the template changed
/// it too. Lists of words (utterances, capabilities) combine: what the template added is added, what it dropped is
/// dropped, and the rest stays as it is. The document is stamped with <see cref="RouterConfiguration.TemplateVersion"/>.
/// </summary>
internal static class TemplateMigration
{
    /// <summary>The current template's number, as its <c>TemplateVersion</c> says.</summary>
    public const int Current = 2;

    /// <summary>History source of a start that upgraded the template.</summary>
    public const string HistorySource = "upgrade";

    /// <summary>The template numbered <paramref name="version"/>: 1 is the one of 0.1.0–0.2.1, 2 that of 0.3.0–0.5.0 and later.</summary>
    public static string Template(int version) =>
        version == Current ? RouterHost.Template() : RouterHost.Resource($"semantic-router.template.{version.ToString(CultureInfo.InvariantCulture)}.json");

    /// <summary>The evaluation set <c>init</c> wrote with the template numbered <paramref name="version"/>.</summary>
    public static string EvaluationTemplate(int version) =>
        version == Current ? RouterHost.EvaluationTemplate() : RouterHost.Resource($"semantic-router.evaluation.{version.ToString(CultureInfo.InvariantCulture)}.jsonl");

    /// <summary>
    /// The template a document started from: its <see cref="RouterConfiguration.TemplateVersion"/>, or, for a document
    /// written before templates were numbered (0.5.0 and older), the template it differs from least, the newer one on a
    /// tie. <see langword="null"/> for an unstamped document that did not come from a template: one that keeps fewer than
    /// half of that template's routes was written by hand (or from an application's own settings), and a template's new
    /// routes and aliases are not for it.
    /// </summary>
    public static int? Origin(RouterConfiguration document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.TemplateVersion is int version)
        {
            return version;
        }

        JsonObject mine = Normalize(document);
        int closest = Enumerable.Range(1, Current)
            .Select(v => (Version: v, Distance: Distance(mine, Normalize(Template(v)))))
            .OrderBy(c => c.Distance)
            .ThenByDescending(c => c.Version)
            .First().Version;
        string[] routes = [.. RouterConfiguration.FromJson(Template(closest)).Routes.Select(r => r.Name)];
        int kept = routes.Count(name => document.Routes.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)));
        return kept * 2 >= routes.Length ? closest : null;
    }

    /// <summary>
    /// The document brought up to the current template, or <see langword="null"/> when it is stamped with the current
    /// template already, or with a newer one (written by a newer release: it is left alone), or did not come from a
    /// template at all (<see cref="Origin"/>). An unstamped document that needs no change still gets the stamp.
    /// </summary>
    public static TemplateUpgrade? Upgrade(RouterConfiguration document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.TemplateVersion >= Current)
        {
            return null;
        }

        if (Origin(document) is not int origin)
        {
            return null;
        }

        int from = Math.Clamp(origin, 1, Current);
        JsonObject mine = Normalize(document);
        var changes = new List<string>();
        var kept = new List<string>();
        for (int version = from; version < Current; version++)
        {
            MergeObject(mine, Normalize(Template(version)), Normalize(Template(version + 1)), string.Empty, changes, kept);
        }

        RouterConfiguration upgraded = RouterConfiguration.FromJson(mine.ToJsonString());
        upgraded.TemplateVersion = Current;
        return new TemplateUpgrade(from, upgraded, changes, kept);
    }

    /// <summary>
    /// Upgrades the configuration file and the evaluation set next to it, as the router does on every start. Returns what
    /// changed, for the configuration history, or <see langword="null"/> when nothing did. A file that cannot be read or
    /// parsed is left for the router to report; one whose upgrade would not validate is left as it is, and said so.
    /// </summary>
    public static async Task<string?> UpgradeFilesAsync(string configurationFile, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        string? evaluation = UpgradeEvaluationSet(SemanticRouterServiceCollectionExtensions.EvaluationSetFileNextTo(configurationFile));
        if (evaluation is not null)
        {
            await output.WriteLineAsync(evaluation).ConfigureAwait(false);
        }

        var store = new JsonFileRouterConfigurationStore(configurationFile);
        RouterConfiguration? stored;
        try
        {
            stored = store.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }

        if (stored is null || Upgrade(stored) is not { } upgrade)
        {
            return null;
        }

        IReadOnlyList<string> errors = upgrade.Document.GetDocumentValidationErrors();
        if (errors.Count > 0)
        {
            await output.WriteLineAsync($"{configurationFile} was not brought up to template {Current} (it would not be valid: {string.Join("; ", errors)}).").ConfigureAwait(false);
            return null;
        }

        try
        {
            store.Save(upgrade.Document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"{configurationFile} could not be brought up to template {Current}: {ex.Message}").ConfigureAwait(false);
            return null;
        }

        if (upgrade.Changes.Count == 0 && upgrade.Kept.Count == 0)
        {
            // Only the stamp was added: nothing to tell anyone.
            return null;
        }

        string note = upgrade.Describe();
        await output.WriteLineAsync($"Updated {configurationFile} to the current template ({note}).").ConfigureAwait(false);
        return note;
    }

    /// <summary>
    /// Replaces an evaluation set that is still exactly one an earlier <c>init</c> wrote with the current one, and says
    /// so; one with any prompt added, changed or removed is yours, and stays. Returns the line to print, or
    /// <see langword="null"/>.
    /// </summary>
    public static string? UpgradeEvaluationSet(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            string mine = Lines(File.ReadAllText(file));
            for (int version = 1; version < Current; version++)
            {
                if (mine == Lines(EvaluationTemplate(version)))
                {
                    File.WriteAllText(file, EvaluationTemplate(Current));
                    OwnerOnly.Restrict(file);
                    return $"Updated {file} to the current sample prompts (it held the ones of template {version.ToString(CultureInfo.InvariantCulture)}).";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left as it is: the Evaluate page reads whatever is there.
        }

        return null;

        static string Lines(string text) => text.ReplaceLineEndings("\n").TrimEnd();
    }

    /// <summary>
    /// Whether <paramref name="document"/> holds a released template exactly (apart from its stamp), so <c>init</c> may
    /// replace it. With <paramref name="adjust"/>, also when it holds one as that function changes it.
    /// </summary>
    public static bool IsTemplate(RouterConfiguration document, Action<RouterConfiguration>? adjust = null)
    {
        JsonObject mine = Normalize(document);
        for (int version = 1; version <= Current; version++)
        {
            RouterConfiguration template = RouterConfiguration.FromJson(Template(version));
            if (JsonNode.DeepEquals(mine, Normalize(template)))
            {
                return true;
            }

            if (adjust is not null)
            {
                adjust(template);
                if (JsonNode.DeepEquals(mine, Normalize(template)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The document as JSON, every setting written out, without its stamp.</summary>
    private static JsonObject Normalize(RouterConfiguration document)
    {
        JsonObject json = JsonNode.Parse(document.ToJson())!.AsObject();
        json.Remove(nameof(RouterConfiguration.TemplateVersion));
        return json;
    }

    private static JsonObject Normalize(string template) => Normalize(RouterConfiguration.FromJson(template));

    /// <summary>The properties that name an item in a list of objects (routes by name, tiers by level), in that order.</summary>
    private static readonly string[] ItemKeys = ["Name", "Level", "Tier"];

    /// <summary>
    /// Applies what the template changed from <paramref name="old"/> to <paramref name="new"/> to <paramref name="mine"/>,
    /// where <paramref name="mine"/> still has the old value. A property the template added goes where the template has
    /// it, since order can matter (the first model alias that matches wins).
    /// </summary>
    private static void MergeObject(JsonObject mine, JsonObject old, JsonObject @new, string path, List<string> changes, List<string> kept)
    {
        foreach ((string key, JsonNode? before) in old.ToArray())
        {
            if (@new.ContainsKey(key) || !mine.TryGetPropertyValue(key, out JsonNode? current))
            {
                continue;
            }

            if (JsonNode.DeepEquals(current, before))
            {
                mine.Remove(key);
                changes.Add($"{Join(path, key)} removed");
            }
            else
            {
                kept.Add($"{Join(path, key)} (the template no longer has it)");
            }
        }

        string[] order = [.. @new.Select(p => p.Key)];
        for (int i = 0; i < order.Length; i++)
        {
            string key = order[i];
            JsonNode? after = @new[key];
            if (!old.TryGetPropertyValue(key, out JsonNode? before))
            {
                if (!mine.ContainsKey(key))
                {
                    int at = order.Skip(i + 1).Select(mine.IndexOf).FirstOrDefault(index => index >= 0, mine.Count);
                    mine.Insert(at, key, after?.DeepClone());
                    changes.Add($"{Join(path, key)} added");
                }
                else if (!JsonNode.DeepEquals(mine[key], after))
                {
                    kept.Add($"{Join(path, key)} (the template now adds {Show(after)})");
                }

                continue;
            }

            if (JsonNode.DeepEquals(before, after) || !mine.TryGetPropertyValue(key, out JsonNode? current))
            {
                // Unchanged by the template, or removed by you.
                continue;
            }

            JsonNode? merged = Merge(current, before, after, Join(path, key), changes, kept);
            if (!ReferenceEquals(merged, current))
            {
                mine[key] = merged;
            }
        }
    }

    /// <summary>One value: the template's new one where it is still the old one, merged within objects and lists.</summary>
    private static JsonNode? Merge(JsonNode? mine, JsonNode? old, JsonNode? @new, string path, List<string> changes, List<string> kept)
    {
        if (JsonNode.DeepEquals(mine, @new))
        {
            return mine;
        }

        if (mine is JsonObject mineObject && old is JsonObject oldObject && @new is JsonObject newObject)
        {
            MergeObject(mineObject, oldObject, newObject, path, changes, kept);
            return mine;
        }

        if (mine is JsonArray mineArray && old is JsonArray oldArray && @new is JsonArray newArray)
        {
            if (ItemKey(mineArray, oldArray, newArray) is { } itemKey)
            {
                MergeItems(mineArray, oldArray, newArray, itemKey, path, changes, kept);
                return mine;
            }

            if (Words(mineArray) is { } words && Words(oldArray) is { } before && Words(newArray) is { } after)
            {
                return MergeWords(words, before, after, path, changes);
            }
        }

        if (JsonNode.DeepEquals(mine, old))
        {
            changes.Add($"{path}: {Show(old)} → {Show(@new)}");
            return @new?.DeepClone();
        }

        kept.Add($"{path} (the template changed it from {Show(old)} to {Show(@new)})");
        return mine;
    }

    /// <summary>A list of named items (routes, tiers): each item merged with the template's item of the same name.</summary>
    private static void MergeItems(JsonArray mine, JsonArray old, JsonArray @new, string itemKey, string path, List<string> changes, List<string> kept)
    {
        foreach (JsonNode? before in old)
        {
            string name = KeyOf(before, itemKey)!;
            if (Find(@new, itemKey, name) is not null || Find(mine, itemKey, name) is not { } current)
            {
                continue;
            }

            if (JsonNode.DeepEquals(current, before))
            {
                mine.Remove(current);
                changes.Add($"{path}[{name}] removed");
            }
            else
            {
                kept.Add($"{path}[{name}] (the template no longer has it)");
            }
        }

        for (int i = 0; i < @new.Count; i++)
        {
            JsonNode after = @new[i]!;
            string name = KeyOf(after, itemKey)!;
            JsonNode? current = Find(mine, itemKey, name);
            if (Find(old, itemKey, name) is not { } before)
            {
                if (current is null)
                {
                    int at = @new.Skip(i + 1).Select(n => Find(mine, itemKey, KeyOf(n, itemKey)!)).Where(n => n is not null).Select(n => mine.IndexOf(n)).FirstOrDefault(index => index >= 0, mine.Count);
                    mine.Insert(at, after.DeepClone());
                    changes.Add($"{path}[{name}] added");
                }
                else if (!JsonNode.DeepEquals(current, after))
                {
                    kept.Add($"{path}[{name}] (the template now adds one of its own)");
                }

                continue;
            }

            if (current is not null && !JsonNode.DeepEquals(before, after))
            {
                JsonNode? merged = Merge(current, before, after, $"{path}[{name}]", changes, kept);
                if (!ReferenceEquals(merged, current))
                {
                    mine[mine.IndexOf(current)] = merged;
                }
            }
        }
    }

    /// <summary>A list of words, as a set: what the template added is added (at the end), what it dropped is dropped.</summary>
    private static JsonArray MergeWords(List<string> mine, List<string> old, List<string> @new, string path, List<string> changes)
    {
        string[] dropped = [.. old.Except(@new, StringComparer.Ordinal).Where(w => mine.Contains(w, StringComparer.Ordinal))];
        string[] added = [.. @new.Except(old, StringComparer.Ordinal).Where(w => !mine.Contains(w, StringComparer.Ordinal))];
        var result = new JsonArray();
        foreach (string word in mine.Where(w => !dropped.Contains(w, StringComparer.Ordinal)).Concat(added))
        {
            result.Add(word);
        }

        if (added.Length > 0 || dropped.Length > 0)
        {
            changes.Add($"{path}: {string.Join(", ", new[] { added.Length > 0 ? $"{added.Length} added" : null, dropped.Length > 0 ? $"{dropped.Length} removed" : null }.Where(p => p is not null))}");
        }

        return result;
    }

    /// <summary>The property that names every item of all three lists, uniquely in each; <see langword="null"/> when none does.</summary>
    private static string? ItemKey(params JsonArray[] lists) =>
        ItemKeys.FirstOrDefault(key => lists.All(list =>
            list.All(item => KeyOf(item, key) is not null) && list.Select(item => KeyOf(item, key)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == list.Count));

    private static string? KeyOf(JsonNode? item, string key) =>
        item is JsonObject o && o.TryGetPropertyValue(key, out JsonNode? value) && value is JsonValue v
            ? v.GetValueKind() switch
            {
                JsonValueKind.String => v.GetValue<string>(),
                JsonValueKind.Number => v.ToJsonString(),
                _ => null,
            }
            : null;

    private static JsonNode? Find(JsonArray list, string key, string name) =>
        list.FirstOrDefault(item => string.Equals(KeyOf(item, key), name, StringComparison.OrdinalIgnoreCase));

    private static List<string>? Words(JsonArray list) =>
        list.All(item => item is JsonValue v && v.GetValueKind() == JsonValueKind.String) ? [.. list.Select(item => item!.GetValue<string>())] : null;

    /// <summary>How far apart two documents are: the number of settings, list items and words that differ.</summary>
    private static int Distance(JsonNode? a, JsonNode? b)
    {
        if (JsonNode.DeepEquals(a, b))
        {
            return 0;
        }

        if (a is JsonObject x && b is JsonObject y)
        {
            return x.Select(p => p.Key).Union(y.Select(p => p.Key)).Sum(key => Distance(x[key], y[key]));
        }

        if (a is JsonArray first && b is JsonArray second)
        {
            if (ItemKey(first, second) is { } key)
            {
                return first.Select(n => KeyOf(n, key)!).Union(second.Select(n => KeyOf(n, key)!), StringComparer.OrdinalIgnoreCase)
                    .Sum(name => Distance(Find(first, key, name), Find(second, key, name)));
            }

            if (Words(first) is { } one && Words(second) is { } other)
            {
                return one.Except(other, StringComparer.Ordinal).Count() + other.Except(one, StringComparer.Ordinal).Count();
            }
        }

        return Math.Max(Size(a), Size(b));

        static int Size(JsonNode? node) => node switch
        {
            JsonObject o => Math.Max(1, o.Sum(p => Size(p.Value))),
            JsonArray list => Math.Max(1, list.Sum(Size)),
            _ => 1,
        };
    }

    private static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";

    private static string Show(JsonNode? value) => value is null ? "null" : value.ToJsonString();
}
