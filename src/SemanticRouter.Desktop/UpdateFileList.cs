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
/// without the publisher's certificate.
/// </summary>
/// <remarks>
/// The library holds one managed resource, <c>files.json</c>: <c>{"files":{"&lt;path in the app folder&gt;":"&lt;sha256 hex&gt;"}}</c>.
/// It is never loaded, only read. <c>sq.version</c>, which vpk writes into the app folder after the list was made, must
/// be a copy of the package's <c>.nuspec</c>.
/// </remarks>
internal static class UpdateFileList
{
    /// <summary>The list's file name in the app folder.</summary>
    public const string FileName = "SemanticRouter.Files.dll";

    private const string Resource = "files.json";

    private const string VersionFile = "sq.version";

    /// <summary>
    /// Why the package's files that are not executable images (<paramref name="data"/>: zip entries, by path in the app
    /// folder) differ from the list in <paramref name="library"/>, or <see langword="null"/> when they are exactly the
    /// files it names, with the same contents.
    /// </summary>
    public static string? Refuses(string library, IEnumerable<(string Relative, ZipArchiveEntry Entry)> data, ZipArchiveEntry nuspec)
    {
        Dictionary<string, string> listed;
        try
        {
            listed = Read(library);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or InvalidDataException or JsonException or FormatException)
        {
            return $"its list of files ({FileName}) cannot be read ({ex.Message})";
        }

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

    /// <summary>The files the list in <paramref name="library"/> names, by path in the app folder, with their SHA-256 (hex).</summary>
    internal static Dictionary<string, string> Read(string library)
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

            var listed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty entry in files.EnumerateObject())
            {
                listed[entry.Name] = entry.Value.GetString() ?? throw new InvalidDataException($"{Resource} has no SHA-256 for {entry.Name}");
            }

            return listed;
        }

        throw new InvalidDataException($"it has no {Resource}");
    }

    private static byte[] Bytes(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
