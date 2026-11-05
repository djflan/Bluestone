namespace Bluestone.Desktop.Views;

/// <summary>The mixer console (kept apart from the rest of the main window).</summary>
public partial class MainWindow
{
    private MixerWindow? _mixer;

    /// <summary>Opens the mixer console (Window ▸ Mixer), or brings it to the front.</summary>
    internal void ShowMixer()
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        if (_mixer is { } open)
        {
            open.Activate();
            return;
        }

        _mixer = new MixerWindow();
        _mixer.Attach(vm);
        _mixer.Closed += (_, _) => _mixer = null;
        _mixer.Show(this);
    }
}
