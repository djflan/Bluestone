using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bluestone.Desktop.Views;

/// <summary>The keyboard shortcut reference, shown from Help ▸ Keyboard Shortcuts.</summary>
internal static class Shortcuts
{
    /// <summary>(Group, action, keys). "⌘" is shown as Ctrl on Windows and Linux.</summary>
    public static readonly (string Group, string Action, string Keys)[] All =
    [
        ("Transport", "Play / stop", "Space"),
        ("Transport", "Record (punch in and out while playing)", "R"),
        ("Transport", "Metronome on / off", "K"),
        ("Transport", "Go to start / end", "Home  /  End"),
        ("Transport", "Previous / next bar", ",  /  ."),
        ("Transport", "Loop on / off (the range stays; four bars from the playhead if there is none)", "L"),
        ("Transport", "Loop the selected clips or notes", "⌘ L"),
        ("Transport", "Draw / move / resize the loop", "Drag in the loop strip, on the loop, or on its edge"),
        ("Transport", "Ignore the grid while dragging the loop", "⌘-drag"),
        ("Transport", "Select the loop, then nudge it by the grid / a bar", "Click it, then ←  →  /  ⇧ ←  →"),
        ("Transport", "Turn looping on or off from the ruler", "Double-click the loop"),
        ("Transport", "Panic: silence every output", "⌘ ."),
        ("Tracks", "Select previous / next track", "↑  /  ↓"),
        ("Tracks", "Extend the selection", "⇧ ↑  /  ⇧ ↓,  ⇧-click"),
        ("Tracks", "Add or remove a track from the selection", "⌘-click"),
        ("Tracks", "Select all tracks", "⌘ A"),
        ("Tracks", "Rename the selected track", "Return"),
        ("Tracks", "Mute / solo the selected tracks", "M  /  S"),
        ("Tracks", "New track", "⌘ T"),
        ("Tracks", "Duplicate the selected tracks", "⌘ D"),
        ("Tracks", "Delete the selected tracks", "⌫"),
        ("Tracks", "Group the selected tracks", "⌘ G"),
        ("Tracks", "Add to a group / remove from it", "Right-click the track"),
        ("Arrangement", "Select clips / add / add or remove", "Click  /  ⇧-click  /  ⌘-click"),
        ("Arrangement", "Move clips, also to another track / copy them", "Drag  /  ⌥-drag"),
        ("Arrangement", "Trim or extend a clip", "Drag its edge"),
        ("Arrangement", "Open a clip in the piano roll", "Double-click it"),
        ("Arrangement", "New clip", "Double-click empty space"),
        ("Arrangement", "Rename a clip", "Double-click its name, or Return"),
        ("Arrangement", "Split clips at the playhead", "B"),
        ("Arrangement", "Duplicate / delete the selected clips", "⌘ D  /  ⌫"),
        ("Arrangement", "Add / move / delete an automation point", "Click  /  drag  /  ⌥-click"),
        ("Arrangement", "Switch a point between hold and ramp", "Double-click it"),
        ("Piano roll", "Arrow / pencil / eraser tool", "1  /  2  /  3"),
        ("Piano roll", "Add a note", "Double-click, or click with the pencil"),
        ("Piano roll", "Copy while dragging / ignore the grid", "⌥-drag  /  ⌘-drag"),
        ("Piano roll", "Move the selection by a grid step / four steps", "←  →  /  ⇧ ←  →"),
        ("Piano roll", "Transpose a semitone / an octave", "↑  ↓  /  ⇧ ↑  ↓"),
        ("Piano roll", "Velocity up / down", "⌥ ↑  /  ⌥ ↓"),
        ("Piano roll", "Quantize / quantize note ends", "Q  /  ⇧ Q"),
        ("Piano roll", "Select all notes / deselect", "⌘ A  /  Esc"),
        ("Piano roll", "Copy, cut, paste at the playhead, duplicate", "⌘ C,  ⌘ X,  ⌘ V,  ⌘ D"),
        ("Piano roll", "Delete the selected notes", "⌫"),
        ("Piano roll", "Zoom time / key height", "⌘-scroll  /  ⌥-scroll"),
        ("Piano roll", "Zoom and scroll in time", "Drag up or down / sideways in its ruler"),
        ("View", "Inspector / Browser", "I  /  ⇧ B"),
        ("View", "Device View", "E"),
        ("View", "Piano roll / event list", "P  /  D"),
        ("View", "Zoom in / out", "⌘ =  /  ⌘ −"),
        ("View", "Zoom and scroll in time", "Drag up or down / sideways in the beat ruler"),
        ("View", "Zoom with the pointer", "⌘-scroll, pinch, or scroll over the ruler"),
        ("View", "Zoom to fit the project / the selected clips / the loop", "⌘ 0  /  ⇧ ⌘ 0  /  ⌥ ⌘ 0"),
        ("View", "Settings / back to the workspace", "⌘ ,  /  Esc"),
        ("Input", "Computer keyboard as MIDI input on / off", "`"),
        ("File", "New / open project", "⌘ N  /  ⌘ O"),
        ("File", "Save / save as", "⌘ S  /  ⇧ ⌘ S"),
        ("File", "Import / export MIDI", "⌘ I  /  ⌘ E"),
        ("File", "Open a project or MIDI file", "Drag it onto the window"),
        ("Edit", "Undo / redo", "⌘ Z  /  ⇧ ⌘ Z"),
        ("Help", "This list", "⌘ /"),
    ];

    public static string ForPlatform(string keys) =>
        OperatingSystem.IsMacOS() ? keys : keys.Replace("⌘", "Ctrl", StringComparison.Ordinal).Replace("⇧", "Shift", StringComparison.Ordinal).Replace("⌥", "Alt", StringComparison.Ordinal).Replace("⌫", "Delete", StringComparison.Ordinal);

    public static Window CreateWindow()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Avalonia.Thickness(24) };
        var row = 0;
        foreach (var group in All.GroupBy(s => s.Group))
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var header = new TextBlock { Text = group.Key.ToUpperInvariant(), Classes = { "overline" }, Margin = new Avalonia.Thickness(0, row == 0 ? 0 : 14, 0, 6) };
            Grid.SetRow(header, row++);
            Grid.SetColumnSpan(header, 2);
            grid.Children.Add(header);

            foreach (var (_, action, keys) in group)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var name = new TextBlock { Text = action, Margin = new Avalonia.Thickness(0, 3, 24, 3) };
                var gesture = new TextBlock { Text = ForPlatform(keys), Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Avalonia.Thickness(0, 3) };
                Grid.SetRow(name, row);
                Grid.SetRow(gesture, row++);
                Grid.SetColumn(gesture, 1);
                grid.Children.Add(name);
                grid.Children.Add(gesture);
            }
        }

        return new Window
        {
            Title = "Keyboard Shortcuts",
            Width = 520,
            Height = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = grid },
            Background = new SolidColorBrush(Color.Parse("#1C1C1E")),
        };
    }
}
