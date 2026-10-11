using Avalonia.Interactivity;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>The audio settings window and the mixer console (kept apart from the rest of the main window).</summary>
public partial class MainWindow
{
    private AudioViewModel? _audio;
    private MixerWindow? _mixer;

    /// <summary>Gives the window the audio settings to show; without it the menu item does nothing.</summary>
    public void AttachAudio(AudioViewModel audio) => _audio = audio;

    private void OnAudioSettingsClick(object? sender, RoutedEventArgs e) => ShowAudioSettings();

    /// <summary>Opens the audio settings (Window ▸ Audio Settings… in the macOS menu bar, File ▸ Audio Settings… elsewhere).</summary>
    internal void ShowAudioSettings()
    {
        if (_audio is { } audio)
        {
            new AudioWindow(audio).Show(this);
        }
    }

    /// <summary>Opens the mixer console (Window ▸ Mixer in the macOS menu bar, File ▸ Mixer… elsewhere), or brings it to the front.</summary>
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

    private void OnShowMixer(object? sender, RoutedEventArgs e) => ShowMixer();
}
