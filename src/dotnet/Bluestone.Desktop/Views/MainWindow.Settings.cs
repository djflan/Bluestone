using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>
/// Settings, shown in the workspace's place: the workspace grid is hidden while Settings is open and shown again as it was when
/// it closes, so no region, tab, size, or detached window changes. The host composes audio and plugins into it.
/// </summary>
public partial class MainWindow
{
    /// <summary>The Settings surface: its pages are the audio and plugin view models the host attaches.</summary>
    public SettingsViewModel Settings { get; } = new();

    /// <summary>Gives Settings the audio page; without it the page says audio is not available.</summary>
    public void AttachAudio(AudioViewModel audio) => Settings.Audio = audio;

    /// <summary>Opens Settings at <paramref name="page"/>, or where it was last.</summary>
    internal void ShowSettings(SettingsPage? page = null) => Settings.Open(page);

    /// <summary>Opens Settings ▸ Audio (Window ▸ Audio Settings… in the macOS menu bar, File ▸ Audio Settings… elsewhere).</summary>
    internal void ShowAudioSettings() => ShowSettings(SettingsPage.Audio);

    /// <summary>Opens Settings ▸ Plugins (Window ▸ Plugins… in the macOS menu bar, File ▸ Plugins… elsewhere).</summary>
    internal void ShowPlugins() => ShowSettings(SettingsPage.Plugins);

    private void AttachSettings(MainViewModel vm)
    {
        Settings.Plugins = vm.Plugins;
        vm.Browser.PluginSettingsRequested += (_, _) => ShowPlugins();
        SettingsHost.DataContext = Settings;
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.IsOpen))
            {
                ApplySettingsVisibility();
            }
        };
        ApplySettingsVisibility();

        // Escape closes Settings wherever focus is, before the workspace's own shortcuts see it.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (Settings.IsOpen && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                Settings.Close();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    private void ApplySettingsVisibility()
    {
        var open = Settings.IsOpen;
        WorkspaceGrid.IsVisible = !open;
        SettingsHost.IsVisible = open;
        SettingsButton.IsChecked = open;
        Dispatcher.UIThread.Post(() => (open ? SettingsHost.CategoryList : (Control)TrackList).Focus(), DispatcherPriority.Loaded);
    }

    private void OnAudioSettingsClick(object? sender, RoutedEventArgs e) => ShowAudioSettings();

    private void OnShowPlugins(object? sender, RoutedEventArgs e) => ShowPlugins();

    private void OnSettingsClick(object? sender, RoutedEventArgs e) => ShowSettings();

    private void OnSettingsButtonClick(object? sender, RoutedEventArgs e)
    {
        if (Settings.IsOpen)
        {
            Settings.Close();
        }
        else
        {
            ShowSettings();
        }
    }
}
