using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using SemanticRouter.AspNetCore;
using SemanticRouter.Desktop;
using SemanticRouter.Host;
using Velopack;
using static SemanticRouter.Tests.Desktop.InstallationTests;

namespace SemanticRouter.Tests.Desktop;

/// <summary>
/// Desktop updates under an administrator's control (#103): the policy's Notify and Publisher settings, notify-only
/// updates that are never applied on quit, and the publisher check before an update is applied.
/// </summary>
public sealed class UpdateControlTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("updates").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Notify_and_the_publisher_are_read_from_every_policy_source()
    {
        // JSON (Linux, containers), and what plutil makes of a macOS configuration profile.
        MachinePolicy json = MachinePolicy.FromJson("""
            { "Updates": { "Mode": "Feed", "Feed": "https://updates.corp.example/router", "Apply": "Notify", "Publisher": "A1B2C3D4E5" } }
            """);
        (json.Updates.Mode, json.Updates.Apply, json.Updates.Publisher).Should().Be((UpdateMode.Feed, UpdateApply.Notify, "A1B2C3D4E5"));
        json.IsEmpty.Should().BeFalse();

        // HKLM\Software\Policies\SemanticRouter\Updates: strings in a subkey.
        JsonObject root = MachinePolicyLoader.FromValues(new Dictionary<string, object?>());
        root["Updates"] = MachinePolicyLoader.FromValues(new Dictionary<string, object?>
        {
            ["Apply"] = "Notify",
            ["Publisher"] = "CN=Contoso Ltd, O=Contoso Ltd, C=US",
        });
        MachinePolicy registry = MachinePolicy.FromJson(root.ToJsonString(), @"HKLM\Software\Policies\SemanticRouter");
        (registry.Updates.Mode, registry.Updates.Apply, registry.Updates.Publisher).Should().Be((UpdateMode.Default, UpdateApply.Notify, "CN=Contoso Ltd, O=Contoso Ltd, C=US"));

        // Each setting alone is a policy; an existing Mode: Feed document means what it did.
        MachinePolicy.FromJson("""{"Updates":{"Apply":"Notify"}}""").IsEmpty.Should().BeFalse();
        MachinePolicy.FromJson("""{"Updates":{"Publisher":"A1B2C3D4E5"}}""").IsEmpty.Should().BeFalse();
        MachinePolicy.FromJson("""{"Updates":{"Mode":"Feed","Feed":"https://m.corp.example/"}}""").Updates.Should()
            .Match<UpdatePolicy>(u => u.Apply == UpdateApply.Default && u.Publisher == null);

        FluentActions.Invoking(() => MachinePolicy.FromJson("""{"Updates":{"Apply":"Silently"}}""")).Should().Throw<InvalidDataException>();
        FluentActions.Invoking(() => MachinePolicy.FromJson("""{"Updates":{"Publisher":"  "}}""")).Should().Throw<InvalidDataException>().WithMessage("*Updates:Publisher*");
        FluentActions.Invoking(() => MachinePolicy.FromJson("""{"Updates":{"Publisher":"CN=, =x"}}""")).Should().Throw<InvalidDataException>().WithMessage("*certificate subject*");
    }

    [Fact]
    public void On_macOS_a_configuration_profile_sets_notify_and_the_team()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string plist = Path.Combine(_directory, "policy.plist");
        File.WriteAllText(plist, """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>PayloadUUID</key><string>2F9C4F6A-0000-4000-8000-000000000000</string>
              <key>Updates</key>
              <dict><key>Apply</key><string>Notify</string><key>Publisher</key><string>A1B2C3D4E5</string></dict>
            </dict>
            </plist>
            """);

        MachinePolicy policy = MachinePolicyLoader.FromPropertyList(plist);

        (policy.Updates.Apply, policy.Updates.Publisher).Should().Be((UpdateApply.Notify, "A1B2C3D4E5"));
    }

    [Fact]
    public async Task By_default_an_update_is_downloaded_and_applied_when_the_app_quits()
    {
        var updates = new FakeUpdates();
        var platform = new RecordingPlatform();
        var updater = new DesktopUpdater(updates, platform, TextWriter.Null);

        (await updater.CheckAsync()).Should().Be("1.0.1");

        (updater.Ready, updater.Offer).Should().Be(("1.0.1", "1.0.1"));
        (await updater.ApplyOnQuitAsync()).Should().BeTrue();
        updates.Calls.Should().Equal("check", "download", "apply 1.0.1 restart=False");
    }

    [Fact]
    public async Task Notify_announces_an_update_and_never_applies_it_on_quit()
    {
        var updates = new FakeUpdates();
        var platform = new RecordingPlatform { Installed = true };
        var updater = new DesktopUpdater(updates, platform, TextWriter.Null, apply: UpdateApply.Notify);

        (await updater.CheckAsync()).Should().Be("1.0.1");

        (updater.Ready, updater.Available, updater.Offer).Should().Be(((string?)null, "1.0.1", "1.0.1"), "only announced: nothing is downloaded yet");
        (await updater.ApplyOnQuitAsync()).Should().BeFalse();
        updates.Calls.Should().Equal("check");
        platform.Calls.Should().BeEmpty("the background service is not stopped for an update that is not applied");

        // The person chose Restart to update: now it is downloaded and applied, and the app comes back.
        (await updater.ApplyNowAsync(restartApp: true)).Should().BeTrue();
        updates.Calls.Should().Equal("check", "download", "apply 1.0.1 restart=True");
        platform.Calls.Should().Equal("stop");
    }

    [Fact]
    public async Task Notify_does_not_apply_an_update_an_earlier_run_downloaded()
    {
        var updates = new FakeUpdates { Downloaded = true };
        var updater = new DesktopUpdater(updates, null, TextWriter.Null, apply: UpdateApply.Notify);

        updater.Offer.Should().Be("1.0.1");
        (await updater.ApplyOnQuitAsync()).Should().BeFalse();
        updates.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_policy_locks_notify_and_the_users_own_preference_is_honoured()
    {
        var locked = new DesktopUpdater(new FakeUpdates { Downloaded = true }, null, TextWriter.Null, apply: UpdateApply.Notify) { ApplyOnQuit = true };
        (locked.NotifyLocked, locked.NotifyOnly).Should().Be((true, true), "the user's preference cannot turn the policy's Notify back to automatic");
        (await locked.ApplyOnQuitAsync()).Should().BeFalse();

        var updates = new FakeUpdates();
        var chosen = new DesktopUpdater(updates, null, TextWriter.Null) { ApplyOnQuit = false };
        (chosen.NotifyLocked, chosen.NotifyOnly).Should().Be((false, true));
        await chosen.CheckAsync();
        (await chosen.ApplyOnQuitAsync()).Should().BeFalse();
        updates.Calls.Should().Equal("check");

        new DesktopPreferences().ApplyUpdatesOnQuit.Should().BeTrue("individual users keep updates applied on quit unless they choose otherwise");
        DesktopPreferences.Load(_directory).ApplyUpdatesOnQuit.Should().BeTrue();
        new DesktopPreferences { ApplyUpdatesOnQuit = false }.Save(_directory);
        DesktopPreferences.Load(_directory).ApplyUpdatesOnQuit.Should().BeFalse();
    }

    [Fact]
    public async Task Without_a_publisher_in_the_policy_the_package_is_not_inspected()
    {
        var updates = new FakeUpdates { Package = null };
        var updater = new DesktopUpdater(updates, null, TextWriter.Null);

        await updater.CheckAsync();

        (await updater.ApplyOnQuitAsync()).Should().BeTrue();
        updates.Calls.Should().NotContain("package");
    }

    [Fact]
    public async Task An_update_signed_by_the_publisher_is_applied()
    {
        var updates = new FakeUpdates { Package = WindowsPackage() };
        var publisher = new UpdatePublisher("CN=Contoso Ltd, O=Contoso Ltd, C=US", UpdatePublisher.Platform.Windows, Signatures(Contoso));
        var log = new StringWriter();
        var updater = new DesktopUpdater(updates, null, log, publisher: publisher);

        (await updater.CheckAsync()).Should().Be("1.0.1");

        (await updater.ApplyOnQuitAsync()).Should().BeTrue();
        updates.Calls.Should().Equal("check", "download", "package", "apply 1.0.1 restart=False");
        updater.Problem.Should().BeNull();
        log.ToString().Should().Contain("Update 1.0.1 is signed by CN=Contoso Ltd");
    }

    [Fact]
    public async Task An_update_signed_by_someone_else_is_refused_deleted_and_not_offered_again()
    {
        var updates = new FakeUpdates { Package = WindowsPackage() };
        using X509Certificate2 mallory = Certificate("CN=Mallory, O=Mallory, C=US");
        var publisher = new UpdatePublisher("CN=Contoso Ltd, O=Contoso Ltd, C=US", UpdatePublisher.Platform.Windows, Signatures(mallory));
        var log = new StringWriter();
        var updater = new DesktopUpdater(updates, new RecordingPlatform { Installed = true }, log, publisher: publisher);

        (await updater.CheckAsync()).Should().BeNull();

        updater.Problem.Should().Be("Update 1.0.1 was refused: SemanticRouter.exe is signed by CN=Mallory, O=Mallory, C=US, not by CN=Contoso Ltd, O=Contoso Ltd, C=US");
        (updater.Ready, updater.Offer).Should().Be(((string?)null, (string?)null));
        log.ToString().Should().Contain("Your organization's policy accepts only updates signed by CN=Contoso Ltd");
        (await updater.ApplyOnQuitAsync()).Should().BeFalse();
        (await updater.ApplyNowAsync(restartApp: true)).Should().BeFalse();
        await updater.CheckAsync();
        updates.Calls.Should().Equal("check", "download", "package", "discard", "check");
    }

    [Fact]
    public async Task An_unsigned_update_is_refused_even_when_an_earlier_run_downloaded_it()
    {
        var updates = new FakeUpdates { Package = WindowsPackage(), Downloaded = true };
        var publisher = new UpdatePublisher("Contoso Ltd", UpdatePublisher.Platform.Windows, new FakeSignatures(_ => new CodeSignature("is not signed")));
        var updater = new DesktopUpdater(updates, null, TextWriter.Null, publisher: publisher);

        (await updater.ApplyOnQuitAsync()).Should().BeFalse();

        updater.Problem.Should().Be("Update 1.0.1 was refused: SemanticRouter.exe is not signed");
        updates.Calls.Should().Equal("package", "discard");
    }

    [Fact]
    public async Task On_Linux_a_publisher_in_the_policy_refuses_every_update()
    {
        var updates = new FakeUpdates { Package = WindowsPackage() };
        var updater = new DesktopUpdater(updates, null, TextWriter.Null, publisher: new UpdatePublisher("Contoso Ltd", UpdatePublisher.Platform.Linux, null));

        await updater.CheckAsync();

        updater.Problem.Should().Contain("cannot be checked on Linux");
        (await updater.ApplyOnQuitAsync()).Should().BeFalse();
        updates.Calls.Should().NotContain(c => c.StartsWith("apply", StringComparison.Ordinal));
        UpdatePublisher.ForThisMachine(null).Should().BeNull("no publisher in the policy: no check");
    }

    [Fact]
    public void On_Windows_the_apps_own_code_must_be_the_publishers_and_the_rest_validly_signed()
    {
        using X509Certificate2 microsoft = Certificate("CN=Microsoft Corporation, O=Microsoft Corporation, C=US");
        string package = WindowsPackage();
        UpdatePublisher Check(Func<string, CodeSignature> read) => new("Contoso Ltd", UpdatePublisher.Platform.Windows, new FakeSignatures(read));
        CodeSignature Ours(string file) => Path.GetFileName(file).StartsWith("SemanticRouter", StringComparison.Ordinal) ? new CodeSignature(null, Contoso) : new CodeSignature(null, microsoft);

        Check(Ours).Refuses(package).Should().BeNull("the app is Contoso's; the runtime and the updater are signed by their makers");
        Check(f => Path.GetFileName(f) == "SemanticRouter.Core.dll" ? new CodeSignature(null, microsoft) : Ours(f)).Refuses(package)
            .Should().Be("SemanticRouter.Core.dll is signed by CN=Microsoft Corporation, O=Microsoft Corporation, C=US, not by Contoso Ltd");
        Check(f => Path.GetFileName(f) == "onnxruntime.dll" ? new CodeSignature("is not signed") : Ours(f)).Refuses(package)
            .Should().Be("onnxruntime.dll is not signed");
        Check(f => Path.GetFileName(f) == "Squirrel.exe" ? new CodeSignature("was changed after it was signed") : Ours(f)).Refuses(package)
            .Should().Be("Squirrel.exe was changed after it was signed");

        Check(Ours).Refuses(Package("noexe.nupkg", "SemanticRouter.exe", ["lib/app/SemanticRouter.dll"])).Should().Be("its package has no SemanticRouter.exe");
        Check(Ours).Refuses(Package("escape.nupkg", "SemanticRouter.exe", ["lib/app/SemanticRouter.exe", "lib/app/../../evil.dll"])).Should().Contain("points outside the package");
        Check(Ours).Refuses(Path.Combine(_directory, "missing.nupkg")).Should().StartWith("its package cannot be read");
    }

    [Fact]
    public void On_macOS_the_whole_bundle_is_checked_and_its_team_compared()
    {
        string package = Package("osx.nupkg", "SemanticRouter", ["lib/app/Contents/Info.plist", "lib/app/Contents/MacOS/SemanticRouter", "lib/app/Contents/MacOS/SemanticRouter.dll"]);
        var seen = new List<string>();
        UpdatePublisher Check(CodeSignature signature) => new("A1B2C3D4E5", UpdatePublisher.Platform.MacOS, new FakeSignatures(path =>
        {
            seen.Add(path);
            File.Exists(Path.Combine(path, "Contents", "MacOS", "SemanticRouter")).Should().BeTrue("codesign is given the unpacked bundle");
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(Path.Combine(path, "Contents", "MacOS", "SemanticRouter")).Should().HaveFlag(UnixFileMode.UserExecute);
            }

            return signature;
        }));

        Check(new CodeSignature(null, TeamId: "A1B2C3D4E5")).Refuses(package).Should().BeNull();
        seen.Should().ContainSingle().Which.Should().EndWith("SemanticRouter.app");
        Check(new CodeSignature(null, TeamId: "ZZZZZZZZZZ")).Refuses(package).Should().Be("SemanticRouter.app is signed by team ZZZZZZZZZZ, not by team A1B2C3D4E5");
        Check(new CodeSignature("is not signed with a Developer ID")).Refuses(package).Should().Be("SemanticRouter.app is not signed with a Developer ID");
        seen.Should().OnlyContain(bundle => !Directory.Exists(bundle), "the unpacked bundle is removed after the check");
    }

    [Fact]
    public void A_Windows_publisher_is_a_subject_a_common_name_or_a_thumbprint()
    {
        X509Certificate2 contoso = Contoso;
        string sha256 = contoso.GetCertHashString(HashAlgorithmName.SHA256);

        UpdatePublisher.SignedBy(contoso, "CN=Contoso Ltd, O=Contoso Ltd, C=US").Should().BeTrue();
        UpdatePublisher.SignedBy(contoso, "c=US,  o=contoso ltd, CN=Contoso Ltd").Should().BeTrue("the same names in another order and case");
        UpdatePublisher.SignedBy(contoso, "Contoso Ltd").Should().BeTrue("the common name");
        UpdatePublisher.SignedBy(contoso, string.Join(' ', contoso.Thumbprint.Chunk(2).Select(c => new string(c)))).Should().BeTrue("the SHA-1 thumbprint, as certificate dialogs show it");
        UpdatePublisher.SignedBy(contoso, sha256.ToLowerInvariant()).Should().BeTrue("the SHA-256 thumbprint");

        UpdatePublisher.SignedBy(contoso, "CN=Contoso Ltd").Should().BeFalse("a subject must be the whole subject");
        UpdatePublisher.SignedBy(contoso, "CN=Contoso Ltd, O=Contoso Ltd, C=NL").Should().BeFalse();
        UpdatePublisher.SignedBy(contoso, "Contoso").Should().BeFalse();
        UpdatePublisher.SignedBy(contoso, new string('A', 40)).Should().BeFalse();
    }

    [Fact]
    public void On_Windows_Authenticode_is_checked_by_Windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The .NET runtime's own files carry Microsoft's Authenticode signature; this test's assembly carries none.
        string signed = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "coreclr.dll");
        var signatures = new AuthenticodeSignatures();

        CodeSignature microsoft = signatures.Read(signed);
        microsoft.Problem.Should().BeNull();
        microsoft.Signer!.GetNameInfo(X509NameType.SimpleName, false).Should().Be("Microsoft Corporation");
        signatures.Read(typeof(UpdateControlTests).Assembly.Location).Problem.Should().Be("is not signed");

        string changed = Path.Combine(_directory, "changed.dll");
        byte[] bytes = File.ReadAllBytes(signed);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(changed, bytes);
        signatures.Read(changed).Problem.Should().NotBeNull("a file changed after signing is not valid");

        // A whole package: its main executable is a copy of that signed file.
        string package = Path.Combine(_directory, "signed.nupkg");
        using (ZipArchive zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            Nuspec(zip, "SemanticRouter.exe");
            zip.CreateEntryFromFile(signed, "lib/app/SemanticRouter.exe");
        }

        new UpdatePublisher("Microsoft Corporation", UpdatePublisher.Platform.Windows, signatures).Refuses(package).Should().BeNull();
        new UpdatePublisher("Contoso Ltd", UpdatePublisher.Platform.Windows, signatures).Refuses(package).Should().StartWith("SemanticRouter.exe is signed by CN=Microsoft Corporation");
    }

    [Fact]
    public void On_macOS_codesign_refuses_an_unsigned_bundle()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string bundle = Path.Combine(_directory, "Unsigned.app");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        File.Copy("/bin/echo", Path.Combine(bundle, "Contents", "MacOS", "Unsigned"));
        File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict><key>CFBundleExecutable</key><string>Unsigned</string><key>CFBundleIdentifier</key><string>test.unsigned</string></dict></plist>
            """);

        new CodesignSignatures().Read(bundle).Problem.Should().NotBeNull();
    }

    [Fact]
    public void Doctor_says_what_the_policy_does_to_desktop_updates()
    {
        EgressList.Updates(MachinePolicy.None).Should().Be(
            "desktop app updates: api.github.com, github.com and objects.githubusercontent.com (only the installed desktop app; untick Check for updates in its tray menu to turn this off).");
        EgressList.Updates(MachinePolicy.FromJson("""{"Updates":{"Mode":"Off","Apply":"Notify"}}""")).Should().Be("desktop app updates: off (your organization's policy).");
        EgressList.Updates(MachinePolicy.FromJson("""{"Updates":{"Mode":"Feed","Feed":"https://updates.corp.example/router","Apply":"Notify","Publisher":"A1B2C3D4E5"}}""")).Should().Be(
            "desktop app updates: updates.corp.example (your organization's feed) (only the installed desktop app; applied only when the person chooses to; applied only when signed by A1B2C3D4E5; untick Check for updates in its tray menu to turn this off).");
    }

    private static readonly X509Certificate2 Contoso = Certificate("CN=Contoso Ltd, O=Contoso Ltd, C=US");

    private static X509Certificate2 Certificate(string subject)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static FakeSignatures Signatures(X509Certificate2 signer) => new(_ => new CodeSignature(null, signer));

    /// <summary>A Windows package as vpk lays it out: the app, its own assemblies, a library, and Velopack's updater.</summary>
    private string WindowsPackage() => Package("win.nupkg", "SemanticRouter.exe",
        ["lib/app/SemanticRouter.exe", "lib/app/SemanticRouter.dll", "lib/app/SemanticRouter.Core.dll", "lib/app/onnxruntime.dll", "lib/app/Squirrel.exe", "lib/app/SemanticRouter.deps.json"]);

    private string Package(string name, string mainExe, string[] files)
    {
        string path = Path.Combine(_directory, name);
        if (File.Exists(path))
        {
            return path;
        }

        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Nuspec(zip, mainExe);
        foreach (string file in files)
        {
            using StreamWriter writer = new(zip.CreateEntry(file).Open());
            writer.Write("not really " + file);
        }

        return path;
    }

    private static void Nuspec(ZipArchive zip, string mainExe)
    {
        using StreamWriter writer = new(zip.CreateEntry("SemanticRouter.nuspec").Open());
        writer.Write($"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
            <metadata><id>SemanticRouter</id><version>1.0.1</version><mainExe>{mainExe}</mainExe></metadata>
            </package>
            """);
    }

    private sealed class FakeSignatures(Func<string, CodeSignature> read) : ICodeSignatures
    {
        public CodeSignature Read(string path) => read(path);
    }

    /// <summary>A feed with 1.0.1 for an installed 1.0.0, recording what the updater asks of Velopack.</summary>
    private sealed class FakeUpdates : IAppUpdates
    {
        private readonly VelopackAsset _latest = new()
        {
            PackageId = "SemanticRouter",
            Version = SemanticVersion.Parse("1.0.1"),
            Type = VelopackAssetType.Full,
            FileName = "SemanticRouter-1.0.1-win-x64-full.nupkg",
        };

        public List<string> Calls { get; } = [];

        public bool Downloaded { get; set; }

        public string? Package { get; set; }

        public string? CurrentVersion => "1.0.0";

        public VelopackAsset? Pending => Downloaded ? _latest : null;

        public Task<UpdateInfo?> CheckAsync()
        {
            Calls.Add("check");
            return Task.FromResult<UpdateInfo?>(new UpdateInfo(_latest, false));
        }

        public Task DownloadAsync(UpdateInfo update, CancellationToken cancellationToken)
        {
            Calls.Add("download");
            Downloaded = true;
            return Task.CompletedTask;
        }

        public string? PackageFile(VelopackAsset asset)
        {
            Calls.Add("package");
            return Package;
        }

        public void Discard(VelopackAsset asset)
        {
            Calls.Add("discard");
            Downloaded = false;
        }

        public void ApplyAfterExit(VelopackAsset asset, bool restart, string[] arguments) => Calls.Add($"apply {asset.Version} restart={restart}");
    }
}
