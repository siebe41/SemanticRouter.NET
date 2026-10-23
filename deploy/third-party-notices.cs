// Writes THIRD-PARTY-NOTICES.txt for what a build ships: every NuGet package (and .NET runtime pack) named in the
// *.deps.json files given, with its licence, copyright and project page from the package's .nuspec, and the components
// no deps.json names (the embedded fonts, Velopack's native binaries) from deploy/third-party/extra.json, and the Rust
// crates linked into those binaries from deploy/third-party/velopack-crates.json. The licence
// and notice files the packages carry follow, each distinct text once, and the standard text of every licence an entry
// has no text of its own for (deploy/third-party/licences, from the SPDX License List). A .NET 10 file-based app, so it
// runs wherever the SDK does (the release jobs on every OS and the container build) with nothing to install:
//
//   dotnet run --file deploy/third-party-notices.cs -- <output-file> <deps.json or directory>...
//   dotnet run --file deploy/third-party-notices.cs -- publish/THIRD-PARTY-NOTICES.txt publish
//
// A directory is searched for *.deps.json. Package metadata is read from the NuGet global packages folder that restore
// filled (NUGET_PACKAGES, or ~/.nuget/packages), so it makes no network call. A package missing from it, a package
// without an SPDX licence and a licence without a text are errors, not silent gaps. The output depends only on the
// packages and the files in this repository: same inputs, same bytes.
#:property PublishAot=false

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: dotnet run --file deploy/third-party-notices.cs -- <output-file> <deps.json or directory>...");
    return 2;
}

var output = Path.GetFullPath(args[0]);
var packagesFolder = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
    ? configured
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

// This file's directory (deploy/), which the SDK passes to a file-based app; the repository root is its parent.
if (AppContext.GetData("EntryPointFileDirectoryPath") is not string deployDirectory)
{
    Console.Error.WriteLine("Run it with dotnet run --file, so it can find deploy/third-party next to it.");
    return 2;
}

var repositoryRoot = Path.GetDirectoryName(deployDirectory)!;
var licencesDirectory = Path.Combine(deployDirectory, "third-party", "licences");
using var extra = JsonDocument.Parse(File.ReadAllText(Path.Combine(deployDirectory, "third-party", "extra.json")));

var depsFiles = new List<string>();
foreach (var input in args.Skip(1))
{
    if (Directory.Exists(input))
    {
        depsFiles.AddRange(Directory.EnumerateFiles(input, "*.deps.json", SearchOption.AllDirectories));
    }
    else if (File.Exists(input))
    {
        depsFiles.Add(input);
    }
    else
    {
        Console.Error.WriteLine($"Not found: {input}");
        return 1;
    }
}

if (depsFiles.Count == 0)
{
    Console.Error.WriteLine("No *.deps.json found: publish or build first.");
    return 1;
}

depsFiles.Sort(StringComparer.Ordinal);

// Packages and runtime packs, by id and version. Project references are this repository's own code. Each deps.json's
// runtime identifier (empty when it has none) and every library it names, projects included, decide which extra
// components apply.
var packages = new SortedDictionary<string, (string Id, string Version)>(StringComparer.Ordinal);
var packageVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var targets = new List<(string Runtime, HashSet<string> Libraries)>();
foreach (var depsFile in depsFiles)
{
    using var deps = JsonDocument.Parse(File.ReadAllText(depsFile));
    if (!deps.RootElement.TryGetProperty("libraries", out var libraries))
    {
        continue;
    }

    var targetName = deps.RootElement.TryGetProperty("runtimeTarget", out var runtimeTarget)
        && runtimeTarget.TryGetProperty("name", out var targetNameElement) ? targetNameElement.GetString() ?? "" : "";
    var slashInTarget = targetName.IndexOf('/', StringComparison.Ordinal);
    var target = (Runtime: slashInTarget >= 0 ? targetName[(slashInTarget + 1)..] : "",
        Libraries: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    targets.Add(target);

    foreach (var library in libraries.EnumerateObject())
    {
        var slash = library.Name.LastIndexOf('/');
        var id = library.Name[..slash];
        var version = library.Name[(slash + 1)..];
        target.Libraries.Add(id);

        var type = library.Value.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type is not ("package" or "runtimepack"))
        {
            continue;
        }

        if (type == "runtimepack" && id.StartsWith("runtimepack.", StringComparison.Ordinal))
        {
            id = id["runtimepack.".Length..];
        }

        // Sorted by id, then version; NUL sorts before any character an id can continue with.
        packages[$"{id.ToUpperInvariant()}\0{version.ToUpperInvariant()}"] = (id, version);
        packageVersions.TryAdd(id, version);
    }
}

var mappedLicences = extra.RootElement.GetProperty("licences").EnumerateObject()
    .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase);

var entries = new List<Entry>();
var components = new List<Entry>();
var texts = new List<Text>();
var textIndex = new Dictionary<string, int>(StringComparer.Ordinal);
var missing = new List<string>();
var problems = new List<string>();

foreach (var (id, version) in packages.Values)
{
    var directory = Path.Combine(packagesFolder, id.ToLowerInvariant(), version.ToLowerInvariant());
    var nuspecPath = Path.Combine(directory, id.ToLowerInvariant() + ".nuspec");
    if (!File.Exists(nuspecPath))
    {
        missing.Add($"{id} {version} (looked for {nuspecPath})");
        continue;
    }

    var metadata = XDocument.Load(nuspecPath).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
    string? Field(string name) =>
        metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } value ? value : null;
    var licenseElement = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
    var licenseType = licenseElement?.Attribute("type")?.Value;

    string? expression;
    string licence;
    var files = new List<string>();
    if (licenseType == "expression" && licenseElement!.Value.Trim() is { Length: > 0 } declared)
    {
        expression = declared;
        licence = $"{expression} (https://licenses.nuget.org/{expression})";
    }
    else
    {
        // A licence file or a licence URL names no SPDX id: extra.json does, once someone has read the licence.
        expression = mappedLicences.GetValueOrDefault(id);
        if (licenseType == "file")
        {
            files.Add(licenseElement!.Value.Trim().Replace('\\', '/'));
        }

        var source = licenseType == "file" ? "the licence file in the package, see Texts"
            : Field("licenseUrl") is { } url ? url
            : "not declared in the package";
        licence = expression is null ? source : $"{expression} ({source})";
        if (expression is null)
        {
            problems.Add($"{id} {version} declares no SPDX licence ({source}): add its id to \"licences\" in deploy/third-party/extra.json");
        }
    }

    // The licence and notice files at the package's root: they carry the copyright lines and the notices of what the
    // package itself bundles (the .NET runtime, ONNX Runtime, SkiaSharp's native libraries).
    var licenceFile = files.FirstOrDefault();
    files.AddRange(Directory.EnumerateFiles(directory)
        .Select(Path.GetFileName)
        .OfType<string>()
        .Where(IsLicenceOrNotice));

    var numbers = new SortedSet<int>();
    var hasLicenceText = false;
    foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
    {
        var path = Path.Combine(directory, file);
        if (File.Exists(path) && AddText(File.ReadAllText(path), $"{id} {version} ({Path.GetFileName(file)})") is { } number)
        {
            numbers.Add(number);
            hasLicenceText |= IsLicence(file) || string.Equals(file, licenceFile, StringComparison.OrdinalIgnoreCase);
        }
    }

    AddStandardTexts(expression, hasLicenceText, $"{id} {version}", numbers);
    entries.Add(new Entry($"{id} {version}", licence, Field("copyright"), Field("projectUrl"), null, [.. numbers]));
}

foreach (var component in extra.RootElement.GetProperty("components").EnumerateArray())
{
    string? Value(string name) => component.TryGetProperty(name, out var value) ? value.GetString() : null;
    string[] Values(string name) =>
        component.TryGetProperty(name, out var value) ? [.. value.EnumerateArray().Select(e => e.GetString()!)] : [];

    if (!Ships(Value("with")!, Values("runtimes")))
    {
        continue;
    }

    var name = Value("name")!;
    var title = Value("version") is { } template ? $"{name} {ExpandVersion(template)}" : name;
    var expression = Value("licence")!;
    var numbers = new SortedSet<int>();
    var hasLicenceText = false;
    foreach (var file in Values("texts"))
    {
        var path = Path.Combine(repositoryRoot, file);
        if (!File.Exists(path))
        {
            problems.Add($"{name}: {file} (in deploy/third-party/extra.json) does not exist");
        }
        else if (AddText(File.ReadAllText(path), $"{name} ({Path.GetFileName(file)})") is { } number)
        {
            numbers.Add(number);
            hasLicenceText = true;
        }
    }

    AddStandardTexts(expression, hasLicenceText, title, numbers);
    components.Add(new Entry(title, expression, Value("copyright"), Value("project"), Value("description"), [.. numbers]));
}

// The Rust crates statically linked into Velopack's native binaries, which ship with the Velopack package: listed for
// one Velopack release (deploy/third-party/velopack-crates.py writes the file), so it has to be the package's.
using var velopackCrates = JsonDocument.Parse(File.ReadAllText(Path.Combine(deployDirectory, "third-party", "velopack-crates.json")));
var crates = new List<Entry>();
if (packageVersions.TryGetValue("Velopack", out var velopackVersion))
{
    var listed = velopackCrates.RootElement.GetProperty("velopack").GetString();
    if (!string.Equals(listed, velopackVersion, StringComparison.OrdinalIgnoreCase))
    {
        problems.Add($"deploy/third-party/velopack-crates.json lists the crates of Velopack {listed}, but the package is {velopackVersion}: run python deploy/third-party/velopack-crates.py {velopackVersion}");
    }

    foreach (var crate in velopackCrates.RootElement.GetProperty("crates").EnumerateArray())
    {
        string? Value(string name) => crate.TryGetProperty(name, out var value) ? value.GetString() : null;
        if (!Ships("Velopack", [.. crate.GetProperty("runtimes").EnumerateArray().Select(e => e.GetString()!)]))
        {
            continue;
        }

        var (name, version) = (Value("name")!, Value("version")!);
        if (Value("licence") is not { } expression)
        {
            problems.Add($"{name} {version} (in deploy/third-party/velopack-crates.json) has no licence: read it in the crate's sources, add it to LICENCES in deploy/third-party/velopack-crates.py and rerun that");
            continue;
        }

        // MIT, BSD, ISC and Unicode-3.0 ask for the holder's copyright notice in binary copies too. A crate without one
        // is listed under another licence of its OR when one doesn't ask for it, and otherwise needs its own licence
        // file read: "copyrightFrom" names the file the script read, and says the file names nobody when "copyright"
        // is missing.
        var (copyright, copyrightFrom) = (Value("copyright"), Value("copyrightFrom"));
        var numbers = new SortedSet<int>();
        var ids = AddStandardTexts(expression, false, $"{name} {version}", numbers, hasCopyright: copyright is not null);
        if (copyright is null && copyrightFrom is null && ids?.FirstOrDefault(NamesHolder) is { } holderLicence)
        {
            problems.Add($"{name} {version} (in deploy/third-party/velopack-crates.json) has no copyright line, which {holderLicence} requires: add the crate's repository to COPYRIGHTS in deploy/third-party/velopack-crates.py and rerun that");
        }

        var licence = ids is not null && !ids.SequenceEqual(RequiredLicences(expression, hasCopyright: true)!)
            ? $"{expression}, used here under {string.Join(" AND ", ids)}"
            : expression;
        crates.Add(new Entry($"{name} {version}", licence, copyright ?? (copyrightFrom is null ? null : $"none named in its licence file ({copyrightFrom})"),
            $"https://crates.io/crates/{name}/{version}", null, [.. numbers]));
    }
}

if (missing.Count > 0)
{
    Console.Error.WriteLine($"Not in the NuGet packages folder {packagesFolder} (restore first):");
    foreach (var line in missing)
    {
        Console.Error.WriteLine($"  {line}");
    }

    return 1;
}

if (problems.Count > 0)
{
    foreach (var line in problems)
    {
        Console.Error.WriteLine(line);
    }

    return 1;
}

var rule = new string('=', 100);
var text = new StringBuilder();
text.Append("SemanticRouter: third-party notices\n\n");
text.Append("SemanticRouter is under the MIT licence (LICENSE, https://github.com/siebe41/SemanticRouter.NET). It includes\n");
text.Append("the third-party packages and components below, each under its own licence (an SPDX expression). The licence and\n");
text.Append("notice files that come with them follow the list, each distinct text once, and then the standard text of each\n");
text.Append("licence an entry has no text of its own for.\n\n");
text.Append(rule).Append('\n');
AppendEntries(entries);
if (components.Count > 0)
{
    text.Append('\n').Append(rule).Append('\n');
    text.Append("\nComponents that are not NuGet packages\n");
    AppendEntries(components);
}

if (crates.Count > 0)
{
    text.Append('\n').Append(rule).Append('\n');
    text.Append("\nRust crates in Velopack's native binaries (sources on crates.io; copyright lines as ClearlyDefined or the crate's licence file give them)\n");
    AppendEntries(crates);
}

for (var i = 0; i < texts.Count; i++)
{
    text.Append('\n').Append(rule).Append('\n');
    text.Append('[').Append(i + 1).Append("] ").Append(texts[i].Heading).AppendJoin("; ", texts[i].Carriers).Append('\n');
    text.Append(rule).Append("\n\n");
    text.Append(texts[i].Content).Append('\n');
}

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine($"{output}: {entries.Count} packages, {components.Count} other components, {crates.Count} Rust crates, {texts.Count} licence and notice texts");
return 0;

// Whether a deps.json given names the library and, when runtimes are given, targets one starting with one of them (a
// deps.json without a runtime identifier matches all).
bool Ships(string with, string[] runtimes) =>
    targets.Any(target => target.Libraries.Contains(with)
        && (runtimes.Length == 0 || target.Runtime.Length == 0
            || runtimes.Any(prefix => target.Runtime.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))));

void AppendEntries(List<Entry> list)
{
    foreach (var entry in list)
    {
        text.Append('\n').Append(entry.Title).Append('\n');
        if (entry.Description is { } description)
        {
            text.Append("  What:      ").Append(description).Append('\n');
        }

        text.Append("  Licence:   ").Append(entry.Licence).Append('\n');
        if (entry.Copyright is { } copyright)
        {
            text.Append("  Copyright: ").Append(copyright.ReplaceLineEndings(" ")).Append('\n');
        }

        if (entry.ProjectUrl is { } projectUrl)
        {
            text.Append("  Project:   ").Append(projectUrl).Append('\n');
        }

        if (entry.Texts.Length > 0)
        {
            text.Append("  Texts:     ").AppendJoin(", ", entry.Texts.Select(n => $"[{n}]")).Append('\n');
        }
    }
}

// Adds a text once (by heading and content) and records who carries it; null for an empty file.
int? AddText(string raw, string carrier, string heading = "")
{
    var content = Normalize(raw);
    if (content.Length == 0)
    {
        return null;
    }

    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(heading + "\0" + content)));
    if (!textIndex.TryGetValue(hash, out var number))
    {
        texts.Add(new Text(heading, content, []));
        number = texts.Count;
        textIndex[hash] = number;
    }

    texts[number - 1].Carriers.Add(carrier);
    return number;
}

// The standard text of each licence the expression requires, unless the entry carries the text of its only licence
// itself (a package's own LICENSE file, which also holds its copyright line). The standard texts leave the copyright
// line out: the entry's Copyright says who holds it. Returns the licences required, or null.
List<string>? AddStandardTexts(string? expression, bool hasLicenceText, string carrier, SortedSet<int> numbers, bool hasCopyright = true)
{
    if (expression is null)
    {
        return null;
    }

    var ids = RequiredLicences(expression, hasCopyright);
    if (ids is null)
    {
        problems.Add($"{carrier}: \"{expression}\" is not an SPDX expression this generator reads");
        return null;
    }

    if (hasLicenceText && ids.Count == 1)
    {
        return ids;
    }

    foreach (var id in ids)
    {
        var path = Path.Combine(licencesDirectory, id + ".txt");
        if (!File.Exists(path))
        {
            problems.Add($"{carrier}: no standard text for {id}; add deploy/third-party/licences/{id}.txt (from https://github.com/spdx/license-list-data/tree/main/text)");
        }
        else if (AddText(File.ReadAllText(path), carrier, $"{id}, the standard licence text (SPDX License List), for: ") is { } number)
        {
            numbers.Add(number);
        }
    }

    return ids;
}

// "{Velopack}" in a component's version is the version of the Velopack package.
string ExpandVersion(string template)
{
    var result = new StringBuilder();
    var at = 0;
    while (template.IndexOf('{', at) is var open and >= 0 && template.IndexOf('}', open) is var close and >= 0)
    {
        var id = template[(open + 1)..close];
        if (!packageVersions.TryGetValue(id, out var version))
        {
            problems.Add($"\"{template}\" (in deploy/third-party/extra.json) names {id}, which no deps.json has as a package");
        }

        result.Append(template, at, open - at).Append(version ?? "?");
        at = close + 1;
    }

    return result.Append(template, at, template.Length - at).ToString();
}

static bool IsLicenceOrNotice(string name)
{
    var stem = Stem(name);
    var extension = Path.GetExtension(name);
    return extension.ToUpperInvariant() is "" or ".TXT" or ".MD"
        && (IsLicence(name)
            || stem.StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("THIRDPARTYNOTICE", StringComparison.OrdinalIgnoreCase));
}

static bool IsLicence(string name) =>
    Stem(name).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) || Stem(name).StartsWith("LICENCE", StringComparison.OrdinalIgnoreCase);

static string Stem(string name) =>
    Path.GetFileNameWithoutExtension(name).Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);

// The licence ids whose terms the recipient takes: all of an AND, the first of an OR (the one this distribution is
// under), and a WITH's licence and exception. Without a copyright line, an OR's first choice that requires one gives way
// to the first that doesn't (MIT OR Apache-2.0 is used under Apache-2.0). Null when the expression does not parse.
static List<string>? RequiredLicences(string expression, bool hasCopyright)
{
    var tokens = expression.Replace("(", " ( ", StringComparison.Ordinal).Replace(")", " ) ", StringComparison.Ordinal)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var position = 0;
    var ids = Or();
    return ids is not null && position == tokens.Length ? [.. ids.Distinct(StringComparer.Ordinal)] : null;

    bool Next(string keyword)
    {
        if (position < tokens.Length && tokens[position].Equals(keyword, StringComparison.OrdinalIgnoreCase))
        {
            position++;
            return true;
        }

        return false;
    }

    List<string>? Or()
    {
        if (And() is not { } first)
        {
            return null;
        }

        var choices = new List<List<string>> { first };
        while (Next("OR"))
        {
            if (And() is not { } choice)
            {
                return null;
            }

            choices.Add(choice);
        }

        return hasCopyright || !first.Any(NamesHolder) ? first : choices.FirstOrDefault(c => !c.Any(NamesHolder)) ?? first;
    }

    List<string>? And()
    {
        var all = With();
        while (all is not null && Next("AND"))
        {
            all = With() is { } next ? [.. all, .. next] : null;
        }

        return all;
    }

    List<string>? With()
    {
        var licence = Primary();
        if (licence is not null && Next("WITH"))
        {
            return position < tokens.Length && IsId(tokens[position]) ? [.. licence, tokens[position++]] : null;
        }

        return licence;
    }

    List<string>? Primary()
    {
        if (Next("("))
        {
            var inner = Or();
            return inner is not null && Next(")") ? inner : null;
        }

        return position < tokens.Length && IsId(tokens[position]) ? [tokens[position++]] : null;
    }

    static bool IsId(string token) => token is not ("(" or ")")
        && !token.Equals("AND", StringComparison.OrdinalIgnoreCase)
        && !token.Equals("OR", StringComparison.OrdinalIgnoreCase)
        && !token.Equals("WITH", StringComparison.OrdinalIgnoreCase);
}

// Licences whose terms ask binary copies to carry the holder's copyright notice. Apache-2.0 asks for the licence and
// any NOTICE file, zlib for an unaltered notice in source copies only, and 0BSD, CC0, the Unlicense and
// CDLA-Permissive-2.0 for no notice at all.
static bool NamesHolder(string id) => id is "MIT" or "BSD-2-Clause" or "BSD-3-Clause" or "ISC" or "Unicode-3.0" or "Unicode-DFS-2016";

// Line endings, a byte-order mark and trailing whitespace differ between packages and say nothing.
static string Normalize(string content) =>
    string.Join('\n', content.TrimStart('﻿').ReplaceLineEndings("\n").Split('\n').Select(line => line.TrimEnd())).Trim('\n');

internal sealed record Entry(string Title, string Licence, string? Copyright, string? ProjectUrl, string? Description, int[] Texts);

internal sealed record Text(string Heading, string Content, List<string> Carriers);
