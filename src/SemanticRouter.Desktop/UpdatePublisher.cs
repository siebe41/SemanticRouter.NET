using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Desktop;

/// <summary>
/// The publisher check the machine policy asks for (<see cref="UpdatePolicy.Publisher"/>): before a downloaded update is
/// applied, the app in its package is unpacked to a temporary folder and its code signature checked by the operating
/// system, so a release asset or feed that was tampered with is not enough to change the app.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Windows: every executable image in the package (whatever its extension) must have a valid Authenticode
/// signature (<c>WinVerifyTrust</c>); the main executable and the app's own assemblies (<c>SemanticRouter.*</c>) must be
/// signed by the publisher, and the rest by the publisher or Microsoft.</item>
/// <item>macOS: the app bundle must pass <c>codesign --verify --deep --strict</c> with a Developer ID signature, and its
/// team ID must be the publisher.</item>
/// <item>Linux: the packages carry no signature, so every update is refused.</item>
/// </list>
/// </remarks>
internal sealed class UpdatePublisher(string publisher, UpdatePublisher.Platform platform, ICodeSignatures? signatures)
{
    /// <summary>How a package is laid out and checked: by the operating system the app runs on.</summary>
    internal enum Platform
    {
        Windows,
        MacOS,
        Linux,
    }

    /// <summary>
    /// The organization in Microsoft's code-signing certificates (the .NET runtime, ONNX Runtime). Their common names
    /// vary (<c>Microsoft Corporation</c>, <c>.NET</c>); the organization is what a certificate authority checks.
    /// </summary>
    internal const string Microsoft = "O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    /// <summary>Where the app's files are in a Velopack package.</summary>
    private const string AppFolder = "lib/app/";

    /// <summary>The publisher the policy names.</summary>
    public string Expected => publisher;

    /// <summary>The check for this machine, or <see langword="null"/> when the policy names no publisher.</summary>
    public static UpdatePublisher? ForThisMachine(string? publisher)
    {
        if (publisher is null)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return new UpdatePublisher(publisher, Platform.Windows, new AuthenticodeSignatures());
        }

        return OperatingSystem.IsMacOS()
            ? new UpdatePublisher(publisher, Platform.MacOS, new CodesignSignatures())
            : new UpdatePublisher(publisher, Platform.Linux, null);
    }

    /// <summary>Why the update in <paramref name="package"/> (a Velopack <c>.nupkg</c>) may not be applied, or <see langword="null"/> when it may.</summary>
    public string? Refuses(string package)
    {
        if (platform == Platform.Linux || signatures is null)
        {
            return "its publisher cannot be checked on Linux (the packages are not signed), and your organization's policy requires it";
        }

        string folder = Path.Combine(Path.GetTempPath(), "semantic-router-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(package);
            (string id, string mainExe) = Manifest(zip);
            return platform == Platform.Windows ? RefusesWindows(zip, id, mainExe, folder) : RefusesMacOS(zip, id, mainExe, folder);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return $"its package cannot be read ({ex.Message})";
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in the temporary folder; the operating system cleans it up.
            }
        }
    }

    private string? RefusesWindows(ZipArchive zip, string id, string mainExe, string folder)
    {
        // Every Windows executable image, whatever its name: a library renamed to .bin is loaded just the same once
        // SemanticRouter.deps.json names it, and that file carries no signature of its own.
        ZipArchiveEntry[] code = [.. zip.Entries.Where(e => e.FullName.StartsWith(AppFolder, StringComparison.Ordinal) && !e.FullName.EndsWith('/') && IsExecutableImage(e))];
        if (!code.Any(e => e.FullName == AppFolder + mainExe))
        {
            return $"its package has no {mainExe}";
        }

        // The main executable first, so the reason given is about the app when it is the one that is wrong.
        foreach (ZipArchiveEntry entry in code.OrderBy(e => e.FullName == AppFolder + mainExe ? 0 : 1))
        {
            string relative = entry.FullName[AppFolder.Length..];
            string file = Extract(entry, folder, relative);
            CodeSignature signature = signatures!.Read(file);
            if (signature.Problem is { } problem)
            {
                return $"{relative} {problem}";
            }

            // The app's own code must be the publisher's. The rest is the publisher's too (vpk signs every unsigned
            // file with the release's certificate) or Microsoft's (the .NET runtime, ONNX Runtime): a valid signature
            // from anyone else is not enough, or a cheap code-signing certificate would let a tampered package through.
            bool own = relative == mainExe || Path.GetFileName(relative).StartsWith(id + ".", StringComparison.OrdinalIgnoreCase);
            if (!Matches(signature) && (own || signature.Signer is not { } signer || !IsMicrosoft(signer)))
            {
                return $"{relative} is signed by {Describe(signature)}, not by {publisher}{(own ? string.Empty : " or Microsoft")}";
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="entry"/> is a Windows executable image (<c>MZ</c>): a program or a library.</summary>
    private static bool IsExecutableImage(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        Span<byte> header = stackalloc byte[2];
        return stream.ReadAtLeast(header, 2, throwOnEndOfStream: false) == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
    }

    private string? RefusesMacOS(ZipArchive zip, string id, string mainExe, string folder)
    {
        // The package holds the bundle's Contents folder: unpack it as an .app and let codesign check the whole bundle.
        string bundle = Path.Combine(folder, id + ".app");
        foreach (ZipArchiveEntry entry in zip.Entries.Where(e => e.FullName.StartsWith(AppFolder, StringComparison.Ordinal) && !e.FullName.EndsWith('/')))
        {
            Extract(entry, bundle, entry.FullName[AppFolder.Length..]);
        }

        string executable = Path.Combine(bundle, "Contents", "MacOS", mainExe);
        if (!File.Exists(executable))
        {
            return $"its package has no Contents/MacOS/{mainExe}";
        }

        if (!OperatingSystem.IsWindows())
        {
            // Velopack stores the files without their modes; the updater makes the executable runnable the same way.
            File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        CodeSignature signature = signatures!.Read(bundle);
        if (signature.Problem is { } problem)
        {
            return $"{id}.app {problem}";
        }

        return Matches(signature) ? null : $"{id}.app is signed by {Describe(signature)}, not by team {publisher}";
    }

    /// <summary>Whether <paramref name="signature"/> is the publisher's: the team ID on macOS; the subject, common name or thumbprint on Windows.</summary>
    internal bool Matches(CodeSignature signature)
    {
        if (platform == Platform.MacOS)
        {
            return signature.TeamId is { } team && string.Equals(team, publisher.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        return signature.Signer is { } signer && SignedBy(signer, publisher);
    }

    /// <summary>
    /// Whether <paramref name="certificate"/> is the one <paramref name="publisher"/> names: its SHA-1 or SHA-256
    /// thumbprint (hex, spaces and colons ignored), its whole subject (<c>CN=…, O=…</c>; the same names and values in
    /// any order, case ignored), or its common name.
    /// </summary>
    internal static bool SignedBy(X509Certificate2 certificate, string publisher)
    {
        string value = publisher.Trim();
        string hex = value.Replace(" ", string.Empty, StringComparison.Ordinal).Replace(":", string.Empty, StringComparison.Ordinal);
        if (hex.Length is 40 or 64 && hex.All(char.IsAsciiHexDigit))
        {
            string thumbprint = hex.Length == 40 ? certificate.Thumbprint : certificate.GetCertHashString(HashAlgorithmName.SHA256);
            return string.Equals(thumbprint, hex, StringComparison.OrdinalIgnoreCase);
        }

        if (value.Contains('=', StringComparison.Ordinal))
        {
            try
            {
                return Names(certificate.SubjectName).SequenceEqual(Names(new X500DistinguishedName(value)));
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        return string.Equals(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false), value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A name's parts as <c>oid=VALUE</c>, sorted, so the same names in any order compare equal.</summary>
    private static IEnumerable<string> Names(X500DistinguishedName name) => name.EnumerateRelativeDistinguishedNames()
        .Select(n => n.HasMultipleElements
            ? Convert.ToHexString(n.RawData.Span)
            : n.GetSingleElementType().Value + "=" + n.GetSingleElementValue()?.Trim().ToUpperInvariant())
        .Order(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="certificate"/> was issued to Microsoft: its subject is <see cref="Microsoft"/> plus a common name.</summary>
    internal static bool IsMicrosoft(X509Certificate2 certificate)
    {
        string[] names = [.. Names(certificate.SubjectName).Where(n => !n.StartsWith("2.5.4.3=", StringComparison.Ordinal))];
        return names.SequenceEqual(Names(new X500DistinguishedName(Microsoft)));
    }

    private static string Describe(CodeSignature signature) =>
        signature.TeamId is { } team ? "team " + team
        : signature.Signer is { } signer ? signer.Subject
        : "an unknown publisher";

    /// <summary>The package's id and main executable, from its <c>.nuspec</c>.</summary>
    private static (string Id, string MainExe) Manifest(ZipArchive zip)
    {
        ZipArchiveEntry nuspec = zip.Entries.FirstOrDefault(e => !e.FullName.Contains('/', StringComparison.Ordinal) && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("it has no .nuspec");
        using Stream stream = nuspec.Open();
        XElement? metadata = XDocument.Load(stream).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
        string? id = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "id")?.Value.Trim();
        string? mainExe = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "mainExe")?.Value.Trim();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(mainExe) || mainExe.Contains('/', StringComparison.Ordinal) || mainExe.Contains('\\', StringComparison.Ordinal) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("its .nuspec names no id or main executable");
        }

        return (id, mainExe);
    }

    /// <summary>Writes <paramref name="entry"/> to <paramref name="relative"/> under <paramref name="root"/>, never outside it.</summary>
    private static string Extract(ZipArchiveEntry entry, string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string file = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!file.StartsWith(fullRoot, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{entry.FullName} points outside the package");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        entry.ExtractToFile(file, overwrite: true);
        return file;
    }
}
