using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace SemanticRouter.Desktop;

/// <summary>
/// What the operating system says about a file's or app bundle's code signature: why it is not valid
/// (<see cref="Problem"/>, worded to follow the file's name), or who signed it.
/// </summary>
/// <param name="Problem">Why the signature is missing or not valid; <see langword="null"/> when it is valid.</param>
/// <param name="Signer">Windows: the Authenticode signer's certificate.</param>
/// <param name="TeamId">macOS: the Developer ID team ID.</param>
internal sealed record CodeSignature(string? Problem, X509Certificate2? Signer = null, string? TeamId = null);

/// <summary>Reads code signatures with the operating system's own checks; a seam for tests.</summary>
internal interface ICodeSignatures
{
    /// <summary>The signature of a file (Windows) or an app bundle (macOS).</summary>
    CodeSignature Read(string path);
}

/// <summary>
/// Windows: <c>WinVerifyTrust</c>, the check Explorer and SmartScreen use, for a valid Authenticode signature that
/// chains to a trusted root; then the signer's certificate. Revocation is not checked (it needs the certificate
/// authority's servers, and an internal feed may have no way out).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AuthenticodeSignatures : ICodeSignatures
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public CodeSignature Read(string path)
    {
        int result = Verify(path);
        if (result != 0)
        {
            return new CodeSignature(unchecked((uint)result) switch
            {
                0x800B0100 => "is not signed",
                0x80096010 => "was changed after it was signed",
                0x800B0109 or 0x800B010A => "is signed with a certificate that is not trusted on this machine",
                0x800B0101 => "is signed with a certificate that had expired",
                0x800B0111 => "is signed with a certificate this machine distrusts",
                uint other => $"has no valid signature (0x{other:X8})",
            });
        }

        // Only now that Windows has checked the signature: whose it is.
#pragma warning disable SYSLIB0057 // Reading the signer of a signed file has no X509CertificateLoader equivalent.
        using X509Certificate signer = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
        return new CodeSignature(null, X509CertificateLoader.LoadCertificate(signer.GetRawCertData()));
    }

    private static int Verify(string path)
    {
        IntPtr name = Marshal.StringToHGlobalUni(path);
        var file = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = name };
        IntPtr fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, fileInfo, fDeleteOld: false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 0, // WTD_REVOKE_NONE
                UnionChoice = 1, // WTD_CHOICE_FILE
                FileInfo = fileInfo,
                StateAction = 1, // WTD_STATEACTION_VERIFY
            };
            Guid action = GenericVerifyV2;
            int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            data.StateAction = 2; // WTD_STATEACTION_CLOSE
            _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfo);
            Marshal.FreeHGlobal(name);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}

/// <summary>
/// macOS: <c>codesign --verify --deep --strict</c> against Apple's Developer ID requirement (a certificate Apple issued
/// for distribution outside the App Store), then the team ID from <c>codesign -dv</c>.
/// </summary>
internal sealed class CodesignSignatures : ICodeSignatures
{
    private const string Codesign = "/usr/bin/codesign";

    /// <summary>Apple's designated requirement for Developer ID applications.</summary>
    internal const string DeveloperId =
        "anchor apple generic and certificate 1[field.1.2.840.113635.100.6.2.6] exists and certificate leaf[field.1.2.840.113635.100.6.1.13] exists";

    public CodeSignature Read(string path)
    {
        (int verified, string verifyOutput) = Run("--verify", "--deep", "--strict", "-R", "=" + DeveloperId, path);
        if (verified != 0)
        {
            string detail = LastLine(verifyOutput);
            return new CodeSignature(verifyOutput.Contains("not signed at all", StringComparison.Ordinal) ? "is not signed"
                : verifyOutput.Contains("satisfy", StringComparison.Ordinal) ? "is not signed with a Developer ID"
                : $"has no valid signature ({detail})");
        }

        (int displayed, string details) = Run("-dv", "--verbose=2", path);
        string? team = displayed != 0 ? null : details.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("TeamIdentifier=", StringComparison.Ordinal))?["TeamIdentifier=".Length..];
        return team is null or "not set" ? new CodeSignature("has no team ID in its signature") : new CodeSignature(null, TeamId: team);

        static string LastLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "codesign failed";
    }

    /// <summary>Runs codesign; its findings go to standard error.</summary>
    private static (int ExitCode, string Output) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Codesign)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using Process codesign = Process.Start(start) ?? throw new InvalidOperationException("codesign did not start.");
            Task<string> output = codesign.StandardOutput.ReadToEndAsync();
            string error = codesign.StandardError.ReadToEnd();
            if (!codesign.WaitForExit(120_000))
            {
                codesign.Kill(entireProcessTree: true);
                return (-1, "codesign did not finish");
            }

            return (codesign.ExitCode, error + output.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, ex.Message);
        }
    }
}
