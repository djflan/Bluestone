using Avalonia.Interactivity;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>The audio settings window, opened from the File menu (kept apart from the rest of the main window).</summary>
public partial class MainWindow
{
    private AudioViewModel? _audio;

    /// <summary>Gives the window the audio settings to show; without it the menu item does nothing.</summary>
    public void AttachAudio(AudioViewModel audio) => _audio = audio;

    private void OnAudioSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (_audio is { } audio)
        {
            new AudioWindow(audio).Show(this);
        }
    }
}
