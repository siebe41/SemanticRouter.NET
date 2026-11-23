// The list of a Windows build's files that its signature covers. Authenticode signs programs and libraries, each on its
// own, and not the data files beside them (SemanticRouter.runtimeconfig.json, SemanticRouter.deps.json,
// THIRD-PARTY-NOTICES.txt, …), so `write` puts every file of a published app into a small library,
// SemanticRouter.Files.dll, next to them: a data file by its SHA-256, a Windows executable image by its digest without
// an Authenticode signature (vpk signs the images after this runs, and that must not change it). vpk signs that library
// with the rest of the release, and the desktop app's publisher check (src/SemanticRouter.Desktop/UpdateFileList.cs)
// refuses an update whose files differ from it, so no program or library can be swapped for another signed one either.
// `write` first deletes from the publish folder what vpk would leave out of the package (createdump.exe, symbols), so
// that the list names exactly what the package will hold. The list also records the version it was made for, which vpk
// gives the package (--packVersion): the app refuses a package whose .nuspec, or the feed, says another version, so an
// older signed release cannot be offered with its .nuspec rewritten to a newer version (#428). `check` reads the list
// back from a Velopack package and fails unless it matches the package's files exactly and names the version its
// .nuspec gives, so a file added or changed after `write` ran (other than by signing) is caught before the release, not
// by every updater in the field. A .NET 10 file-based app, with nothing to install:
//
//   dotnet run --file deploy/update-file-list.cs -- write <publish-dir> <version>
//   dotnet run --file deploy/update-file-list.cs -- check <SemanticRouter-<version>-<rid>-full.nupkg>
//
// The library holds one managed resource, files.json:
// {"version":"<x.y.z>","files":{"<path relative to the app folder>":"<sha256 hex>"},"executables":{"<path>":"<image digest hex>"}},
// paths with forward slashes, sorted. It is never loaded: no deps.json names it. Same files and version, same bytes.
#:property PublishAot=false

using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

const string ListFile = "SemanticRouter.Files.dll";
const string Resource = "files.json";
const string AppFolder = "lib/app/";

// The app's identity, which the desktop app requires of every update package (UpdatePublisher).
const string AppId = "SemanticRouter";
const string MainExe = "SemanticRouter.exe";

// vpk writes it into the app folder after the list was made: a copy of the package's .nuspec.
const string VersionFile = "sq.version";

// The programs vpk adds to the app folder after the list was made, which the app requires the publisher to have signed
// (UpdateFileList.Packaged); deploy/installer-signature-check.ps1 checks that they are.
string[] packaged = ["Squirrel.exe", AppId + "_ExecutionStub.exe"];

// What `vpk pack` (1.2.161) leaves out of the package or replaces, matched against a file's path in the publish folder:
// its built-in excludes (`.*[\\/]createdump.*|.*\.vshost\..*|.*\.nupkg$`, without --noDefaultExclude), the default of
// its --exclude option (`.*\.pdb`; package-installer.sh passes none), ClickOnce manifests (`*.application`) on Windows,
// and the Update.exe and sq.version it writes itself. The .NET runtime's createdump.exe is one: a self-contained
// publish has it, and vpk drops it. `write` deletes them from the publish folder first, so the list, the package and the
// zip download hold the same files; matched without case, so the folder never keeps what vpk might drop.
Regex leftOutByVpk = new(@"(^|/)createdump|\.vshost\.|\.nupkg$|\.pdb$|\.application$|^Update\.exe$|^sq\.version$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

// A release's version as the release passes it to vpk (the tag without its v): major.minor.patch and an optional
// prerelease, nothing vpk would write differently in the .nuspec, so `check` can compare the two as text.
Regex releaseVersion = new(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant);

if (args is ["write", var publish, var releasing])
{
    if (!releaseVersion.IsMatch(releasing))
    {
        Console.Error.WriteLine($"::error::{releasing} is not a release version (major.minor.patch, with an optional -prerelease).");
        return 2;
    }

    string root = Path.GetFullPath(publish);
    var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
    var executables = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList())
    {
        string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        if (leftOutByVpk.IsMatch(relative))
        {
            File.Delete(file);
            Console.WriteLine($"{relative}: removed, as vpk leaves it out of the package");
            continue;
        }

        byte[] content = File.ReadAllBytes(file);
        if (relative == ListFile)
        {
            continue;
        }

        if (IsExecutableImage(content))
        {
            executables[relative] = ImageDigest(content);
        }
        else
        {
            files[relative] = Convert.ToHexStringLower(SHA256.HashData(content));
        }
    }

    var json = new JsonObject
    {
        ["version"] = releasing,
        ["files"] = new JsonObject(files.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value))),
        ["executables"] = new JsonObject(executables.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value))),
    };
    File.WriteAllBytes(Path.Combine(root, ListFile), Library(JsonSerializer.SerializeToUtf8Bytes(json)));
    Console.WriteLine($"{ListFile}: {files.Count} data files and {executables.Count} programs and libraries of {releasing}");
    return 0;
}

if (args is ["check", var package])
{
    using ZipArchive zip = ZipFile.OpenRead(package);
    string name = Path.GetFileName(package);
    ZipArchiveEntry[] app = [.. zip.Entries.Where(e => e.FullName.StartsWith(AppFolder, StringComparison.Ordinal) && !e.FullName.EndsWith('/'))];
    if (app.FirstOrDefault(e => e.FullName == AppFolder + ListFile) is not { } list)
    {
        Console.Error.WriteLine($"::error::{name} has no {ListFile}: run deploy/update-file-list.cs write on the published app before packaging it.");
        return 1;
    }

    JsonNode listed = JsonNode.Parse(ReadResource(Bytes(list)))!;
    var files = listed["files"]!.AsObject().ToDictionary(f => f.Key, f => f.Value!.GetValue<string>(), StringComparer.Ordinal);
    var executables = (listed["executables"]?.AsObject() ?? []).ToDictionary(f => f.Key, f => f.Value!.GetValue<string>(), StringComparer.Ordinal);

    byte[] nuspec = Bytes(zip.Entries.Single(e => !e.FullName.Contains('/', StringComparison.Ordinal) && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)));
    var problems = new List<string>();
    XElement? metadata = XDocument.Load(new MemoryStream(nuspec)).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
    string? id = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "id")?.Value.Trim();
    string? mainExe = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "mainExe")?.Value.Trim();
    string? version = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "version")?.Value.Trim();
    if (id != AppId || mainExe != MainExe)
    {
        problems.Add($"its .nuspec names {id} with main executable {mainExe}; the desktop app accepts only {AppId} with {MainExe}");
    }

    // The desktop app refuses a package whose list was made for another version than its .nuspec gives.
    string? listedVersion = listed["version"]?.GetValueKind() == JsonValueKind.String ? listed["version"]!.GetValue<string>() : null;
    if (listedVersion is null)
    {
        problems.Add($"{ListFile} does not say which version it was made for: run deploy/update-file-list.cs write with the release's version");
    }
    else if (listedVersion != version)
    {
        problems.Add($"{ListFile} was made for version {listedVersion}, but its .nuspec gives {version}: pass deploy/update-file-list.cs write the version vpk packs");
    }

    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (ZipArchiveEntry entry in app)
    {
        string relative = entry.FullName[AppFolder.Length..];
        byte[] content = Bytes(entry);
        bool executable = IsExecutableImage(content);
        if (!seen.Add(relative))
        {
            problems.Add($"{relative} is in the package twice");
        }
        else if (relative == ListFile || (executable && packaged.Contains(relative)))
        {
            // Covered by its own signature, which must be the publisher's.
        }
        else if (relative == VersionFile)
        {
            if (!content.AsSpan().SequenceEqual(nuspec))
            {
                problems.Add($"{VersionFile} is not a copy of the package's .nuspec");
            }
        }
        else if (!(executable ? executables : files).Remove(relative, out string? expected))
        {
            problems.Add($"{relative} is not in {ListFile}: it was added after deploy/update-file-list.cs write ran");
        }
        else if (!string.Equals(executable ? ImageDigest(content) : Convert.ToHexStringLower(SHA256.HashData(content)), expected, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"{relative} was changed after deploy/update-file-list.cs write ran{(executable ? " (other than by signing it)" : string.Empty)}");
        }
    }

    problems.AddRange(files.Keys.Concat(executables.Keys).Select(path => $"{path} is in {ListFile} but not in the package (or is now of another kind)"));
    foreach (string problem in problems)
    {
        Console.Error.WriteLine($"::error::{name}: {problem}");
    }

    if (problems.Count > 0)
    {
        return 1;
    }

    Console.WriteLine($"{name}: every file but those vpk adds is in {ListFile}, made for {version}");
    return 0;
}

Console.Error.WriteLine("Usage: dotnet run --file deploy/update-file-list.cs -- write <publish-dir> <version> | check <full.nupkg>");
return 2;

// A Windows program or library starts with MZ; the desktop app's check tells them apart the same way.
static bool IsExecutableImage(byte[] content) => content.Length >= 2 && content[0] == (byte)'M' && content[1] == (byte)'Z';

// The SHA-256 of an executable image without its Authenticode signature: the image with its checksum, the certificate
// table's entry in the data directories and the certificate table left out, and without the zeros before that table (a
// signer pads the image to eight bytes). A file that is not a PE image, or whose certificate table does not end it, is
// hashed whole. The same as UpdateFileList.ImageDigest in the desktop app.
static string ImageDigest(byte[] image)
{
    if (Layout(image) is not { } layout)
    {
        return Convert.ToHexStringLower(SHA256.HashData(image));
    }

    (int checksum, int entry, int end) = layout;
    int after = entry + 8;
    while (end > after && image[end - 1] == 0)
    {
        end--;
    }

    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    hash.AppendData(image, 0, checksum);
    hash.AppendData(image, checksum + 4, entry - checksum - 4);
    hash.AppendData(image, after, end - after);
    return Convert.ToHexStringLower(hash.GetHashAndReset());
}

static (int Checksum, int Entry, int End)? Layout(byte[] image)
{
    if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
    {
        return null;
    }

    int pe = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C));
    if (pe < 0x40 || pe > image.Length - 26 || !image.AsSpan(pe, 4).SequenceEqual("PE\0\0"u8))
    {
        return null;
    }

    int optional = pe + 24;
    int optionalEnd = optional + BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 20));
    int directories = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(optional)) switch
    {
        0x10b => optional + 96,
        0x20b => optional + 112,
        _ => -1,
    };
    int entry = directories + (4 * 8);
    if (directories < 0 || entry + 8 > optionalEnd || entry + 8 > image.Length
        || BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(directories - 4)) <= 4)
    {
        return null;
    }

    uint offset = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(entry));
    uint size = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(entry + 4));
    if (offset == 0 && size == 0)
    {
        return (optional + 64, entry, image.Length);
    }

    return offset >= entry + 8 && (long)offset + size == image.Length ? (optional + 64, entry, (int)offset) : null;
}

static byte[] Bytes(ZipArchiveEntry entry)
{
    using Stream stream = entry.Open();
    using var bytes = new MemoryStream();
    stream.CopyTo(bytes);
    return bytes.ToArray();
}

// A library with no code and one managed resource. Its identity comes from its contents, so the same list gives the
// same bytes.
static byte[] Library(byte[] resource)
{
    var metadata = new MetadataBuilder();
    metadata.AddModule(0, metadata.GetOrAddString(ListFile), metadata.GetOrAddGuid(new Guid(SHA256.HashData(resource).AsSpan(0, 16))), default, default);
    metadata.AddAssembly(metadata.GetOrAddString(Path.GetFileNameWithoutExtension(ListFile)), new Version(1, 0, 0, 0), default, default, default, AssemblyHashAlgorithm.Sha1);
    metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

    var resources = new BlobBuilder();
    metadata.AddManifestResource(ManifestResourceAttributes.Public, metadata.GetOrAddString(Resource), default, (uint)resources.Count);
    resources.WriteInt32(resource.Length);
    resources.WriteBytes(resource);

    var builder = new ManagedPEBuilder(
        PEHeaderBuilder.CreateLibraryHeader(),
        new MetadataRootBuilder(metadata),
        new BlobBuilder(),
        managedResources: resources,
        deterministicIdProvider: content =>
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (Blob blob in content)
            {
                ArraySegment<byte> bytes = blob.GetBytes();
                hash.AppendData(bytes.Array!, bytes.Offset, bytes.Count);
            }

            return BlobContentId.FromHash(hash.GetHashAndReset());
        });
    var image = new BlobBuilder();
    builder.Serialize(image);
    return image.ToArray();
}

static byte[] ReadResource(byte[] library)
{
    using var pe = new PEReader(new MemoryStream(library));
    MetadataReader metadata = pe.GetMetadataReader();
    foreach (ManifestResourceHandle handle in metadata.ManifestResources)
    {
        ManifestResource resource = metadata.GetManifestResource(handle);
        if (resource.Implementation.IsNil && metadata.StringComparer.Equals(resource.Name, Resource))
        {
            BlobReader reader = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress).GetReader();
            reader.Offset = checked((int)resource.Offset);
            return reader.ReadBytes(reader.ReadInt32());
        }
    }

    throw new InvalidDataException($"{ListFile} has no {Resource}");
}
