using System.Diagnostics;
using System.Text.Json.Nodes;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Reads the <see cref="MachinePolicy"/> from where only administrators can write it, so device management (Group
/// Policy, Intune, an MDM profile, configuration management) can push it:
/// <list type="bullet">
/// <item>Windows: the registry key <c>HKLM\Software\Policies\SemanticRouter</c>. Each value is a setting: a string, a
/// DWORD (numbers, and 0/1 for switches), a multi-string (a list), or a string holding JSON; a subkey is a section
/// (<c>Features</c>, <c>Updates</c>).</item>
/// <item>macOS: the managed preferences domain <c>io.github.siebe41.semantic-router</c>
/// (<c>/Library/Managed Preferences/io.github.siebe41.semantic-router.plist</c>), as an MDM configuration profile
/// writes it.</item>
/// <item>Linux and containers: <c>/etc/semantic-router/policy.json</c>.</item>
/// </list>
/// A policy that cannot be read is an error, never "no policy": the router does not start without the restrictions an
/// administrator set.
/// </summary>
public static class MachinePolicyLoader
{
    /// <summary>The registry key read on Windows, under <c>HKEY_LOCAL_MACHINE</c>.</summary>
    public const string RegistryKey = @"Software\Policies\SemanticRouter";

    /// <summary>The managed preferences file read on macOS.</summary>
    public const string ManagedPreferences = "/Library/Managed Preferences/io.github.siebe41.semantic-router.plist";

    /// <summary>The file read on Linux and in containers.</summary>
    public const string PolicyFile = "/etc/semantic-router/policy.json";

    /// <summary>The policy for this machine; <see cref="MachinePolicy.None"/> when none is set.</summary>
    /// <exception cref="InvalidDataException">A policy is set but cannot be read.</exception>
    public static MachinePolicy Load()
    {
        if (OperatingSystem.IsWindows())
        {
            return FromRegistry();
        }

        return OperatingSystem.IsMacOS() ? FromPropertyList(ManagedPreferences) : FromFile(PolicyFile);
    }

    /// <summary>The policy in a JSON file; <see cref="MachinePolicy.None"/> when the file does not exist.</summary>
    public static MachinePolicy FromFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            return MachinePolicy.None;
        }

        try
        {
            return MachinePolicy.FromJson(File.ReadAllText(path), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"The machine policy {path} cannot be read: {ex.Message}", ex);
        }
    }

    /// <summary>The policy in a property list (converted with macOS's own <c>plutil</c>); <see cref="MachinePolicy.None"/> when it does not exist.</summary>
    internal static MachinePolicy FromPropertyList(string path)
    {
        if (!File.Exists(path))
        {
            return MachinePolicy.None;
        }

        var start = new ProcessStartInfo("/usr/bin/plutil")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[] { "-convert", "json", "-o", "-", path })
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using Process plutil = Process.Start(start) ?? throw new InvalidDataException("plutil did not start.");
            Task<string> output = plutil.StandardOutput.ReadToEndAsync();
            string error = plutil.StandardError.ReadToEnd();
            if (!plutil.WaitForExit(10_000) || plutil.ExitCode != 0)
            {
                throw new InvalidDataException($"The machine policy {path} cannot be read: {error.Trim()}");
            }

            // MDM payloads add their own keys (PayloadUUID, …); only the policy's own settings are read.
            JsonObject document = JsonNode.Parse(output.GetAwaiter().GetResult()) as JsonObject ?? [];
            foreach (string key in document.Select(p => p.Key).Where(k => k.StartsWith("Payload", StringComparison.Ordinal)).ToArray())
            {
                document.Remove(key);
            }

            return MachinePolicy.FromJson(document.ToJsonString(), path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or System.Text.Json.JsonException)
        {
            throw new InvalidDataException($"The machine policy {path} cannot be read: {ex.Message}", ex);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static MachinePolicy FromRegistry()
    {
        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegistryKey);
        if (key is null)
        {
            return MachinePolicy.None;
        }

        string source = @"HKLM\" + RegistryKey;
        return MachinePolicy.FromJson(ToJson(key).ToJsonString(), source);

        static JsonObject ToJson(Microsoft.Win32.RegistryKey key)
        {
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in key.GetValueNames().Where(n => n.Length > 0))
            {
                values[name] = key.GetValue(name);
            }

            JsonObject json = FromValues(values);
            foreach (string name in key.GetSubKeyNames())
            {
                using Microsoft.Win32.RegistryKey? child = key.OpenSubKey(name);
                if (child is not null)
                {
                    json[name] = ToJson(child);
                }
            }

            return json;
        }
    }

    /// <summary>
    /// Registry values as a JSON object: a DWORD or QWORD is a number, a multi-string a list, and a string is taken as JSON
    /// when it is a JSON array, object, number or <c>true</c>/<c>false</c>, otherwise as text.
    /// </summary>
    internal static JsonObject FromValues(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var json = new JsonObject();
        foreach ((string name, object? value) in values)
        {
            json[name] = value switch
            {
                int number => JsonValue.Create(number),
                long number => JsonValue.Create(number),
                string[] list => new JsonArray([.. list.Where(s => s.Length > 0).Select(s => (JsonNode?)JsonValue.Create(s))]),
                string text => Parse(text),
                _ => null,
            };
        }

        return json;

        static JsonNode? Parse(string text)
        {
            string trimmed = text.Trim();
            if (trimmed is "true" or "false" || (trimmed.Length > 0 && (trimmed[0] is '[' or '{' || char.IsAsciiDigit(trimmed[0]))))
            {
                try
                {
                    return JsonNode.Parse(trimmed);
                }
                catch (System.Text.Json.JsonException)
                {
                    // Not JSON after all: the text itself.
                }
            }

            return JsonValue.Create(text);
        }
    }
}
