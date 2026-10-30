using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SemanticRouter.AspNetCore;

/// <summary>A dashboard theme, as <c>GET api/themes</c> lists it.</summary>
/// <param name="Id">The <c>data-theme</c> value.</param>
/// <param name="Name">The name the picker shows.</param>
/// <param name="Mode"><c>dark</c> or <c>light</c> (the theme's <c>color-scheme</c>).</param>
/// <param name="BuiltIn">Shipped with the dashboard, as opposed to a file in the themes directory.</param>
/// <param name="MissingTokens">Required tokens the theme does not declare; Machine Dark's values are used for them.</param>
/// <param name="ContrastIssues">Text and surface pairs below WCAG AA (4.5:1).</param>
/// <param name="Problem">Why the theme file cannot be used as is, if it cannot.</param>
internal sealed record DashboardTheme(
    string Id, string Name, string Mode, bool BuiltIn, IReadOnlyList<string> MissingTokens, IReadOnlyList<ThemeContrastIssue> ContrastIssues, string? Problem = null);

/// <summary>A text colour that is too faint on a surface.</summary>
/// <param name="Foreground">The text token.</param>
/// <param name="Background">The surface it is drawn on.</param>
/// <param name="Ratio">The WCAG contrast ratio (AA needs 4.5).</param>
internal sealed record ThemeContrastIssue(string Foreground, string Background, double Ratio);

/// <summary>
/// The dashboard's themes: the built-in token blocks of <c>app.css</c>, plus one <c>{id}.css</c> file per theme in the
/// themes directory (with an optional <c>{id}.json</c> manifest: <c>{ "name": "Ocean" }</c>). Files are read on every
/// listing, so a theme dropped into the directory appears after a reload, without a restart or rebuild.
/// </summary>
internal sealed partial class ThemeCatalog(string? directory)
{
    /// <summary>The built-in themes, the first being the default and the fallback for missing tokens.</summary>
    public static readonly IReadOnlyList<(string Id, string Name)> BuiltIns = [("machine-dark", "Machine Dark"), ("machine-light", "Machine Light"), ("terminal", "Terminal")];

    /// <summary>The text tokens checked against the surfaces they are drawn on.</summary>
    private static readonly string[] TextTokens = ["--text", "--text-muted", "--text-faint", "--accent-ink", "--signal-ink", "--warn", "--danger", "--ok", "--info"];
    private static readonly string[] Surfaces = ["--bg", "--bg-elevated", "--bg-sunken", "--bg-hover"];

    private static readonly Lazy<string> Stylesheet = new(() =>
    {
        using Stream stream = typeof(ThemeCatalog).Assembly.GetManifestResourceStream("dashboard/app.css")
                              ?? throw new InvalidOperationException("The dashboard stylesheet is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>The directory theme files are read from, or <see langword="null"/> when there is none.</summary>
    public string? Directory { get; } = directory;

    /// <summary>The tokens every theme should declare: the default theme's, except fonts and radii (inherited).</summary>
    public static IReadOnlyList<string> RequiredTokens { get; } = Tokens(Stylesheet.Value, BuiltIns[0].Id).Keys
        .Where(t => !t.StartsWith("--font-", StringComparison.Ordinal) && !t.StartsWith("--radius", StringComparison.Ordinal))
        .ToArray();

    /// <summary>Whether <paramref name="id"/> can name a theme file.</summary>
    public static bool IsValidId(string id) => ThemeId().IsMatch(id);

    /// <summary>The built-in themes and every theme file, checked.</summary>
    public IReadOnlyList<DashboardTheme> List()
    {
        var themes = BuiltIns.Select(b => Check(b.Id, b.Name, Stylesheet.Value, builtIn: true)).ToList();
        if (Directory is null || !System.IO.Directory.Exists(Directory))
        {
            return themes;
        }

        foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*.css").Order(StringComparer.Ordinal))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            if (!IsValidId(id) || BuiltIns.Any(b => b.Id == id))
            {
                continue;
            }

            string css = ReadText(file) ?? string.Empty;
            themes.Add(Check(id, ManifestName(id) ?? Title(id), css, builtIn: false));
        }

        return themes;
    }

    /// <summary>The stylesheet of a theme file, or <see langword="null"/> when there is no such file.</summary>
    public string? ReadCss(string id) =>
        Directory is not null && IsValidId(id) && BuiltIns.All(b => b.Id != id) ? ReadText(Path.Combine(Directory, id + ".css")) : null;

    /// <summary>Checks one theme's tokens: completeness and contrast (missing tokens fall back to the default theme's).</summary>
    internal static DashboardTheme Check(string id, string name, string css, bool builtIn)
    {
        Dictionary<string, string> own = Tokens(css, id);
        string? problem = own.Count == 0 ? $"declares no tokens for :root[data-theme=\"{id}\"]" : null;
        var merged = new Dictionary<string, string>(Tokens(Stylesheet.Value, BuiltIns[0].Id), StringComparer.Ordinal);
        foreach ((string token, string value) in own)
        {
            merged[token] = value;
        }

        string mode = own.TryGetValue("color-scheme", out string? scheme) && scheme.Contains("light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        string[] missing = builtIn ? [] : RequiredTokens.Where(t => !own.ContainsKey(t)).ToArray();
        return new DashboardTheme(id, name, mode, builtIn, missing, CheckContrast(merged), problem);
    }

    /// <summary>Text and surface pairs below 4.5:1, including status colours on their tinted badge fills.</summary>
    internal static IReadOnlyList<ThemeContrastIssue> CheckContrast(IReadOnlyDictionary<string, string> tokens)
    {
        var pairs = new List<(string Text, string Surface, string? Fill)>();
        foreach (string text in TextTokens)
        {
            pairs.AddRange(Surfaces.Select(s => (text, s, (string?)null)));
        }

        // Badges sit on every surface: cards, the sunken setup choices and table headers, and hovered rows.
        foreach (string status in (string[])["--ok", "--warn", "--danger", "--info"])
        {
            pairs.AddRange(Surfaces.Select(surface => (status, surface, (string?)(status + "-soft"))));
        }
        pairs.AddRange([("--accent-contrast", "--accent", null), ("--signal-contrast", "--signal", null)]);

        var issues = new List<ThemeContrastIssue>();
        foreach ((string text, string surface, string? fill) in pairs)
        {
            string textToken = text == "--signal-ink" && !tokens.ContainsKey(text) ? "--signal" : text;
            if (!tokens.TryGetValue(textToken, out string? fg) || !tokens.TryGetValue(surface, out string? bg)
                || ParseColor(fg) is not { } foreground || ParseColor(bg) is not { } background)
            {
                continue;
            }

            if (fill is not null)
            {
                if (!tokens.TryGetValue(fill, out string? tint) || ParseColor(tint) is not { } overlay)
                {
                    continue;
                }

                background = Blend(overlay, background);
            }

            double ratio = Contrast(foreground, background);
            if (ratio < 4.5)
            {
                issues.Add(new ThemeContrastIssue(text, fill ?? surface, Math.Round(ratio, 2)));
            }
        }

        return issues;
    }

    /// <summary>The WCAG 2 contrast ratio of two opaque colours.</summary>
    internal static double Contrast((double R, double G, double B, double A) a, (double R, double G, double B, double A) b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>The custom properties (and <c>color-scheme</c>) declared in <c>:root[data-theme="id"] { … }</c>.</summary>
    internal static Dictionary<string, string> Tokens(string css, string id)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        string stripped = Comment().Replace(css, string.Empty);
        foreach (Match block in Block().Matches(stripped))
        {
            if (!block.Groups["selectors"].Value.Contains($"[data-theme=\"{id}\"]", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match declaration in Declaration().Matches(block.Groups["body"].Value))
            {
                tokens[declaration.Groups["name"].Value] = declaration.Groups["value"].Value.Trim();
            }
        }

        return tokens;
    }

    /// <summary>A <c>#rgb</c>, <c>#rrggbb</c>, <c>rgb()</c> or <c>rgba()</c> colour; <see langword="null"/> for anything else.</summary>
    internal static (double R, double G, double B, double A)? ParseColor(string value)
    {
        value = value.Trim();
        if (HexColor().Match(value) is { Success: true } hex)
        {
            string digits = hex.Groups[1].Value;
            if (digits.Length == 3)
            {
                digits = string.Concat(digits.Select(c => new string(c, 2)));
            }

            return (Channel(digits, 0), Channel(digits, 2), Channel(digits, 4), 1);
        }

        if (RgbColor().Match(value) is { Success: true } rgb)
        {
            double Part(int i) => double.Parse(rgb.Groups[i].Value, CultureInfo.InvariantCulture);
            double alpha = rgb.Groups[4].Success ? Part(4) : 1;
            return (Part(1) / 255, Part(2) / 255, Part(3) / 255, alpha);
        }

        return null;
    }

    private static (double R, double G, double B, double A) Blend((double R, double G, double B, double A) top, (double R, double G, double B, double A) bottom) =>
        (top.A * top.R + (1 - top.A) * bottom.R, top.A * top.G + (1 - top.A) * bottom.G, top.A * top.B + (1 - top.A) * bottom.B, 1);

    private static double Luminance((double R, double G, double B, double A) c)
    {
        static double Linear(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return (0.2126 * Linear(c.R)) + (0.7152 * Linear(c.G)) + (0.0722 * Linear(c.B));
    }

    private static double Channel(string hex, int offset) => int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;

    private string? ManifestName(string id)
    {
        string? json = ReadText(Path.Combine(Directory!, id + ".json"));
        if (json is null)
        {
            return null;
        }

        try
        {
            using JsonDocument manifest = JsonDocument.Parse(json);
            return manifest.RootElement.ValueKind == JsonValueKind.Object && manifest.RootElement.TryGetProperty("name", out JsonElement name)
                   && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } text
                ? text
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Title(string id) =>
        string.Join(' ', id.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex ThemeId();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"(?<selectors>[^{}]+)\{(?<body>[^{}]*)\}")]
    private static partial Regex Block();

    [GeneratedRegex(@"(?<name>--[\w-]+|color-scheme)\s*:\s*(?<value>[^;]+)(?:;|$)")]
    private static partial Regex Declaration();

    [GeneratedRegex("^#([0-9a-fA-F]{6}|[0-9a-fA-F]{3})$")]
    private static partial Regex HexColor();

    [GeneratedRegex(@"^rgba?\(\s*(\d+(?:\.\d+)?|\.\d+)\s*,\s*(\d+(?:\.\d+)?|\.\d+)\s*,\s*(\d+(?:\.\d+)?|\.\d+)\s*(?:,\s*(\d+(?:\.\d+)?|\.\d+)\s*)?\)$")]
    private static partial Regex RgbColor();
}
