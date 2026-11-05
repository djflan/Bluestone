using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bluestone.Platform.Windows;

/// <summary>The colours Windows draws a window's title bar in.</summary>
[SupportedOSPlatform("windows10.0.22000")]
public static partial class WindowFrame
{
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    /// <summary>Sets the title bar's background and text colours, each 0xRRGGBB. Returns false if Windows refused either.</summary>
    public static bool SetTitleBarColors(IntPtr window, uint background, uint text) =>
        Set(window, DwmwaCaptionColor, background) & Set(window, DwmwaTextColor, text);

    private static bool Set(IntPtr window, int attribute, uint rgb)
    {
        // COLORREF is 0x00BBGGRR.
        var colorRef = ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
        return DwmSetWindowAttribute(window, attribute, ref colorRef, sizeof(uint)) >= 0;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref uint value, int size);
}
