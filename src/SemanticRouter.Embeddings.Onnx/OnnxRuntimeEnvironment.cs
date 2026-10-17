using System.Runtime.InteropServices;
using System.Text;
using Microsoft.ML.OnnxRuntime;

namespace SemanticRouter.Embeddings.Onnx;

/// <summary>
/// ONNX Runtime's process-wide settings. Its Linux and macOS builds send usage data to Microsoft
/// (<c>mobile.events.data.microsoft.com</c>) and keep a device id under <c>~/.cache/Microsoft/DeveloperTools/.onnxruntime</c>
/// unless <c>ORT_DISABLE_TELEMETRY</c> is set in the process's native environment before ONNX Runtime loads.
/// <c>OrtEnv.DisableTelemetryEvents</c> does not stop that upload, and neither does
/// <see cref="Environment.SetEnvironmentVariable(string, string)"/>, which on Linux and macOS changes only .NET's copy of
/// the environment. This package sets the variable natively before it first uses ONNX Runtime.
/// <para>
/// It also says whether ONNX Runtime can load here at all (<see cref="Unavailable"/>): it publishes no build for Intel
/// Macs, and its Apple silicon build needs macOS 14.
/// </para>
/// </summary>
public static class OnnxRuntimeEnvironment
{
    /// <summary>The variable ONNX Runtime reads; <c>1</c> turns its telemetry off.</summary>
    public const string TelemetryVariable = "ORT_DISABLE_TELEMETRY";

    /// <summary>The host ONNX Runtime's telemetry uploads to.</summary>
    public const string TelemetryHost = "mobile.events.data.microsoft.com";

    /// <summary>The oldest macOS ONNX Runtime's Apple silicon library is built for.</summary>
    internal const int MinimumMacOS = 14;

    private static readonly Lock Gate = new();
    private static readonly Lazy<string?> Probe = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    private static bool _prepared;

    /// <summary>
    /// <see langword="null"/> when ONNX Runtime's native library loads on this machine; otherwise why the in-process
    /// embeddings cannot run here, and what to use instead. Found out once per process: the first call loads the library
    /// (with its telemetry off).
    /// </summary>
    public static string? Unavailable => Probe.Value;

    /// <summary>
    /// Sets <see cref="TelemetryVariable"/> to <c>1</c> in the native environment, unless the process was started with it
    /// set (then it is kept as it is), or it was set in this process through .NET (then that value is passed on). Call it
    /// before anything in the process uses ONNX Runtime; this package calls it before it does. Safe to call more than once.
    /// </summary>
    public static void DisableTelemetry()
    {
        lock (Gate)
        {
            if (_prepared)
            {
                return;
            }

            _prepared = true;
            try
            {
                if (ValueToSet(ReadNative(TelemetryVariable), Environment.GetEnvironmentVariable(TelemetryVariable)) is not { } value)
                {
                    return;
                }

                WriteNative(TelemetryVariable, value);

                // .NET's copy too, so the processes this one starts get it and the doctor reads the same value.
                Environment.SetEnvironmentVariable(TelemetryVariable, value);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // No C library to call: ONNX Runtime, which needs one, cannot load either.
            }
        }
    }

    /// <summary>Why ONNX Runtime did not load, for someone who has to choose other embeddings.</summary>
    /// <param name="error">What loading it threw; <see langword="null"/> when it was not tried (a macOS it is not built for).</param>
    /// <param name="macOS">Whether this is a Mac.</param>
    /// <param name="architecture">The process's architecture.</param>
    /// <param name="version">The operating system's version.</param>
    internal static string Explain(Exception? error, bool macOS, Architecture architecture, Version version)
    {
        const string Instead = "Use an embedding model in LM Studio or Ollama instead (Embeddings: OpenAICompatible).";
        if (macOS && architecture == Architecture.X64)
        {
            return $"In-process (Onnx) embeddings are not available on an Intel Mac: ONNX Runtime publishes no build for it. {Instead}";
        }

        if (macOS && version.Major < MinimumMacOS)
        {
            return $"In-process (Onnx) embeddings need macOS {MinimumMacOS} or later on a Mac (this is {version.Major}.{version.Minor}): " +
                   $"ONNX Runtime's library is built for it. {Instead}";
        }

        Exception innermost = error ?? new DllNotFoundException("not loaded");
        while (innermost.InnerException is { } inner)
        {
            innermost = inner;
        }

        return $"In-process (Onnx) embeddings are not available here: ONNX Runtime's native library could not be loaded ({innermost.Message.TrimEnd('.')}). {Instead}";
    }

    private static string? Load()
    {
        bool macOS = OperatingSystem.IsMacOS();
        Version version = Environment.OSVersion.Version;
        if (macOS && (RuntimeInformation.ProcessArchitecture == Architecture.X64 || version.Major < MinimumMacOS))
        {
            // Not tried: there is no library for an Intel Mac, and one built for a newer macOS may fail in ways that
            // cannot be caught.
            return Explain(null, macOS, RuntimeInformation.ProcessArchitecture, version);
        }

        DisableTelemetry();
        try
        {
            // The first use loads the library; the call also turns off the Windows build's ETW events.
            OrtEnv.Instance().DisableTelemetryEvents();
            return null;
        }
        catch (Exception ex) when (IsLoadFailure(ex))
        {
            return Explain(ex, macOS, RuntimeInformation.ProcessArchitecture, version);
        }
    }

    /// <summary>
    /// What .NET throws when a native library is missing, is for another platform or lacks a function, what ONNX Runtime
    /// throws when it cannot set itself up, and a failed static initialisation (how ONNX Runtime's types report either).
    /// </summary>
    internal static bool IsLoadFailure(Exception ex) =>
        ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or OnnxRuntimeException or TypeInitializationException;

    /// <summary>
    /// <see cref="TelemetryVariable"/> as ONNX Runtime will read it (the native environment), or <see langword="null"/>
    /// when it is not set.
    /// </summary>
    public static string? TelemetrySetting
    {
        get
        {
            try
            {
                return ReadNative(TelemetryVariable);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return Environment.GetEnvironmentVariable(TelemetryVariable);
            }
        }
    }

    /// <summary>
    /// What to set <see cref="TelemetryVariable"/> to natively: nothing when the process was started with it
    /// (<paramref name="native"/>), the value .NET has when it was set in this process (<paramref name="managed"/>), else <c>1</c>.
    /// </summary>
    internal static string? ValueToSet(string? native, string? managed) =>
        native is not null ? null : managed is { Length: > 0 } ? managed : "1";

    /// <summary>Whether <paramref name="value"/> of <see cref="TelemetryVariable"/> is the one known to turn telemetry off.</summary>
    public static bool TurnsTelemetryOff(string? value) => value?.Trim() == "1";

    // On Windows .NET reads and writes the process environment itself (GetEnvironmentVariableW, SetEnvironmentVariableW).
    internal static string? ReadNative(string name) => OperatingSystem.IsWindows()
        ? Environment.GetEnvironmentVariable(name)
        : Marshal.PtrToStringUTF8(GetEnv(Utf8(name)));

    private static void WriteNative(string name, string value)
    {
        if (OperatingSystem.IsWindows())
        {
            Environment.SetEnvironmentVariable(name, value);
            return;
        }

        // Without overwriting: a value someone set in the meantime wins. It fails only when out of memory.
        _ = SetEnv(Utf8(name), Utf8(value), overwrite: 0);
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + '\0');

    [DllImport("libc", EntryPoint = "getenv")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern IntPtr GetEnv(byte[] name);

    [DllImport("libc", EntryPoint = "setenv")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int SetEnv(byte[] name, byte[] value, int overwrite);
}
