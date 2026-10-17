using System.Runtime.InteropServices;

namespace SemanticRouter.Embeddings.Onnx;

/// <summary>
/// ONNX Runtime's own telemetry. On Linux and macOS it sends usage events to Microsoft
/// (<c>mobile.events.data.microsoft.com</c>) and keeps a device id under <c>~/.cache/Microsoft/DeveloperTools</c>, unless
/// <c>ORT_DISABLE_TELEMETRY=1</c> is in the process environment when it starts; on Windows it writes ETW events.
/// <see cref="TurnOff"/> sets the variable in the native environment, which ONNX Runtime reads (.NET's
/// <see cref="Environment.SetEnvironmentVariable(string, string)"/> does not reach it on Unix), before the provider loads
/// it. A value already set when the process started is left as it is.
/// </summary>
public static partial class OnnxRuntimeTelemetry
{
    /// <summary>The variable ONNX Runtime reads.</summary>
    public const string Variable = "ORT_DISABLE_TELEMETRY";

    private static readonly Lock Gate = new();
    private static bool _turnedOff;

    /// <summary>The variable's value in the native environment, or <see langword="null"/> when it is not set.</summary>
    public static string? Setting => OperatingSystem.IsWindows()
        ? Environment.GetEnvironmentVariable(Variable)
        : Marshal.PtrToStringUTF8(GetEnv(Utf8(Variable)));

    /// <summary>Whether ONNX Runtime's telemetry is off in this process.</summary>
    public static bool IsOff => Setting == "1";

    /// <summary>
    /// Sets <c>ORT_DISABLE_TELEMETRY=1</c> in the native environment unless it is set already. Call before ONNX Runtime
    /// is first used; the provider and its factory do.
    /// </summary>
    public static void TurnOff()
    {
        lock (Gate)
        {
            if (_turnedOff)
            {
                return;
            }

            _turnedOff = true;
            if (Setting is not null)
            {
                return; // whoever started the process decided
            }

            // On Windows this is SetEnvironmentVariableW, the environment native code reads. CLIs the router starts inherit
            // .NET's copy, so they see it too.
            Environment.SetEnvironmentVariable(Variable, "1");
            if (!OperatingSystem.IsWindows())
            {
                _ = SetEnv(Utf8(Variable), Utf8("1"), 0);
            }
        }
    }

    /// <summary>A NUL-terminated UTF-8 string for libc.</summary>
    private static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text + "\0");

    [DllImport("libc", EntryPoint = "getenv")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern IntPtr GetEnv(byte[] name);

    [DllImport("libc", EntryPoint = "setenv")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int SetEnv(byte[] name, byte[] value, int overwrite);
}
