using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bluestone.Desktop.Services;

/// <summary>
/// The macOS Dock and app switcher icon. Avalonia ignores <c>Window.Icon</c> on macOS, and Bluestone ships as a bare
/// executable rather than an app bundle with an .icns, so the icon is handed to AppKit directly.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class DockIcon
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    /// <summary>Shows <paramref name="png"/> as the app's icon. Call on the main thread.</summary>
    /// <exception cref="InvalidOperationException">Thrown when AppKit cannot create the image.</exception>
    public static void Set(byte[] png)
    {
        var data = Send(Send(Class("NSData"), "alloc"), Selector("initWithBytes:length:"), png, (nuint)png.Length);
        if (data == 0)
        {
            throw new InvalidOperationException("AppKit could not allocate the Dock icon data.");
        }

        var image = Send(Send(Class("NSImage"), "alloc"), Selector("initWithData:"), data);
        Send(data, "release");
        if (image == 0)
        {
            throw new InvalidOperationException("AppKit could not decode the Dock icon.");
        }

        Send(Send(Class("NSApplication"), "sharedApplication"), Selector("setApplicationIconImage:"), image);
        Send(image, "release");
    }

    private static nint Class(string name) => objc_getClass(name);

    private static nint Selector(string name) => sel_registerName(name);

    private static nint Send(nint receiver, string selector) => objc_msgSend(receiver, Selector(selector));

    private static nint Send(nint receiver, nint selector, nint argument) => objc_msgSend(receiver, selector, argument);

    private static nint Send(nint receiver, nint selector, byte[] bytes, nuint length) => objc_msgSend(receiver, selector, bytes, length);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjC)]
    private static partial nint objc_msgSend(nint receiver, nint selector);

    [LibraryImport(ObjC)]
    private static partial nint objc_msgSend(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjC)]
    private static partial nint objc_msgSend(nint receiver, nint selector, byte[] bytes, nuint length);
}
