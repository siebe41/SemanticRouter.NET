using System.ComponentModel;
using System.Diagnostics;
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
/// shell ever interprets the prompt.
/// </summary>
internal sealed class SystemCliProcessRunner : ICliProcessRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public ICliProcess Start(CliInvocation invocation)
    {
        var startInfo = new ProcessStartInfo(invocation.Executable)
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

        foreach (string name in invocation.RemoveEnvironment)
        {
            startInfo.Environment.Remove(name);
        }

        foreach ((string name, string value) in invocation.SetEnvironment)
        {
            startInfo.Environment[name] = value;
        }

        var process = new Process { StartInfo = startInfo };
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

    private sealed class SystemCliProcess : ICliProcess
    {
        private readonly Process _process;
        private readonly StringBuilder _stderr = new();
        private readonly Task _stdinWriter;

        public SystemCliProcess(Process process, string? standardInput)
        {
            _process = process;
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (_stderr)
                    {
                        _stderr.AppendLine(e.Data);
                    }
                }
            };
            _process.BeginErrorReadLine();

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

        public string StandardError
        {
            get
            {
                lock (_stderr)
                {
                    return _stderr.ToString();
                }
            }
        }

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
