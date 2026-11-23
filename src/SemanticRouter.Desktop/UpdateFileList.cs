using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace SemanticRouter.Desktop;

/// <summary>
/// The list of a Windows release's files that its signature covers: <c>SemanticRouter.Files.dll</c>, written into the
/// published app by <c>deploy/update-file-list.cs</c> and signed by vpk with the rest of the release. Authenticode signs
/// programs and libraries only, each on its own; the files beside them (<c>*.runtimeconfig.json</c>, <c>*.deps.json</c>,
/// the notices, images) are covered by their SHA-256 in this signed library, and so are the programs and libraries
/// themselves (<see cref="ImageDigest"/>), so none of them can be changed, added, removed, renamed or swapped for another
/// signed file without the publisher's certificate.
/// </summary>
/// <remarks>
/// The library holds one managed resource, <c>files.json</c>:
/// <c>{"files":{"&lt;path in the app folder&gt;":"&lt;sha256 hex&gt;"},"executables":{"&lt;path&gt;":"&lt;image digest hex&gt;"}}</c>.
/// It is never loaded, only read. Lists of releases up to 0.6.1 have no <c>executables</c> and are refused. Only what
/// vpk adds after the list was made is not in it: <c>sq.version</c>, which must be a copy of the package's
/// <c>.nuspec</c>, and Velopack's own programs (<see cref="Packaged"/>), which must be signed by the publisher.
/// </remarks>
internal static class UpdateFileList
{
    /// <summary>The list's file name in the app folder.</summary>
    public const string FileName = "SemanticRouter.Files.dll";

    /// <summary>The app's package id and the name its files start with.</summary>
    public const string AppId = "SemanticRouter";

    private const string Resource = "files.json";

    private const string VersionFile = "sq.version";

    /// <summary>
    /// The programs vpk adds to the app folder after the list was made: Velopack's updater and the stub that starts the
    /// app. vpk signs them with the release's certificate, so the publisher check requires the publisher's signature.
    /// </summary>
    public static readonly IReadOnlySet<string> Packaged = new HashSet<string>(StringComparer.Ordinal) { "Squirrel.exe", AppId + "_ExecutionStub.exe" };

    /// <summary>What the list names: data files by their SHA-256, executable images by <see cref="ImageDigest"/>.</summary>
    internal sealed record Listed(Dictionary<string, string> Files, Dictionary<string, string>? Executables);

    /// <summary>
    /// Why the package's files in the app folder (<paramref name="files"/>: zip entries, by path in the app folder, and
    /// whether each is an executable image) differ from the list in <paramref name="library"/>, or <see langword="null"/>
    /// when they are exactly the files it names, with the same contents.
    /// </summary>
    public static string? Refuses(string library, IEnumerable<(string Relative, ZipArchiveEntry Entry, bool Executable)> files, ZipArchiveEntry nuspec)
    {
        Listed listed;
        try
        {
            listed = Read(library);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or InvalidDataException or JsonException or FormatException)
        {
            return $"its list of files ({FileName}) cannot be read ({ex.Message})";
        }

        if (listed.Executables is not { } executables)
        {
            return $"its list of files ({FileName}) does not cover its programs and libraries, as the lists of releases up to 0.6.1 do not";
        }

        foreach ((string relative, ZipArchiveEntry entry, bool executable) in files)
        {
            if (relative == FileName || (executable && Packaged.Contains(relative)))
            {
                // Checked by their own signature, which must be the publisher's.
                continue;
            }

            if (relative == VersionFile)
            {
                if (!Bytes(entry).AsSpan().SequenceEqual(Bytes(nuspec)))
                {
                    return $"{VersionFile} is not a copy of its .nuspec";
                }
            }
            else if (!(executable ? executables : listed.Files).Remove(relative, out string? expected))
            {
                return $"{relative} is not one of the files its publisher signed";
            }
            else
            {
                string actual = executable ? ImageDigest(Bytes(entry)) : Sha256(entry);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{relative} is not the file its publisher signed";
                }
            }
        }

        // A listed file the package lacks, or has as a program instead (or the other way round).
        return listed.Files.Keys.Concat(executables.Keys).Order(StringComparer.Ordinal).FirstOrDefault() is { } missing ? $"{missing} is missing from its package" : null;
    }

    /// <summary>What the list in <paramref name="library"/> names, by path in the app folder.</summary>
    internal static Listed Read(string library)
    {
        using FileStream file = File.OpenRead(library);
        using var pe = new PEReader(file);
        MetadataReader metadata = pe.GetMetadataReader();
        foreach (ManifestResourceHandle handle in metadata.ManifestResources)
        {
            ManifestResource resource = metadata.GetManifestResource(handle);
            if (!resource.Implementation.IsNil || !metadata.StringComparer.Equals(resource.Name, Resource))
            {
                continue;
            }

            // Managed resources: each a 4-byte length and the bytes, at its offset in the CLI header's resources block.
            BlobReader reader = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress).GetReader();
            reader.Offset = checked((int)resource.Offset);
            int length = reader.ReadInt32();
            if (length < 0 || length > reader.RemainingBytes)
            {
                throw new BadImageFormatException($"{Resource} runs past the end of the file");
            }

            using JsonDocument document = JsonDocument.Parse(reader.ReadBytes(length));
            if (!document.RootElement.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{Resource} has no files");
            }

            return new Listed(
                Hashes(files),
                document.RootElement.TryGetProperty("executables", out JsonElement executables) && executables.ValueKind == JsonValueKind.Object ? Hashes(executables) : null);
        }

        throw new InvalidDataException($"it has no {Resource}");

        static Dictionary<string, string> Hashes(JsonElement section)
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty entry in section.EnumerateObject())
            {
                hashes[entry.Name] = entry.Value.GetString() ?? throw new InvalidDataException($"{Resource} has no SHA-256 for {entry.Name}");
            }

            return hashes;
        }
    }

    /// <summary>
    /// The SHA-256 (hex) of an executable image without its Authenticode signature, which vpk adds after the list was
    /// made: the image with its checksum, the certificate table's entry in the data directories and the certificate table
    /// left out, and without the zeros before that table (a signer pads the image to eight bytes). Signing the image, or
    /// signing it again, does not change it; any other change does. A file that is not a PE image, or whose certificate
    /// table is anywhere but at its end, is hashed whole. <c>deploy/update-file-list.cs</c> computes the same.
    /// </summary>
    internal static string ImageDigest(byte[] image)
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

    /// <summary>
    /// Where a PE image's checksum and certificate table entry are, and where its certificate table starts (its length
    /// when it has none); <see langword="null"/> when it is not a PE image with both, or its certificate table does not
    /// end the file.
    /// </summary>
    private static (int Checksum, int Entry, int End)? Layout(byte[] image)
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

    private static string Sha256(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static byte[] Bytes(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
