using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>
/// The mixer console (Window ▸ Mixer, or File ▸ Mixer… in the window's own menu). All behaviour is in
/// <see cref="MixerViewModel"/>; this window reads the meters about 30 times a second while it is open, and undo and
/// redo work from it as from the main window.
/// </summary>
public partial class MixerWindow : Window
{
    private readonly DispatcherTimer _meters = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private MainViewModel? _owner;

    public MixerWindow()
    {
        InitializeComponent();
        _meters.Tick += (_, _) => _owner?.Mixer.RefreshMeters(Stopwatch.GetTimestamp());
        Opened += (_, _) => _meters.Start();
        Closed += (_, _) => _meters.Stop();
    }

    /// <summary>Shows <paramref name="owner"/>'s mixer, with its undo and redo.</summary>
    public void Attach(MainViewModel owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        DataContext = owner.Mixer;
        var command = Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Z, command), Command = owner.UndoCommand });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Z, command | KeyModifiers.Shift), Command = owner.RedoCommand });
    }
}
