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
/// programs and libraries only; the files beside them (<c>*.runtimeconfig.json</c>, <c>*.deps.json</c>, the notices,
/// images) are covered by their SHA-256 in this signed library, so none of them can be changed, added or removed
/// without the publisher's certificate. The programs and libraries are listed too, by their digest without their
/// signature (<see cref="ProgramDigest"/>), so none of them can be swapped for another one signed by the publisher or
/// Microsoft (an older build, another program).
/// </summary>
/// <remarks>
/// The library holds one managed resource, <c>files.json</c>:
/// <c>{"files":{"&lt;path in the app folder&gt;":"&lt;sha256 hex&gt;"},"programs":{"&lt;path&gt;":"&lt;digest hex&gt;"}}</c>.
/// It is never loaded, only read. <c>sq.version</c>, which vpk writes into the app folder after the list was made, must
/// be a copy of the package's <c>.nuspec</c>. A list from before <c>programs</c> was added lists no programs.
/// </remarks>
internal static class UpdateFileList
{
    /// <summary>The files and programs a list names, by path in the app folder: SHA-256 (hex) and <see cref="ProgramDigest"/>.</summary>
    internal sealed record Contents(IReadOnlyDictionary<string, string> Files, IReadOnlyDictionary<string, string> Programs);

    /// <summary>The list's file name in the app folder.</summary>
    public const string FileName = "SemanticRouter.Files.dll";

    private const string Resource = "files.json";

    private const string VersionFile = "sq.version";

    /// <summary>Reads the list in <paramref name="library"/> into <paramref name="list"/>; returns why it cannot be read, or <see langword="null"/>.</summary>
    public static string? Load(string library, out Contents list)
    {
        try
        {
            list = Read(library);
            return null;
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or InvalidDataException or JsonException or FormatException or IOException)
        {
            list = new Contents(new Dictionary<string, string>(), new Dictionary<string, string>());
            return $"its list of files ({FileName}) cannot be read ({ex.Message})";
        }
    }

    /// <summary>
    /// Why the package's files that are not executable images (<paramref name="data"/>: zip entries, by path in the app
    /// folder) differ from the files in <paramref name="list"/>, or <see langword="null"/> when they are exactly the
    /// files it names, with the same contents.
    /// </summary>
    public static string? Refuses(Contents list, IEnumerable<(string Relative, ZipArchiveEntry Entry)> data, ZipArchiveEntry nuspec)
    {
        var listed = new Dictionary<string, string>(list.Files, StringComparer.Ordinal);
        foreach ((string relative, ZipArchiveEntry entry) in data)
        {
            if (relative == VersionFile)
            {
                if (!Bytes(entry).AsSpan().SequenceEqual(Bytes(nuspec)))
                {
                    return $"{VersionFile} is not a copy of its .nuspec";
                }
            }
            else if (!listed.Remove(relative, out string? expected))
            {
                return $"{relative} is not one of the files its publisher signed";
            }
            else
            {
                using Stream stream = entry.Open();
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{relative} is not the file its publisher signed";
                }
            }
        }

        // A listed file the package lacks, or has as a program instead.
        return listed.Keys.Order(StringComparer.Ordinal).FirstOrDefault() is { } missing ? $"{missing} is missing from its package" : null;
    }

    /// <summary>The files and programs the list in <paramref name="library"/> names.</summary>
    internal static Contents Read(string library)
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
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{Resource} has no files");
            }

            JsonElement? programs = document.RootElement.TryGetProperty("programs", out JsonElement listed) ? listed : null;
            if (programs is { ValueKind: not JsonValueKind.Object })
            {
                throw new InvalidDataException($"{Resource} has a list of programs that is not one");
            }

            return new Contents(Paths(files), programs is { } named ? Paths(named) : new Dictionary<string, string>());
        }

        throw new InvalidDataException($"it has no {Resource}");

        static Dictionary<string, string> Paths(JsonElement element)
        {
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty entry in element.EnumerateObject())
            {
                paths[entry.Name] = (entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() : null)
                    ?? throw new InvalidDataException($"{Resource} has no digest for {entry.Name}");
            }

            return paths;
        }
    }

    /// <summary>
    /// The digest of a Windows program or library without its Authenticode signature, as <c>deploy/update-file-list.cs</c>
    /// writes it into the list before vpk signs the release: the SHA-256 (hex) of the bytes Authenticode's own hash covers
    /// (all but the optional header's checksum, the certificate table's entry in the data directories and the certificate
    /// table at the end of the file), and, for a file without a signature, the zeros that signing pads it with to a
    /// multiple of eight bytes. So it is the same before and after the file is signed, or signed again. Of a file that is
    /// not a PE image laid out that way, it is the SHA-256 of the whole file.
    /// </summary>
    public static string ProgramDigest(byte[] image)
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

    /// <summary>
    /// Where a PE image's checksum and certificate table entry are, and where its certificate table starts (the end of
    /// the file when it has none). <see langword="false"/> when it is not a PE image, or its certificate table is not
    /// the last thing in the file, where signing puts it.
    /// </summary>
    private static bool Layout(byte[] image, out int checksum, out int certificateEntry, out int certificates)
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

    private static byte[] Bytes(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
