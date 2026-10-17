using System.Runtime.InteropServices;
using System.Text;

namespace SemanticRouter.Embeddings.Onnx;

/// <summary>
/// ONNX Runtime's process-wide settings. Its Linux and macOS builds send usage data to Microsoft
/// (<c>mobile.events.data.microsoft.com</c>) and keep a device id under <c>~/.cache/Microsoft/DeveloperTools/.onnxruntime</c>
/// unless <c>ORT_DISABLE_TELEMETRY</c> is set in the process's native environment before ONNX Runtime loads.
/// <c>OrtEnv.DisableTelemetryEvents</c> does not stop that upload, and neither does
/// <see cref="Environment.SetEnvironmentVariable(string, string)"/>, which on Linux and macOS changes only .NET's copy of
/// the environment. This package sets the variable natively before it first uses ONNX Runtime.
/// </summary>
public static class OnnxRuntimeEnvironment
{
    /// <summary>The variable ONNX Runtime reads; <c>1</c> turns its telemetry off.</summary>
    public const string TelemetryVariable = "ORT_DISABLE_TELEMETRY";

    /// <summary>The host ONNX Runtime's telemetry uploads to.</summary>
    public const string TelemetryHost = "mobile.events.data.microsoft.com";

    private static readonly Lock Gate = new();
    private static bool _prepared;

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
