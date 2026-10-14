using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// The parent of CLI working directories must be private: a CLI reads instructions and hooks from its working directory
/// and the directories above it. The Unix checks run under the test output directory, not the shared /tmp.
/// </summary>
public sealed class CliScratchTests : IDisposable
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string _base = Path.Combine(AppContext.BaseDirectory, "cli-scratch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }
    }

    [Fact]
    public void Linux_prefers_the_runtime_directory_then_the_cache_never_tmp()
    {
        string runtime = Directory.CreateDirectory(Path.Combine(_base, "run")).FullName;
        string cache = Path.Combine(_base, "cache");
        string home = Path.Combine(_base, "home");

        CliScratch.DefaultRoot(Env(("XDG_RUNTIME_DIR", runtime), ("XDG_CACHE_HOME", cache)), home, macOS: false)
            .Should().Be(Path.Combine(runtime, "semantic-router"));
        CliScratch.DefaultRoot(Env(("XDG_CACHE_HOME", cache)), home, macOS: false).Should().Be(Path.Combine(cache, "semantic-router"));
        CliScratch.DefaultRoot(Env(("XDG_RUNTIME_DIR", Path.Combine(_base, "gone")), ("XDG_CACHE_HOME", cache)), home, macOS: false)
            .Should().Be(Path.Combine(cache, "semantic-router"), "a runtime directory that does not exist is skipped");
        CliScratch.DefaultRoot(Env(("XDG_RUNTIME_DIR", "relative"), ("TMPDIR", "/tmp")), home, macOS: false)
            .Should().Be(Path.Combine(home, ".cache", "semantic-router"), "relative paths are ignored and TMPDIR is shared on Linux");
    }

    [Fact]
    public void MacOS_uses_its_per_user_tmpdir_or_the_caches_folder()
    {
        string temp = Directory.CreateDirectory(Path.Combine(_base, "T")).FullName;
        string home = Path.Combine(_base, "home");

        CliScratch.DefaultRoot(Env(("TMPDIR", temp)), home, macOS: true).Should().Be(Path.Combine(temp, "semantic-router"));
        CliScratch.DefaultRoot(Env(), home, macOS: true).Should().Be(Path.Combine(home, "Library", "Caches", "semantic-router"));
    }

    [Fact]
    public void Creates_the_parent_with_mode_0700_and_tightens_one_others_can_read()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string fresh = CliScratch.EnsurePrivate(Path.Combine(_base, "fresh"));
        File.GetUnixFileMode(fresh).Should().Be(Private);

        string readable = Directory.CreateDirectory(Path.Combine(_base, "readable")).FullName;
        File.SetUnixFileMode(readable, Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        CliScratch.EnsurePrivate(readable);
        File.GetUnixFileMode(readable).Should().Be(Private);
    }

    [Fact]
    public void Refuses_a_parent_others_can_write_to()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string shared = Directory.CreateDirectory(Path.Combine(_base, "shared")).FullName;
        File.SetUnixFileMode(shared, Private | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        Refusal(shared).Message.Should().Match("*can be written to by other users*XDG_RUNTIME_DIR*");
        File.GetUnixFileMode(shared).Should().HaveFlag(UnixFileMode.OtherWrite, "a directory someone else could have filled is not silently reused");
    }

    [Fact]
    public void Refuses_a_parent_inside_a_world_writable_directory_such_as_tmp()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string world = Directory.CreateDirectory(Path.Combine(_base, "world")).FullName;
        File.SetUnixFileMode(world, Private | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute | UnixFileMode.StickyBit);

        Refusal(Path.Combine(world, "semantic-router-me")).Message.Should().Match($"*inside {world}, which every user can write to*");
    }

    [Fact]
    public void Refuses_a_symbolic_link_and_a_relative_path()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string target = Directory.CreateDirectory(Path.Combine(_base, "target")).FullName;
        string link = Path.Combine(_base, "link");
        Directory.CreateSymbolicLink(link, target);

        Refusal(link).Message.Should().Match("*symbolic link*");
        Refusal(Path.Combine(".cache", "semantic-router")).Message.Should().Match("*not an absolute path*");
    }

    [Fact]
    public void Working_directories_are_private_and_never_under_a_shared_tmp()
    {
        string directory = CliScratch.Create("test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.Exists(directory).Should().BeTrue();
            Directory.EnumerateFileSystemEntries(directory).Should().BeEmpty();
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(directory).Should().Be(Private);
                File.GetUnixFileMode(Path.GetDirectoryName(directory)!).Should().Be(Private);
            }

            if (OperatingSystem.IsLinux())
            {
                directory.Should().NotStartWith("/tmp/");
            }
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static IOException Refusal(string path)
    {
        try
        {
            CliScratch.EnsurePrivate(path);
        }
        catch (IOException ex)
        {
            return ex;
        }

        throw new InvalidOperationException($"{path} was accepted");
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(v => v.Name == name).Value;
}
