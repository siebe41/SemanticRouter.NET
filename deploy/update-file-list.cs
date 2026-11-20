// The list of a Windows build's files that its signature covers. Authenticode signs programs and libraries, not the
// data files beside them (SemanticRouter.runtimeconfig.json, SemanticRouter.deps.json, THIRD-PARTY-NOTICES.txt, …), so
// `write` puts the SHA-256 of every file in a published app that is not a Windows executable image into a small library,
// SemanticRouter.Files.dll, next to them. vpk signs that library with the rest of the release, and the desktop app's
// publisher check (src/SemanticRouter.Desktop/UpdateFileList.cs) refuses an update whose data files differ from it.
// The programs and libraries are in it too, each with the SHA-256 of what its Authenticode signature covers (the file
// without its signature, ProgramDigest below), which is the same before and after vpk signs it: so the app's check
// also refuses a program swapped for another one the publisher or Microsoft signed. Programs vpk adds after `write`
// (Velopack's Squirrel.exe and execution stub) are not in it; the app requires those to be signed by the publisher.
// `check` reads the list back from a Velopack package and fails unless it matches the package's files exactly, so a
// file added to the app after `write` ran, or a program vpk changed other than by signing it, is caught before the
// release, not by every updater in the field. A .NET 10 file-based app, with nothing to install:
//
//   dotnet run --file deploy/update-file-list.cs -- write <publish-dir>
//   dotnet run --file deploy/update-file-list.cs -- check <SemanticRouter-<version>-<rid>-full.nupkg>
//
// The library holds one managed resource, files.json:
// {"files":{"<path relative to the app folder>":"<sha256 hex>"},"programs":{"<path>":"<digest hex>"}}, paths with
// forward slashes, sorted. It is never loaded: no deps.json names it. Same files, same bytes.
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

const string ListFile = "SemanticRouter.Files.dll";
const string Resource = "files.json";
const string AppFolder = "lib/app/";

// vpk writes it into the app folder after the list was made: a copy of the package's .nuspec.
const string VersionFile = "sq.version";

if (args is ["write", var publish])
{
    string root = Path.GetFullPath(publish);
    var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
    var programs = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        if (relative == ListFile)
        {
            continue;
        }

        byte[] content = File.ReadAllBytes(file);
        if (IsExecutableImage(new MemoryStream(content)))
        {
            programs[relative] = ProgramDigest(content);
        }
        else
        {
            files[relative] = Convert.ToHexStringLower(SHA256.HashData(content));
        }
    }

    var json = new JsonObject
    {
        ["files"] = new JsonObject(files.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value))),
        ["programs"] = new JsonObject(programs.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
    };
    File.WriteAllBytes(Path.Combine(root, ListFile), Library(JsonSerializer.SerializeToUtf8Bytes(json)));
    Console.WriteLine($"{ListFile}: {files.Count} files that are not programs or libraries, and {programs.Count} programs and libraries");
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

    JsonNode written = JsonNode.Parse(ReadResource(Bytes(list)))!;
    var listed = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach ((string path, JsonNode? hash) in written["files"]!.AsObject())
    {
        listed[path] = hash!.GetValue<string>();
    }

    var programs = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach ((string path, JsonNode? digest) in written["programs"]?.AsObject() ?? [])
    {
        programs[path] = digest!.GetValue<string>();
    }

    byte[] nuspec = Bytes(zip.Entries.Single(e => !e.FullName.Contains('/', StringComparison.Ordinal) && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)));
    var problems = new List<string>();
    var added = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (ZipArchiveEntry entry in app)
    {
        string relative = entry.FullName[AppFolder.Length..];
        byte[] content = Bytes(entry);
        if (!seen.Add(relative))
        {
            problems.Add($"{relative} is in the package twice");
        }
        else if (relative == ListFile)
        {
            // Covered by its own signature.
        }
        else if (IsExecutableImage(new MemoryStream(content)))
        {
            // Listed by what its signature covers, which signing leaves as it was; one vpk added is not listed, and the
            // app requires it to be signed by the publisher.
            if (!programs.Remove(relative, out string? expected))
            {
                added.Add(relative);
            }
            else if (!string.Equals(ProgramDigest(content), expected, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{relative} was changed after deploy/update-file-list.cs write ran, other than by signing it");
            }
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
    problems.AddRange(programs.Keys.Select(path => $"{path} is in {ListFile} as a program but not in the package (or is no longer a program or library)"));
    foreach (string problem in problems)
    {
        Console.Error.WriteLine($"::error::{name}: {problem}");
    }

    if (problems.Count > 0)
    {
        return 1;
    }

    Console.WriteLine($"{name}: every file of the app is in {ListFile}"
        + (added.Count > 0 ? $", except the programs vpk added ({string.Join(", ", added)}), which must be signed by the publisher" : string.Empty));
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

// What a program's Authenticode signature covers, so the same before and after signing, exactly as the desktop app
// computes it (UpdateFileList.ProgramDigest): the SHA-256 of the file without the optional header's checksum, the
// certificate table's entry in the data directories and the certificate table at the end, plus, for an unsigned file,
// the zeros signing pads it with to a multiple of eight bytes. Of a file that is not a PE image laid out that way, the
// SHA-256 of the whole file.
static string ProgramDigest(byte[] image)
{
    if (!Layout(image, out int checksum, out int certificateEntry, out int certificates))
    {
        return Convert.ToHexStringLower(SHA256.HashData(image));
    }

    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    hash.AppendData(image, 0, checksum);
    hash.AppendData(image, checksum + 4, certificateEntry - checksum - 4);
    hash.AppendData(image, certificateEntry + 8, certificates - certificateEntry - 8);
    hash.AppendData(new byte[(8 - (certificates % 8)) % 8]);
    return Convert.ToHexStringLower(hash.GetHashAndReset());
}

static bool Layout(byte[] image, out int checksum, out int certificateEntry, out int certificates)
{
    (checksum, certificateEntry, certificates) = (0, 0, 0);
    if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
    {
        return false;
    }

    int header = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C));
    if (header < 0x40 || header > image.Length - 26 || !image.AsSpan(header, 4).SequenceEqual("PE\0\0"u8))
    {
        return false;
    }

    int optional = header + 24;
    (int count, int directories) = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(optional)) switch
    {
        0x10b => (optional + 92, optional + 96),
        0x20b => (optional + 108, optional + 112),
        _ => (-1, -1),
    };
    if (count < 0 || directories + (5 * 8) > image.Length || BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(count)) < 5)
    {
        return false;
    }

    checksum = optional + 64;
    certificateEntry = directories + (4 * 8);
    uint offset = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(certificateEntry));
    uint size = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(certificateEntry + 4));
    if (size == 0)
    {
        certificates = image.Length;
        return true;
    }

    if (offset < certificateEntry + 8 || (long)offset + size != image.Length)
    {
        return false;
    }

    certificates = (int)offset;
    return true;
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
