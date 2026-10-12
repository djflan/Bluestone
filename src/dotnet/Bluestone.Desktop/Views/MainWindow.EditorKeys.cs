using Avalonia.Input;
using Bluestone.Application.Plugins;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>
/// Keys typed in a plugin editor whose keys go to Bluestone arrive here (from the editor's worker, as USB HID usages) and
/// are raised on this window as ordinary key events, so the same shortcut and computer-keyboard MIDI handling serves them.
/// </summary>
public partial class MainWindow
{
    private void AttachEditorKeys(MainViewModel viewModel)
    {
        if (viewModel.EditorWindows is { } windows)
        {
            windows.KeyForwarded += (_, key) => RaiseEditorKey(key);
        }
    }

    /// <summary>Raises a key from a plugin editor on this window; keys Bluestone does not use are dropped.</summary>
    internal void RaiseEditorKey(PluginEditorKey key)
    {
        if (ToAvalonia(key.Usage) is not { } mapped)
        {
            return;
        }

        var modifiers = KeyModifiers.None;
        modifiers |= (key.Modifiers & PluginEditorKey.Shift) != 0 ? KeyModifiers.Shift : KeyModifiers.None;
        modifiers |= (key.Modifiers & PluginEditorKey.Control) != 0 ? KeyModifiers.Control : KeyModifiers.None;
        modifiers |= (key.Modifiers & PluginEditorKey.Alt) != 0 ? KeyModifiers.Alt : KeyModifiers.None;
        modifiers |= (key.Modifiers & PluginEditorKey.Meta) != 0 ? KeyModifiers.Meta : KeyModifiers.None;
        RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = key.Down ? KeyDownEvent : KeyUpEvent,
            Key = mapped.Key,
            PhysicalKey = mapped.Physical,
            KeyModifiers = modifiers,
            Source = this,
        });
    }

    /// <summary>The Avalonia key and physical key for a USB HID keyboard usage, for the keys Bluestone uses.</summary>
    internal static (Key Key, PhysicalKey Physical)? ToAvalonia(int usage) => usage switch
    {
        >= 4 and <= 29 => (Key.A + (usage - 4), PhysicalKey.A + (usage - 4)),
        >= 30 and <= 38 => (Key.D1 + (usage - 30), PhysicalKey.Digit1 + (usage - 30)),
        39 => (Key.D0, PhysicalKey.Digit0),
        40 => (Key.Return, PhysicalKey.Enter),
        41 => (Key.Escape, PhysicalKey.Escape),
        42 => (Key.Back, PhysicalKey.Backspace),
        43 => (Key.Tab, PhysicalKey.Tab),
        44 => (Key.Space, PhysicalKey.Space),
        45 => (Key.OemMinus, PhysicalKey.Minus),
        46 => (Key.OemPlus, PhysicalKey.Equal),
        47 => (Key.OemOpenBrackets, PhysicalKey.BracketLeft),
        48 => (Key.OemCloseBrackets, PhysicalKey.BracketRight),
        49 => (Key.OemPipe, PhysicalKey.Backslash),
        51 => (Key.OemSemicolon, PhysicalKey.Semicolon),
        52 => (Key.OemQuotes, PhysicalKey.Quote),
        53 => (Key.OemTilde, PhysicalKey.Backquote),
        54 => (Key.OemComma, PhysicalKey.Comma),
        55 => (Key.OemPeriod, PhysicalKey.Period),
        56 => (Key.OemQuestion, PhysicalKey.Slash),
        >= 58 and <= 69 => (Key.F1 + (usage - 58), PhysicalKey.F1 + (usage - 58)),
        74 => (Key.Home, PhysicalKey.Home),
        75 => (Key.PageUp, PhysicalKey.PageUp),
        76 => (Key.Delete, PhysicalKey.Delete),
        77 => (Key.End, PhysicalKey.End),
        78 => (Key.PageDown, PhysicalKey.PageDown),
        79 => (Key.Right, PhysicalKey.ArrowRight),
        80 => (Key.Left, PhysicalKey.ArrowLeft),
        81 => (Key.Down, PhysicalKey.ArrowDown),
        82 => (Key.Up, PhysicalKey.ArrowUp),
        _ => null,
    };
}
