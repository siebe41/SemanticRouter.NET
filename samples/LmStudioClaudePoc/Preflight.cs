using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LmStudioClaudePoc;

/// <summary>
/// Checks both backends before the router starts, without spending any Claude usage:
/// LM Studio's model list (and auto-selects a chat model if none is configured), and that the `claude`
/// executable exists, runs, and is signed in.
/// </summary>
internal static class Preflight
{
    public static async Task<bool> RunAsync(ConfigurationManager configuration)
    {
        Console.WriteLine();
        Console.WriteLine("Preflight");
        Console.WriteLine("---------");
        bool lmStudio = await CheckLmStudioAsync(configuration, report: true);
        bool claude = await CheckClaudeAsync(configuration);
        Console.WriteLine();
        return lmStudio && claude;
    }

    /// <summary>
    /// Fills in the LM Studio model id when none is configured. Runs even with --skip-preflight: without an id the
    /// request would reach LM Studio with the client's "model": "auto".
    /// </summary>
    public static Task ResolveLmStudioModelAsync(ConfigurationManager configuration) =>
        CheckLmStudioAsync(configuration, report: false);

    private static async Task<bool> CheckLmStudioAsync(ConfigurationManager configuration, bool report)
    {
        string baseAddress = configuration["SemanticRouter:Connectors:lmstudio:BaseAddress"] ?? "http://localhost:1234";
        string? modelKey = FindModelKey(configuration, "lmstudio");
        string? configured = modelKey is null ? null : configuration[modelKey];

        List<string> ids;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using JsonDocument json = JsonDocument.Parse(await http.GetStringAsync(new Uri(new Uri(baseAddress), "/v1/models")));
            ids = json.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            if (!report)
            {
                return false;
            }

            Fail("LM Studio", $"not reachable at {baseAddress} ({ex.Message}). Start the server in LM Studio (Developer tab) or `lms server start`.");
            return false;
        }

        List<string> chatModels = ids.Where(id => !id.Contains("embed", StringComparison.OrdinalIgnoreCase)).ToList();
        if (chatModels.Count == 0)
        {
            if (!report)
            {
                return false;
            }

            Fail("LM Studio", $"reachable at {baseAddress} but no chat model is available. Load one in LM Studio.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            if (modelKey is not null)
            {
                configuration[modelKey] = chatModels[0];
            }

            if (report)
            {
                Ok("LM Studio", $"{baseAddress}, using '{chatModels[0]}' (auto-selected from: {string.Join(", ", chatModels)})");
            }

            return true;
        }

        if (!report)
        {
            return true;
        }

        if (!ids.Contains(configured, StringComparer.OrdinalIgnoreCase))
        {
            Fail("LM Studio", $"model '{configured}' is not available. LM Studio reports: {string.Join(", ", ids)}");
            return false;
        }

        Ok("LM Studio", $"{baseAddress}, using '{configured}'");
        return true;
    }

    private static async Task<bool> CheckClaudeAsync(ConfigurationManager configuration)
    {
        string executable = configuration["SemanticRouter:Connectors:claude-cli:Executable"] is { Length: > 0 } configured
            ? configured
            : "claude";

        string? resolved = Resolve(executable);
        if (resolved is null)
        {
            string hint = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Resolve("claude.cmd") is { } shim
                ? $" Only an npm shim was found ({shim}); the router starts executables directly, never through cmd.exe. " +
                  "Install Claude Code with the native installer (claude.exe) or set Connectors:claude-cli:Executable to claude.exe."
                : " Install Claude Code, or set SemanticRouter:Connectors:claude-cli:Executable to its full path.";
            Fail("Claude CLI", $"'{executable}' not found on PATH.{hint}");
            return false;
        }

        (int versionExit, string version) = await RunAsync(resolved, "--version");
        if (versionExit != 0)
        {
            Fail("Claude CLI", $"{resolved} --version failed (exit {versionExit}): {version}");
            return false;
        }

        // `claude auth status` prints JSON such as {"loggedIn":true,"authMethod":"claude.ai",...}.
        (int authExit, string auth) = await RunAsync(resolved, "auth", "status");
        (bool? loggedIn, string? method) = ReadAuthStatus(auth);
        if (authExit != 0 || loggedIn == false)
        {
            Fail("Claude CLI", $"{version.Trim()} at {resolved}, but not signed in. Run `claude` once and use /login. ({FirstLine(auth)})");
            return false;
        }

        string how = method switch
        {
            "claude.ai" or "oauth_token" => "your Claude subscription",
            "api_key" or "api_key_helper" => "an API key (billed per token, not your subscription)",
            "third_party" => "a cloud provider (Bedrock/Vertex/Foundry)",
            null => "unknown method",
            _ => method,
        };
        Ok("Claude CLI", $"{version.Trim()} at {resolved}, signed in with {how}");

        if (method is "api_key" or "api_key_helper" or "third_party")
        {
            Warn("Claude CLI", "requests will not use a Claude subscription. Run `claude`, /logout, then /login with your Claude account if that is what you want.");
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
        {
            Warn("Claude CLI", "ANTHROPIC_API_KEY is set in this shell. The router removes it for `claude`, so your login above is what gets used.");
        }

        return true;
    }

    private static (bool? LoggedIn, string? Method) ReadAuthStatus(string output)
    {
        int start = output.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return (null, null);
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(output[start..]);
            JsonElement root = json.RootElement;
            bool? loggedIn = root.TryGetProperty("loggedIn", out JsonElement l) && l.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? l.GetBoolean()
                : null;
            string? method = root.TryGetProperty("authMethod", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return (loggedIn, method);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? FindModelKey(ConfigurationManager configuration, string connector) =>
        configuration.GetSection("SemanticRouter:Models").GetChildren()
            .Where(m => string.Equals(m["Connector"], connector, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.GetSection("Model").Path)
            .FirstOrDefault();

    private static string? Resolve(string executable)
    {
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar))
        {
            return File.Exists(executable) ? Path.GetFullPath(executable) : null;
        }

        string[] extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !Path.HasExtension(executable)
            ? [".exe"]
            : [string.Empty];

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            foreach (string extension in extensions)
            {
                string candidate = Path.Combine(directory.Trim('"'), executable + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Same environment the router gives `claude`, so the status reflects what requests will actually use.
        foreach (string name in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL" })
        {
            startInfo.Environment.Remove(name);
        }

        try
        {
            using Process process = Process.Start(startInfo)!;
            process.StandardInput.Close();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return (-1, "timed out after 20 s");
            }

            string output = (await stdout) + (await stderr);
            return (process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

    private static void Ok(string what, string detail) => Console.WriteLine($"[ OK ] {what}: {detail}");

    private static void Warn(string what, string detail) => Console.WriteLine($"[WARN] {what}: {detail}");

    private static void Fail(string what, string detail) => Console.WriteLine($"[FAIL] {what}: {detail}");
}
