// Completes a CycloneDX SBOM that Syft made of a release deliverable, in place, with what Syft cannot read (docs/releases.md):
//
//   - the subject: metadata.component names the product, its release version and a purl, instead of the scanned directory;
//   - a licence on every NuGet component: the SPDX expression from the package's .nuspec (or, for a package whose nuspec
//     names a licence file or URL, or that no restore downloads, such as the container's shared frameworks, from
//     "licences" in deploy/third-party/extra.json), and their package licence for this repository's projects, whose
//     purl names the package (the tool's assembly is semantic-router, its package SemanticRouter.Host) or, for a
//     project that is not packed, such as the desktop app, is removed;
//   - the runtime packs' real package ids: Syft names them runtimepack.<id>, which no advisory database knows;
//   - the components that are not NuGet packages, from deploy/third-party/extra.json, as THIRD-PARTY-NOTICES.txt lists them:
//     the embedded fonts and, with --installer, what vpk adds to the installers and update packages (Velopack's native
//     binaries, Rust's standard library, the AppImage runtime and what it links).
//
//   dotnet run --file deploy/sbom-complete.cs -- <sbom.cdx.json> --type <type> --name <name> --version <version>
//       --purl <purl> [--description <text>] [--contents <directory>]... [--installer]
//
// --contents is what the SBOM was made from (or the same files): its *.deps.json files say which extra components apply,
// as for deploy/third-party-notices.cs, and which libraries are this repository's projects, and a *.nuspec in it (an
// unpacked package) gives that package's licence and version. Package metadata is otherwise read from the NuGet global
// packages folder that restore filled (NUGET_PACKAGES, or ~/.nuget/packages), so it makes no network call. A NuGet
// component whose licence cannot be found is an error, not a gap. A .NET 10 file-based app, like the notices generator.
#:property PublishAot=false

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

const string Usage = "Usage: dotnet run --file deploy/sbom-complete.cs -- <sbom.cdx.json> --type <type> --name <name> "
    + "--version <version> --purl <purl> [--description <text>] [--contents <directory>]... [--installer]";

if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var sbomPath = args[0];
var options = new Dictionary<string, string>(StringComparer.Ordinal);
var contents = new List<string>();
var installer = false;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--installer":
            installer = true;
            break;
        case "--contents" when i + 1 < args.Length:
            contents.Add(args[++i]);
            break;
        case "--type" or "--name" or "--version" or "--purl" or "--description" when i + 1 < args.Length:
            options[args[i][2..]] = args[++i];
            break;
        default:
            Console.Error.WriteLine($"Unknown or incomplete option {args[i]}. {Usage}");
            return 2;
    }
}

foreach (var required in new[] { "type", "name", "version", "purl" })
{
    if (!options.ContainsKey(required))
    {
        Console.Error.WriteLine($"--{required} is required. {Usage}");
        return 2;
    }
}

// This file's directory (deploy/), which the SDK passes to a file-based app; the repository root is its parent.
if (AppContext.GetData("EntryPointFileDirectoryPath") is not string deployDirectory)
{
    Console.Error.WriteLine("Run it with dotnet run --file, so it can find deploy/third-party next to it.");
    return 2;
}

var repositoryRoot = Path.GetDirectoryName(deployDirectory)!;
var packagesFolder = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
    ? configured
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
using var extra = JsonDocument.Parse(File.ReadAllText(Path.Combine(deployDirectory, "third-party", "extra.json")));
var mappedLicences = extra.RootElement.GetProperty("licences").EnumerateObject()
    .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase);

// This repository's projects, by assembly name (what a deps.json and Syft call them), with their package licence and,
// for a packed project, its package id (the tool's assembly is semantic-router, its package SemanticRouter.Host).
var defaultLicence = PackageLicence(Path.Combine(repositoryRoot, "Directory.Build.props"))
    ?? throw new InvalidOperationException("Directory.Build.props sets no PackageLicenseExpression.");
var ownProjects = new Dictionary<string, (string Licence, string? PackageId)>(StringComparer.OrdinalIgnoreCase);
foreach (var project in Directory.EnumerateFiles(Path.Combine(repositoryRoot, "src"), "*.csproj", SearchOption.AllDirectories))
{
    var file = XDocument.Load(project);
    string? Property(string name) =>
        file.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } value ? value : null;
    var projectName = Path.GetFileNameWithoutExtension(project);
    ownProjects[Property("AssemblyName") ?? projectName] = (Property("PackageLicenseExpression") ?? defaultLicence,
        string.Equals(Property("IsPackable"), "true", StringComparison.OrdinalIgnoreCase) ? Property("PackageId") ?? projectName : null);
}

// Generated by deploy/update-file-list.cs rather than a csproj, and deliberately absent from deps.json: this
// resource-only library belongs to the app, not to a NuGet package Syft infers from its assembly name.
ownProjects["SemanticRouter.Files"] = (defaultLicence, null);

// What the deliverable is made of: each deps.json's runtime and libraries (as in deploy/third-party-notices.cs), its
// projects, the packages' versions, and the nuspecs of unpacked packages.
var targets = new List<(string Runtime, HashSet<string> Libraries)>();
var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var packageVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var unpacked = new Dictionary<string, (string Version, string? Licence)>(StringComparer.OrdinalIgnoreCase);
foreach (var directory in contents)
{
    if (!Directory.Exists(directory))
    {
        Console.Error.WriteLine($"Not found: {directory}");
        return 1;
    }

    foreach (var depsFile in Directory.EnumerateFiles(directory, "*.deps.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
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
            target.Libraries.Add(id);
            var type = library.Value.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "project")
            {
                projects.Add(id);
            }
            else if (type is "package" or "runtimepack")
            {
                packageVersions.TryAdd(id.StartsWith("runtimepack.", StringComparison.Ordinal) ? id["runtimepack.".Length..] : id,
                    library.Name[(slash + 1)..]);
            }
        }
    }

    foreach (var nuspec in Directory.EnumerateFiles(directory, "*.nuspec", SearchOption.AllDirectories))
    {
        var metadata = Metadata(nuspec);
        if (Field(metadata, "id") is { } id && Field(metadata, "version") is { } version)
        {
            unpacked.TryAdd(id, (version, Expression(metadata)));
        }
    }
}

var sbom = JsonNode.Parse(File.ReadAllText(sbomPath))?.AsObject()
    ?? throw new InvalidOperationException($"{sbomPath} is not a JSON object.");
var problems = new List<string>();

// The subject. Syft's is the scanned directory ("sbom", a file) or, for an image, the image and its id.
var metadataNode = sbom["metadata"] as JsonObject ?? [];
sbom["metadata"] = metadataNode;
var scanned = metadataNode["component"] as JsonObject;
var subject = new JsonObject
{
    ["bom-ref"] = options["purl"],
    ["type"] = options["type"],
    ["name"] = options["name"],
    ["version"] = options["version"],
};
if (options.TryGetValue("description", out var description))
{
    subject["description"] = description;
}

subject["purl"] = options["purl"];
if (scanned?["type"]?.GetValue<string>() == "container" && scanned["version"]?.GetValue<string>() is { } imageId)
{
    subject["properties"] = new JsonArray(new JsonObject { ["name"] = "semantic-router:image-id", ["value"] = imageId });
}

metadataNode["component"] = subject;
if (scanned?["bom-ref"]?.GetValue<string>() is { } scannedRef && sbom["dependencies"] is JsonArray dependencies)
{
    foreach (var dependency in dependencies.OfType<JsonObject>())
    {
        if (dependency["ref"]?.GetValue<string>() == scannedRef)
        {
            dependency["ref"] = options["purl"];
        }
    }
}

// Every NuGet component gets its licence; runtime packs get their package id; unpacked packages their NuGet version (Syft
// reads the assembly's, 0.6.0.0).
var components = sbom["components"] as JsonArray ?? [];
sbom["components"] = components;
var licensed = 0;
foreach (var component in components.OfType<JsonObject>())
{
    if (component["purl"]?.GetValue<string>() is not { } purl || !purl.StartsWith("pkg:nuget/", StringComparison.Ordinal))
    {
        continue;
    }

    var coordinates = purl["pkg:nuget/".Length..].Split('?', '#')[0];
    var at = coordinates.LastIndexOf('@');
    var id = Uri.UnescapeDataString(at >= 0 ? coordinates[..at] : coordinates);
    var version = at >= 0 ? Uri.UnescapeDataString(coordinates[(at + 1)..]) : component["version"]?.GetValue<string>() ?? "";
    if (id.StartsWith("runtimepack.", StringComparison.Ordinal))
    {
        id = id["runtimepack.".Length..];
    }

    var nuspecPath = Path.Combine(packagesFolder, id.ToLowerInvariant(), version.ToLowerInvariant(), id.ToLowerInvariant() + ".nuspec");
    string? licence;
    string? packageId = id;
    if (unpacked.TryGetValue(id, out var package) && package.Licence is not null)
    {
        (version, licence) = package;
    }
    else if (projects.Contains(id) || ownProjects.ContainsKey(id) && !File.Exists(nuspecPath))
    {
        // A project that is not packed is no NuGet package: a purl would name whatever nuget.org has under its name.
        (licence, packageId) = ownProjects.GetValueOrDefault(id, (defaultLicence, null));
    }
    else
    {
        licence = (File.Exists(nuspecPath) ? Expression(Metadata(nuspecPath)) : null) ?? MappedLicence(id);
    }

    component["name"] = id;
    component["version"] = version;
    if (packageId is null)
    {
        component.Remove("purl");
    }
    else
    {
        component["purl"] = $"pkg:nuget/{Uri.EscapeDataString(packageId)}@{Uri.EscapeDataString(version)}";
    }
    if (licence is null)
    {
        problems.Add(File.Exists(nuspecPath)
            ? $"{id} {version} declares no SPDX licence: add its id to \"licences\" in deploy/third-party/extra.json"
            : $"{id} {version} is not in the NuGet packages folder {packagesFolder} (restore first), nor in \"licences\" in deploy/third-party/extra.json");
        continue;
    }

    component["licenses"] = new JsonArray(new JsonObject { ["expression"] = licence });
    licensed++;
}

// The components no scanner finds, under the same rule as the notices: a deps.json names the library in "with" and, if
// "runtimes" is set, targets one of them. What vpk adds ("addedBy": "vpk") is only in the installers and update packages.
var added = 0;
foreach (var entry in extra.RootElement.GetProperty("components").EnumerateArray())
{
    string? Value(string name) => entry.TryGetProperty(name, out var value) ? value.GetString() : null;
    var with = Value("with")!;
    string[] runtimes = entry.TryGetProperty("runtimes", out var r) ? [.. r.EnumerateArray().Select(e => e.GetString()!)] : [];
    if ((Value("addedBy") == "vpk" && !installer)
        || !targets.Any(target => target.Libraries.Contains(with)
            && (runtimes.Length == 0 || target.Runtime.Length == 0
                || runtimes.Any(prefix => target.Runtime.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))))
    {
        continue;
    }

    var name = Value("name")!;
    var bomRef = $"third-party:{name}";
    if (components.OfType<JsonObject>().Any(c => c["bom-ref"]?.GetValue<string>() == bomRef))
    {
        continue;
    }

    var component = new JsonObject { ["bom-ref"] = bomRef, ["type"] = Value("type") ?? "library", ["name"] = name };
    // A version the notices word as prose ("as vendored by Velopack 1.2.161") is not one an inventory tool can read: the
    // component gets no version, and the prose goes in its description.
    string? prose = null;
    if (Value("version") is { } template)
    {
        var version = ExpandVersion(template);
        if (version.Any(char.IsWhiteSpace))
        {
            prose = version;
        }
        else
        {
            component["version"] = version;
        }
    }

    if (Value("description") is { } what)
    {
        component["description"] = prose is null ? what : $"{what} ({prose})";
    }
    else if (prose is not null)
    {
        component["description"] = prose;
    }

    if (Value("copyright") is { } copyright)
    {
        component["copyright"] = copyright;
    }

    component["licenses"] = new JsonArray(new JsonObject { ["expression"] = Value("licence")! });
    if (Value("project") is { } project)
    {
        component["externalReferences"] = new JsonArray(new JsonObject { ["type"] = "website", ["url"] = project });
    }

    components.Add(component);
    added++;
}

if (problems.Count > 0)
{
    foreach (var line in problems)
    {
        Console.Error.WriteLine(line);
    }

    return 1;
}

var json = sbom.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
File.WriteAllText(sbomPath, json.ReplaceLineEndings("\n") + "\n");
Console.WriteLine($"{sbomPath}: {options["name"]} {options["version"]}, {licensed} NuGet components with a licence, {added} other components added");
return 0;

// The licence "licences" in deploy/third-party/extra.json gives the package: by its id, or by the longest id ending in
// "*" that it starts with.
string? MappedLicence(string id) =>
    mappedLicences.GetValueOrDefault(id)
    ?? mappedLicences.Where(p => p.Key.EndsWith('*') && id.StartsWith(p.Key[..^1], StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(p => p.Key.Length).Select(p => p.Value).FirstOrDefault();

// "{Velopack}" in a component's version is the version of the Velopack package.
string ExpandVersion(string template)
{
    var result = template;
    while (result.IndexOf('{', StringComparison.Ordinal) is var open and >= 0 && result.IndexOf('}', open) is var close and >= 0)
    {
        var id = result[(open + 1)..close];
        if (!packageVersions.TryGetValue(id, out var version))
        {
            problems.Add($"\"{template}\" (in deploy/third-party/extra.json) names {id}, which no deps.json has as a package");
        }

        result = string.Concat(result.AsSpan(0, open), version ?? "?", result.AsSpan(close + 1));
    }

    return result;
}

static XElement? Metadata(string nuspec) =>
    XDocument.Load(nuspec).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");

static string? Field(XElement? metadata, string name) =>
    metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } value ? value : null;

// The nuspec's SPDX expression; a licence file or URL names none.
static string? Expression(XElement? metadata) =>
    metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "license") is { } license
        && license.Attribute("type")?.Value == "expression" && license.Value.Trim() is { Length: > 0 } expression
        ? expression
        : null;

static string? PackageLicence(string projectFile) =>
    XDocument.Load(projectFile).Descendants().FirstOrDefault(e => e.Name.LocalName == "PackageLicenseExpression")?.Value.Trim()
        is { Length: > 0 } licence ? licence : null;
