using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>The shared docked and detached view for the track-owned mixer console.</summary>
/// <remarks>
/// The strips area sets a height tier on the view. At or above <c>Mixer.RoomyStripHeight</c> every band has its full
/// height. Below it the view is <c>tight</c>: one-line readouts and a shorter name block, with the star-sized rails giving
/// up height first, so the default docked mixer keeps its insert stack and sends. Below <c>Mixer.FullStripHeight</c> it
/// is also <c>short</c>: the processing band folds into a one-line cue that still opens every device and send.
/// </remarks>
public partial class MixerConsoleView : UserControl
{
    private const string TightClass = "tight";
    private const string ShortClass = "short";

    static MixerConsoleView()
    {
        // A class handler, not one on the view: the chain and Master flyouts render their rows in a popup outside the view.
        Button.KeyDownEvent.AddClassHandler<Button>(ToggleInsertPower, RoutingStrategies.Tunnel);
    }

    public MixerConsoleView()
    {
        InitializeComponent();
        StripsArea.SizeChanged += (_, e) => UpdateHeightClasses(e.NewSize.Height);
    }

    private void UpdateHeightClasses(double height)
    {
        var known = height > 0;
        Classes.Set(TightClass, known && height < Token("Mixer.RoomyStripHeight"));
        Classes.Set(ShortClass, known && height < Token("Mixer.FullStripHeight"));
    }

    private double Token(string key) =>
        this.TryFindResource(key, out var value) && value is double tokenHeight ? tokenHeight : double.PositiveInfinity;

    /// <summary>
    /// An insert row has one Tab stop, its name: Enter opens the device and Space turns it on or off, so a keyboard user
    /// reaches bypass without a second stop per row. Handled on the way down so the name button does not also click; this
    /// holds wherever the row is, in a strip or in a flyout.
    /// </summary>
    private static void ToggleInsertPower(Button button, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None || !ReferenceEquals(e.Source, button))
        {
            return;
        }

        if (button is { DataContext: MixerInsertViewModel insert } && button.Classes.Contains("insertName"))
        {
            insert.IsEnabled = !insert.IsEnabled;
            e.Handled = true;
        }
    }
}
