// The list of a Windows build's files that its signature covers. Authenticode signs programs and libraries, not the
// data files beside them (SemanticRouter.runtimeconfig.json, SemanticRouter.deps.json, THIRD-PARTY-NOTICES.txt, …), so
// `write` puts the SHA-256 of every file in a published app that is not a Windows executable image into a small library,
// SemanticRouter.Files.dll, next to them. vpk signs that library with the rest of the release, and the desktop app's
// publisher check (src/SemanticRouter.Desktop/UpdateFileList.cs) refuses an update whose data files differ from it.
// `check` reads the list back from a Velopack package and fails unless it matches the package's files exactly, so a
// file added to the app after `write` ran is caught before the release, not by every updater in the field. A .NET 10
// file-based app, with nothing to install:
//
//   dotnet run --file deploy/update-file-list.cs -- write <publish-dir>
//   dotnet run --file deploy/update-file-list.cs -- check <SemanticRouter-<version>-<rid>-full.nupkg>
//
// The library holds one managed resource, files.json: {"files":{"<path relative to the app folder>":"<sha256 hex>"}},
// paths with forward slashes, sorted. It is never loaded: no deps.json names it. Same files, same bytes.
#:property PublishAot=false

using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

const string ListFile = "SemanticRouter.Files.dll";
const string Resource = "files.json";
const string AppFolder = "lib/app/";

// vpk writes it into the app folder after the list was made: a copy of the package's .nuspec.
const string VersionFile = "sq.version";

if (args is ["write", var publish])
{
    string root = Path.GetFullPath(publish);
    var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        using FileStream stream = File.OpenRead(file);
        if (relative != ListFile && !IsExecutableImage(stream))
        {
            stream.Position = 0;
            files[relative] = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
    }

    var json = new JsonObject { ["files"] = new JsonObject(files.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value))) };
    File.WriteAllBytes(Path.Combine(root, ListFile), Library(JsonSerializer.SerializeToUtf8Bytes(json)));
    Console.WriteLine($"{ListFile}: {files.Count} files that are not programs or libraries");
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

    var listed = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach ((string path, JsonNode? hash) in JsonNode.Parse(ReadResource(Bytes(list)))!["files"]!.AsObject())
    {
        listed[path] = hash!.GetValue<string>();
    }

    byte[] nuspec = Bytes(zip.Entries.Single(e => !e.FullName.Contains('/', StringComparison.Ordinal) && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)));
    var problems = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (ZipArchiveEntry entry in app)
    {
        string relative = entry.FullName[AppFolder.Length..];
        byte[] content = Bytes(entry);
        if (!seen.Add(relative))
        {
            problems.Add($"{relative} is in the package twice");
        }
        else if (IsExecutableImage(new MemoryStream(content)))
        {
            // Covered by its own signature.
        }
        else if (relative == VersionFile)
        {
            if (!content.AsSpan().SequenceEqual(nuspec))
            {
                problems.Add($"{VersionFile} is not a copy of the package's .nuspec");
            }
        }
        else if (!listed.Remove(relative, out string? expected))
        {
            problems.Add($"{relative} is not in {ListFile}: it was added after deploy/update-file-list.cs write ran");
        }
        else if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), expected, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"{relative} was changed after deploy/update-file-list.cs write ran");
        }
    }

    problems.AddRange(listed.Keys.Select(path => $"{path} is in {ListFile} but not in the package (or is now a program or library)"));
    foreach (string problem in problems)
    {
        Console.Error.WriteLine($"::error::{name}: {problem}");
    }

    if (problems.Count > 0)
    {
        return 1;
    }

    Console.WriteLine($"{name}: every file that is not a program or library is in {ListFile}");
    return 0;
}

Console.Error.WriteLine("Usage: dotnet run --file deploy/update-file-list.cs -- write <publish-dir> | check <full.nupkg>");
return 2;

// A Windows program or library starts with MZ; the desktop app's check tells them apart the same way.
static bool IsExecutableImage(Stream stream)
{
    Span<byte> header = stackalloc byte[2];
    return stream.ReadAtLeast(header, 2, throwOnEndOfStream: false) == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
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
