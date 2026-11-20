using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace SemanticRouter.AspNetCore;

/// <summary>Everything needed to start a CLI process.</summary>
internal sealed class CliInvocation
{
    public required string Executable { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>Text written to standard input before it is closed; <see langword="null"/> closes it immediately.</summary>
    public string? StandardInput { get; init; }

    public IReadOnlyDictionary<string, string> SetEnvironment { get; init; } = new Dictionary<string, string>();

    public IReadOnlyCollection<string> RemoveEnvironment { get; init; } = [];

    /// <summary>
    /// The router's secrets (resolved keys, gateway keys, CLI environment values): an inherited variable holding one of
    /// them is removed, whatever its name, so a key the router was given never reaches a child it was not meant for.
    /// </summary>
    public IReadOnlyCollection<string> SecretValues { get; init; } = [];
}

/// <summary>A running CLI process.</summary>
internal interface ICliProcess : IAsyncDisposable
{
    /// <summary>Standard output, line by line, as it is produced.</summary>
    IAsyncEnumerable<string> ReadOutputLinesAsync(CancellationToken cancellationToken);

    /// <summary>Waits for exit and returns the exit code.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Everything written to standard error so far.</summary>
    string StandardError { get; }
}

/// <summary>Starts CLI processes. Abstracted so connectors can be tested without real binaries.</summary>
internal interface ICliProcessRunner
{
    /// <exception cref="FileNotFoundException">The executable could not be found or started.</exception>
    ICliProcess Start(CliInvocation invocation);
}

/// <summary>
/// Starts real processes with <see cref="ProcessStartInfo.ArgumentList"/> and <c>UseShellExecute = false</c>, so no
/// shell ever interprets the prompt. The executable is resolved with <see cref="CliExecutableLocator"/> first and started
/// by its full path, so a file of the same name in the current directory is never run instead.
/// </summary>
internal sealed class SystemCliProcessRunner : ICliProcessRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public ICliProcess Start(CliInvocation invocation)
    {
        var process = new Process { StartInfo = CreateStartInfo(invocation) };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            throw new FileNotFoundException($"Could not start '{invocation.Executable}': {ex.Message}", invocation.Executable, ex);
        }

        return new SystemCliProcess(process, invocation.StandardInput);
    }

    /// <exception cref="FileNotFoundException">The executable is not on PATH, or only a script wrapper is.</exception>
    internal static ProcessStartInfo CreateStartInfo(CliInvocation invocation)
    {
        CliExecutableLookup lookup = CliExecutableLocator.Find(invocation.Executable);
        if (lookup.Path is not { } path)
        {
            throw new FileNotFoundException(
                $"Could not start '{invocation.Executable}': " + (lookup.ScriptPath is null ? "not found." : $"only a script wrapper was found ({lookup.ScriptPath})."),
                invocation.Executable);
        }

        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = invocation.WorkingDirectory,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };

        foreach (string argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        CliEnvironment.Apply(startInfo.Environment, invocation.RemoveEnvironment, invocation.SecretValues, invocation.SetEnvironment);
        return startInfo;
    }

    private sealed class SystemCliProcess : ICliProcess
    {
        private readonly Process _process;
        private readonly BoundedText _stderr = new(BoundedText.StandardErrorLimit);
        private readonly Task _stderrReader;
        private readonly Task _stdinWriter;

        public SystemCliProcess(Process process, string? standardInput)
        {
            _process = process;

            // Read in chunks, not lines, so a child that writes without line breaks cannot grow one line without end.
            _stderrReader = Task.Run(async () =>
            {
                char[] buffer = new char[4096];
                try
                {
                    int read;
                    while ((read = await _process.StandardError.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                    {
                        _stderr.Append(buffer.AsSpan(0, read));
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // The process was killed and disposed: what was read so far is what there is.
                }
            });

            // Write on a background task so a child that produces output before reading its input cannot deadlock us.
            _stdinWriter = Task.Run(async () =>
            {
                try
                {
                    if (standardInput is not null)
                    {
                        await _process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
                    }
                }
                catch (IOException)
                {
                    // The child exited or closed its input early; its exit code and output tell the story.
                }
                finally
                {
                    try
                    {
                        _process.StandardInput.Close();
                    }
                    catch (IOException)
                    {
                    }
                }
            });
        }

        public string StandardError => _stderr.ToString();

        public async IAsyncEnumerable<string> ReadOutputLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                yield return line;
            }
        }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await _stdinWriter.ConfigureAwait(false);
            await _stderrReader.WaitAsync(cancellationToken).ConfigureAwait(false);
            return _process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            _process.Dispose();
        }
    }
}

/// <summary>
/// Text that arrives in pieces (a child's standard error), kept to its first and last <c>limit / 2</c> characters with
/// a note of how many were left out between them, so a child that writes without end cannot fill the router's memory.
/// The start holds what a tool said first (a usage error), the end what it said last (the error it stopped on).
/// Thread-safe.
/// </summary>
internal sealed class BoundedText(int limit)
{
    /// <summary>How much of a CLI's standard error is kept: 64 Ki characters, far more than a diagnostic needs.</summary>
    public const int StandardErrorLimit = 64 * 1024;

    private readonly int _half = limit / 2;
    private readonly StringBuilder _head = new();
    private readonly StringBuilder _tail = new();
    private readonly Lock _gate = new();
    private long _omitted;

    public void Append(ReadOnlySpan<char> text)
    {
        lock (_gate)
        {
            int toHead = Math.Min(text.Length, _half - _head.Length);
            if (toHead > 0)
            {
                _head.Append(text[..toHead]);
                text = text[toHead..];
            }

            if (text.IsEmpty)
            {
                return;
            }

            if (text.Length >= _half)
            {
                _omitted += _tail.Length + text.Length - _half;
                _tail.Clear().Append(text[^_half..]);
                return;
            }

            _tail.Append(text);
            if (_tail.Length > _half)
            {
                int excess = _tail.Length - _half;
                _tail.Remove(0, excess);
                _omitted += excess;
            }
        }
    }

    public override string ToString()
    {
        lock (_gate)
        {
            return _omitted == 0
                ? string.Concat(_head.ToString(), _tail.ToString())
                : string.Create(CultureInfo.InvariantCulture, $"{_head}\n[… {_omitted} characters left out …]\n{_tail}");
        }
    }
}

/// <summary>
/// The variables of the router's environment that a child it starts (a CLI, or the Copilot SDK's runtime) never inherits,
/// and the one place that builds such a child's environment.
/// </summary>
internal static class CliEnvironment
{
    /// <summary>
    /// Turns <paramref name="environment"/>, a copy of the router's own, into the child's: removes the connector's
    /// <paramref name="remove"/> list, then what belongs to the router (<see cref="IsRouterVariable"/>, and any variable
    /// whose value is one of <paramref name="secretValues"/>), then sets the connector's <paramref name="set"/> variables,
    /// which win. A name in <paramref name="keepSecretValued"/> survives the secret-value check (not the other two): a
    /// variable that is how the child itself signs in stays the child's even when a router setting reads the same value.
    /// </summary>
    public static void Apply(
        IDictionary<string, string?> environment,
        IEnumerable<string> remove,
        IEnumerable<string> secretValues,
        IEnumerable<KeyValuePair<string, string>> set,
        IReadOnlyCollection<string>? keepSecretValued = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        foreach (string name in remove)
        {
            environment.Remove(name);
        }

        // The router's own settings and keys stay with it: what configures the router (SemanticRouter__…, ROUTER_…), the
        // providers' keys the setup reads (OPENAI_API_KEY, …), and any variable holding a secret the router resolved.
        // Variables the connector sets itself come back below.
        HashSet<string> secrets = [.. secretValues];
        foreach ((string name, string? value) in environment.ToArray())
        {
            bool holdsSecret = value is not null && secrets.Contains(value)
                               && keepSecretValued?.Contains(name, StringComparer.OrdinalIgnoreCase) != true;
            if (IsRouterVariable(name) || holdsSecret)
            {
                environment.Remove(name);
            }
        }

        foreach ((string name, string value) in set)
        {
            environment[name] = value;
        }
    }

    /// <summary>
    /// A copy of the router's environment, keyed as the operating system compares names (case-insensitively on Windows),
    /// for <see cref="Apply"/>.
    /// </summary>
    public static Dictionary<string, string?> Inherited()
    {
        var environment = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = (string?)entry.Value;
        }

        return environment;
    }

    private static readonly string[] Prefixes = ["SemanticRouter__", "ROUTER_"];

    // The API keys the dashboard's provider presets read (SetupApi.Presets), plus ANTHROPIC_API_KEY that Claude Code
    // already drops: each belongs to a connector of the router, not to a subscription CLI.
    private static readonly HashSet<string> ProviderKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "OPENROUTER_API_KEY", "GEMINI_API_KEY",
    };

    /// <summary>Whether <paramref name="name"/> configures the router or holds a provider key it uses.</summary>
    public static bool IsRouterVariable(string name) =>
        ProviderKeys.Contains(name) || Prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}
