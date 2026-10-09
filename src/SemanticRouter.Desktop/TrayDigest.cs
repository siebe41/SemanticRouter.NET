using System.Globalization;
using System.Text.Json;

namespace SemanticRouter.Desktop;

/// <summary>One notice from the router's digest: something a person needs to decide.</summary>
internal sealed record TrayNotice(string Key, string Title, string Detail, string Link);

/// <summary>
/// The router's digest (<c>GET /router/api/digest</c>) as the tray menu shows it: a line for the week, how many things
/// it learned, and what needs a decision.
/// </summary>
internal sealed record TrayDigest(string? Week, int Learned, IReadOnlyList<TrayNotice> Notices, bool Notify)
{
    /// <summary>Nothing to show (an older router, or not reachable).</summary>
    public static TrayDigest None { get; } = new(null, 0, [], false);

    /// <summary>Reads the digest; what is missing is left out.</summary>
    public static TrayDigest Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        int prompts = Int(root, "prompts");
        string? week = null;
        if (prompts > 0)
        {
            var parts = new List<string> { string.Format(CultureInfo.CurrentCulture, "{0:N0} prompt{1}", prompts, prompts == 1 ? string.Empty : "s") };
            if (Int(root, "served") > 0 && root.TryGetProperty("localShare", out JsonElement share) && share.TryGetDouble(out double local))
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture, "{0:P0} local", local));
            }

            if (root.TryGetProperty("referenceModel", out JsonElement reference) && reference.ValueKind == JsonValueKind.String
                && root.TryGetProperty("savedUsd", out JsonElement saved) && saved.TryGetDecimal(out decimal usd) && usd > 0)
            {
                parts.Add(string.Format(CultureInfo.InvariantCulture, "saved ${0:0.00}", usd));
            }

            week = "This week: " + string.Join(" · ", parts);
        }

        int learned = root.TryGetProperty("learned", out JsonElement changes) && changes.ValueKind == JsonValueKind.Array
            ? changes.EnumerateArray().Count(c => Str(c, "state") == "Applied")
            : 0;
        var notices = new List<TrayNotice>();
        if (root.TryGetProperty("notices", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement notice in list.EnumerateArray())
            {
                if (Str(notice, "key") is { } key && Str(notice, "title") is { } title)
                {
                    notices.Add(new TrayNotice(key, title, Str(notice, "detail") ?? string.Empty, Str(notice, "link") ?? "#/overview"));
                }
            }
        }

        return new TrayDigest(week, learned, notices, root.TryGetProperty("notify", out JsonElement notify) && notify.ValueKind == JsonValueKind.True);
    }

    /// <summary>The menu line for what needs a decision, or <see langword="null"/> when nothing does.</summary>
    public string? NeedsLine => Notices.Count switch
    {
        0 => null,
        1 => "Needs a decision: " + Notices[0].Title,
        _ => $"{Notices.Count} things need a decision",
    };

    /// <summary>The page the decision line opens.</summary>
    public string NeedsLink => Notices.Count == 1 ? Notices[0].Link : "#/overview";

    /// <summary>Notices not among <paramref name="announced"/>: to show as notifications, when <see cref="Notify"/> is on.</summary>
    public IReadOnlyList<TrayNotice> New(IReadOnlySet<string> announced) => Notify ? [.. Notices.Where(n => !announced.Contains(n.Key))] : [];

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int number) ? number : 0;

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
