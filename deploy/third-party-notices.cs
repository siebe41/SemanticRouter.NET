// Writes THIRD-PARTY-NOTICES.txt for what a build ships: every NuGet package (and .NET runtime pack) named in the
// *.deps.json files given, with its licence, copyright and project page from the package's .nuspec, followed by the
// licence and notice files the packages carry (each distinct text once). A .NET 10 file-based app, so it runs wherever
// the SDK does (the release jobs on every OS and the container build) with nothing to install:
//
//   dotnet run --file deploy/third-party-notices.cs -- <output-file> <deps.json or directory>...
//   dotnet run --file deploy/third-party-notices.cs -- publish/THIRD-PARTY-NOTICES.txt publish
//
// A directory is searched for *.deps.json. Package metadata is read from the NuGet global packages folder that restore
// filled (NUGET_PACKAGES, or ~/.nuget/packages), so it makes no network call; a package missing from it is an error,
// not a silent gap. The output depends only on the packages: same inputs, same bytes.
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

// Packages and runtime packs, by id and version. Project references are this repository's own code.
var packages = new SortedDictionary<string, (string Id, string Version)>(StringComparer.Ordinal);
foreach (var depsFile in depsFiles)
{
    using var deps = JsonDocument.Parse(File.ReadAllText(depsFile));
    if (!deps.RootElement.TryGetProperty("libraries", out var libraries))
    {
        continue;
    }

    foreach (var library in libraries.EnumerateObject())
    {
        var type = library.Value.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type is not ("package" or "runtimepack"))
        {
            continue;
        }

        var slash = library.Name.LastIndexOf('/');
        var id = library.Name[..slash];
        if (type == "runtimepack" && id.StartsWith("runtimepack.", StringComparison.Ordinal))
        {
            id = id["runtimepack.".Length..];
        }

        var version = library.Name[(slash + 1)..];
        // Sorted by id, then version; NUL sorts before any character an id can continue with.
        packages[$"{id.ToUpperInvariant()}\0{version.ToUpperInvariant()}"] = (id, version);
    }
}

var entries = new List<Entry>();
var texts = new List<Text>();
var textIndex = new Dictionary<string, int>(StringComparer.Ordinal);
var missing = new List<string>();

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

    string licence;
    var files = new List<string>();
    if (licenseType == "expression" && licenseElement!.Value.Trim() is { Length: > 0 } expression)
    {
        licence = $"{expression} (https://licenses.nuget.org/{expression})";
    }
    else if (licenseType == "file")
    {
        licence = "the licence file in the package (see Texts)";
        files.Add(licenseElement!.Value.Trim().Replace('\\', '/'));
    }
    else if (Field("licenseUrl") is { } url)
    {
        licence = url;
    }
    else
    {
        licence = "not declared in the package";
    }

    // The licence and notice files at the package's root: they carry the copyright lines and the notices of what the
    // package itself bundles (the .NET runtime, ONNX Runtime, SkiaSharp's native libraries).
    files.AddRange(Directory.EnumerateFiles(directory)
        .Select(Path.GetFileName)
        .OfType<string>()
        .Where(IsLicenceOrNotice));

    var numbers = new SortedSet<int>();
    foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
    {
        var path = Path.Combine(directory, file);
        if (!File.Exists(path))
        {
            continue;
        }

        var content = Normalize(File.ReadAllText(path));
        if (content.Length == 0)
        {
            continue;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        if (!textIndex.TryGetValue(hash, out var number))
        {
            texts.Add(new Text(content, []));
            number = texts.Count;
            textIndex[hash] = number;
        }

        texts[number - 1].Packages.Add($"{id} {version} ({Path.GetFileName(file)})");
        numbers.Add(number);
    }

    entries.Add(new Entry(id, version, licence, Field("copyright"), Field("projectUrl"), [.. numbers]));
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

var rule = new string('=', 100);
var text = new StringBuilder();
text.Append("SemanticRouter: third-party notices\n\n");
text.Append("SemanticRouter is under the MIT licence (LICENSE, https://github.com/siebe41/SemanticRouter.NET). It includes\n");
text.Append("the third-party packages below, each under its own licence. The licence and notice files that come with them\n");
text.Append("follow the list, each distinct text once.\n\n");
text.Append(rule).Append('\n');
foreach (var entry in entries)
{
    text.Append('\n').Append(entry.Id).Append(' ').Append(entry.Version).Append('\n');
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

for (var i = 0; i < texts.Count; i++)
{
    text.Append('\n').Append(rule).Append('\n');
    text.Append('[').Append(i + 1).Append("] ").AppendJoin("; ", texts[i].Packages).Append('\n');
    text.Append(rule).Append("\n\n");
    text.Append(texts[i].Content).Append('\n');
}

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine($"{output}: {entries.Count} packages, {texts.Count} licence and notice texts");
return 0;

static bool IsLicenceOrNotice(string name)
{
    var stem = Path.GetFileNameWithoutExtension(name).Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
    var extension = Path.GetExtension(name);
    return extension.ToUpperInvariant() is "" or ".TXT" or ".MD"
        && (stem.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("LICENCE", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("THIRDPARTYNOTICE", StringComparison.OrdinalIgnoreCase));
}

// Line endings, a byte-order mark and trailing whitespace differ between packages and say nothing.
static string Normalize(string content) =>
    string.Join('\n', content.TrimStart('﻿').ReplaceLineEndings("\n").Split('\n').Select(line => line.TrimEnd())).Trim('\n');

internal sealed record Entry(string Id, string Version, string Licence, string? Copyright, string? ProjectUrl, int[] Texts);

internal sealed record Text(string Content, List<string> Packages);
