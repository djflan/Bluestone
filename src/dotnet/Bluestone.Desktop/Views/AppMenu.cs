using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Bluestone.Presentation;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Desktop.Views;

/// <summary>
/// The menu bar, following macOS conventions: an application menu, then File, Edit, Track, MIDI,
/// Transport, View, Window, and Help. On macOS it appears in the system menu bar; elsewhere the
/// in-window controls and key bindings provide the same commands.
/// </summary>
internal static class AppMenu
{
    public const string HelpUrl = "https://github.com/djflan/Bluestone#readme";

    /// <summary>Gestures handled by the menu bar, so the window must not bind them again.</summary>
    public static readonly (Key Key, KeyModifiers Extra)[] MenuGestures =
    [
        (Key.N, KeyModifiers.None), (Key.O, KeyModifiers.None), (Key.W, KeyModifiers.None), (Key.S, KeyModifiers.None),
        (Key.S, KeyModifiers.Shift), (Key.I, KeyModifiers.None), (Key.E, KeyModifiers.None), (Key.Z, KeyModifiers.None),
        (Key.Z, KeyModifiers.Shift), (Key.T, KeyModifiers.None), (Key.OemPeriod, KeyModifiers.None), (Key.OemPlus, KeyModifiers.None),
        (Key.OemMinus, KeyModifiers.None), (Key.M, KeyModifiers.None), (Key.A, KeyModifiers.None), (Key.D, KeyModifiers.None),
        (Key.X, KeyModifiers.None), (Key.C, KeyModifiers.None), (Key.V, KeyModifiers.None), (Key.OemQuestion, KeyModifiers.None),
        (Key.G, KeyModifiers.None), (Key.OemComma, KeyModifiers.None),
    ];

    public static void Install(MainWindow window, MainViewModel vm, KeyModifiers command)
    {
        KeyGesture Cmd(Key key, KeyModifiers extra = KeyModifiers.None) => new(key, command | extra);

        // Items added to the application menu; macOS supplies Services, Hide, Hide Others, Show All, and Quit.
        var about = Item("About Bluestone", () => _ = new AboutWindow().ShowDialog(window));
        var settings = Item("Settings…", () => window.ShowSettings(), Cmd(Key.OemComma));
        if (Avalonia.Application.Current is { } app)
        {
            NativeMenu.SetMenu(app, [about, new NativeMenuItemSeparator(), settings]);
        }

        var editor = vm.Editor;
        NativeMenuItem[] menus =
        [
            Submenu("File",
                Item("New Project", vm.NewProjectCommand, Cmd(Key.N)),
                Item("Open…", vm.OpenCommand, Cmd(Key.O)),
                new NativeMenuItemSeparator(),
                Item("Close Window", window.Close, Cmd(Key.W)),
                Item("Save", vm.SaveCommand, Cmd(Key.S)),
                Item("Save As…", vm.SaveAsCommand, Cmd(Key.S, KeyModifiers.Shift)),
                new NativeMenuItemSeparator(),
                Item("Import MIDI File…", vm.ImportMidiCommand, Cmd(Key.I)),
                Item("Export MIDI File…", vm.ExportMidiCommand, Cmd(Key.E))),
            Submenu("Edit",
                Item("Undo", vm.UndoCommand, Cmd(Key.Z)),
                Item("Redo", vm.RedoCommand, Cmd(Key.Z, KeyModifiers.Shift)),
                new NativeMenuItemSeparator(),
                Item("Cut", window.Cut, Cmd(Key.X)),
                Item("Copy", window.Copy, Cmd(Key.C)),
                Item("Paste", window.Paste, Cmd(Key.V)),
                Item("Duplicate", window.Duplicate, Cmd(Key.D)),
                Item("Delete    ⌫", window.Delete),
                Item("Split Clips at Playhead    B", vm.Arrangement.SplitAtPlayhead),
                Item("Rename Clip    Return", () => window.RenameSelectedClip()),
                Item("Select All", window.SelectAll, Cmd(Key.A)),
                Item("Deselect All    Esc", editor.SelectNone)),
            // Single-key shortcuts are shown in the titles but handled by the window, so they never
            // reach the menu bar while a text field is being edited.
            Submenu("Track",
                Item("New Track", vm.AddTrackCommand, Cmd(Key.T)),
                Item("Duplicate Tracks", vm.DuplicateTracksCommand),
                Item("Rename    Return", window.BeginRename),
                Item("Delete Tracks", vm.DeleteTracksCommand),
                new NativeMenuItemSeparator(),
                Item("Group Tracks", vm.Groups.GroupSelectedCommand, Cmd(Key.G)),
                Item("Remove from Group", vm.Groups.RemoveSelectedCommand),
                new NativeMenuItemSeparator(),
                Item("Mute    M", vm.ToggleMuteCommand),
                Item("Solo    S", vm.ToggleSoloCommand),
                Item("Arm for Recording", vm.ToggleArmSelectedCommand),
                new NativeMenuItemSeparator(),
                Item("Use Output for All Tracks", vm.UseOutputForAllTracksCommand),
                Item("Initialize Instrument…", vm.InitializeInstrumentCommand)),
            Submenu("MIDI",
                Item("Quantize    Q", editor.QuantizeCommand),
                Item("Quantize Note Ends    ⇧Q", editor.QuantizeEndsCommand),
                Item("Legato", editor.LegatoCommand),
                new NativeMenuItemSeparator(),
                Item("Transpose Up    ↑", () => editor.TransposeSelection(1)),
                Item("Transpose Down    ↓", () => editor.TransposeSelection(-1)),
                Item("Octave Up    ⇧↑", () => editor.TransposeSelection(12)),
                Item("Octave Down    ⇧↓", () => editor.TransposeSelection(-12)),
                Item("Nudge Later    →", () => editor.NudgeSelection(1)),
                Item("Nudge Earlier    ←", () => editor.NudgeSelection(-1)),
                new NativeMenuItemSeparator(),
                Item("Velocity Up    ⌥↑", () => editor.OffsetVelocity(2)),
                Item("Velocity Down    ⌥↓", () => editor.OffsetVelocity(-2)),
                new NativeMenuItemSeparator(),
                Check("Snap to Grid", editor, nameof(EditorViewModel.IsSnapEnabled), () => editor.IsSnapEnabled, () => editor.IsSnapEnabled = !editor.IsSnapEnabled),
                Item("Arrow Tool    1", () => editor.Tool = EditTool.Arrow),
                Item("Pencil Tool    2", () => editor.Tool = EditTool.Pencil),
                Item("Eraser Tool    3", () => editor.Tool = EditTool.Eraser)),
            Submenu("Transport",
                Item("Play / Stop    Space", vm.PlayPauseCommand),
                Item("Record    R", vm.RecordCommand),
                Item("Stop", vm.StopCommand),
                Item("Go to Start    Home", vm.ReturnToStartCommand),
                Item("Go to End    End", vm.GoToEndCommand),
                Item("Previous Bar    ,", vm.PreviousBarCommand),
                Item("Next Bar    .", vm.NextBarCommand),
                Item("Cycle Four Bars    L", vm.ToggleLoopCommand),
                new NativeMenuItemSeparator(),
                Check("Metronome    K", vm, nameof(MainViewModel.IsMetronomeEnabled), () => vm.IsMetronomeEnabled, () => vm.IsMetronomeEnabled = !vm.IsMetronomeEnabled),
                Check("Click While Playing", vm, nameof(MainViewModel.ClickWhilePlaying), () => vm.ClickWhilePlaying, () => vm.ClickWhilePlaying = !vm.ClickWhilePlaying),
                Check("Count-In", vm, nameof(MainViewModel.IsCountInEnabled), () => vm.IsCountInEnabled, () => vm.IsCountInEnabled = !vm.IsCountInEnabled),
                Check("Replace When Recording", vm, nameof(MainViewModel.IsReplaceRecording), () => vm.IsReplaceRecording, () => vm.IsReplaceRecording = !vm.IsReplaceRecording),
                Check("MIDI Thru", vm, nameof(MainViewModel.IsThruEnabled), () => vm.IsThruEnabled, () => vm.IsThruEnabled = !vm.IsThruEnabled),
                new NativeMenuItemSeparator(),
                Item("Panic", vm.PanicCommand, Cmd(Key.OemPeriod))),
            Submenu("View",
                Check("Inspector    I", vm, nameof(MainViewModel.IsInspectorVisible), () => vm.IsInspectorVisible, () => vm.IsInspectorVisible = !vm.IsInspectorVisible),
                Item("Piano Roll    P", () => window.ShowPane(LowerPane.PianoRoll)),
                Item("Event List    D", () => window.ShowPane(LowerPane.EventList)),
                Item("MIDI Monitor", () => window.ShowPane(LowerPane.Monitor)),
                Item("Messages", () => window.ShowPane(LowerPane.Messages)),
                new NativeMenuItemSeparator(),
                Item("Zoom In", vm.ZoomInCommand, Cmd(Key.OemPlus)),
                Item("Zoom Out", vm.ZoomOutCommand, Cmd(Key.OemMinus))),
            Submenu("Window",
                Item("Minimize", () => window.WindowState = WindowState.Minimized, Cmd(Key.M)),
                Item("Zoom", () => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized),
                new NativeMenuItemSeparator(),
                Item("Mixer", new RelayCommand(window.ShowMixer)),
                Item("Plugins…", new RelayCommand(window.ShowPlugins)),
                Item("Audio Settings…", new RelayCommand(window.ShowAudioSettings))),
            Submenu("Help",
                Item("Keyboard Shortcuts", window.ShowShortcuts, Cmd(Key.OemQuestion)),
                Item("Bluestone Help", () => _ = window.Launcher.LaunchUriAsync(new Uri(HelpUrl)))),
        ];
        foreach (var menu in menus)
        {
            ReturnToWorkspace(menu, window);
        }

        NativeMenu.SetMenu(window, [.. menus]);
    }

    /// <summary>Items that act within Settings or the window itself, so they leave Settings open.</summary>
    private static readonly HashSet<string> SettingsSafe =
        ["Close Window", "Minimize", "Zoom", "Mixer", "Plugins…", "Audio Settings…", "Keyboard Shortcuts", "Bluestone Help"];

    /// <summary>Items that edit a focused Settings field where it is, and otherwise act on the workspace.</summary>
    private static readonly HashSet<string> TextEditing = ["Cut", "Copy", "Paste", "Select All"];

    /// <summary>
    /// Makes every workspace and project item close Settings as it runs, so nothing changes behind it unseen (the window's own
    /// key bindings do the same through <see cref="MainWindow.InWorkspace"/>).
    /// </summary>
    private static void ReturnToWorkspace(NativeMenuItem item, MainWindow window)
    {
        foreach (var child in item.Menu?.Items.OfType<NativeMenuItem>() ?? [])
        {
            ReturnToWorkspace(child, window);
        }

        if (item.Menu is not null || item.Header is not { } header || SettingsSafe.Contains(header))
        {
            return;
        }

        // Every leaf item carries a command (Item and Check make one), so Settings closes before it runs.
        if (item.Command is { } command)
        {
            item.Command = TextEditing.Contains(header) ? window.InWorkspaceUnlessEditingText(command) : window.InWorkspace(command);
        }
    }

    private static NativeMenuItem Submenu(string header, params NativeMenuItemBase[] items)
    {
        var menu = new NativeMenu();
        foreach (var item in items)
        {
            menu.Add(item);
        }

        return new NativeMenuItem(header) { Menu = menu };
    }

    private static NativeMenuItem Item(string header, ICommand command, KeyGesture? gesture = null) =>
        new(header) { Command = command, Gesture = gesture };

    private static NativeMenuItem Item(string header, Action action, KeyGesture? gesture = null) =>
        new(header) { Command = new RelayCommand(action), Gesture = gesture };

    /// <summary>A checkable item kept in step with a view model property.</summary>
    private static NativeMenuItem Check(string header, INotifyPropertyChanged source, string property, Func<bool> isChecked, Action toggle)
    {
        var item = new NativeMenuItem(header) { ToggleType = MenuItemToggleType.CheckBox, IsChecked = isChecked(), Command = new RelayCommand(toggle) };
        source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == property)
            {
                item.IsChecked = isChecked();
            }
        };
        return item;
    }
}
