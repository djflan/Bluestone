using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bluestone.Desktop.Services;

/// <summary>
/// Whether the system asks apps for less motion: Reduce motion on macOS, "Show animations in Windows" off on Windows.
/// Linux has no common setting, so there it is always false. Read on the UI thread.
/// </summary>
internal static partial class SystemMotion
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const uint SpiGetClientAreaAnimation = 0x1042;

    private static bool _reportedFailure;

    /// <summary>
    /// Whether to reduce motion. If the system setting cannot be read, motion is reduced (the safe choice for someone who
    /// asked for less) and the failure is traced once.
    /// </summary>
    public static bool PrefersReducedMotion()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return MacReducesMotion() ?? Failed("NSWorkspace was unavailable");
            }

            if (OperatingSystem.IsWindows())
            {
                return WindowsReducesMotion() ?? Failed($"SystemParametersInfo failed with error {Marshal.GetLastPInvokeError()}");
            }

            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return Failed(ex.Message);
        }
    }

    private static bool Failed(string reason)
    {
        if (!_reportedFailure)
        {
            _reportedFailure = true;
            System.Diagnostics.Trace.TraceWarning($"The system's reduce-motion setting could not be read, so LEDs reduce motion: {reason}");
        }

        return true;
    }

    [SupportedOSPlatform("macos")]
    private static bool? MacReducesMotion()
    {
        var workspace = objc_msgSend(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
        return workspace == 0 ? null : objc_msgSend_bool(workspace, sel_registerName("accessibilityDisplayShouldReduceMotion")) != 0;
    }

    [SupportedOSPlatform("windows")]
    private static bool? WindowsReducesMotion() => SystemParametersInfoW(SpiGetClientAreaAnimation, 0, out var animates, 0) != 0 ? animates == 0 : null;

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjC)]
    private static partial nint objc_msgSend(nint receiver, nint selector);

    // The same entry point, for a selector that returns a BOOL (one byte).
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte objc_msgSend_bool(nint receiver, nint selector);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SystemParametersInfoW(uint action, uint parameter, out int value, uint winIni);
}
