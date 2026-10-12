using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>
/// Settings, shown in the workspace's place: the workspace grid is hidden while Settings is open and shown again as it was when
/// it closes, so no region, tab, size, or detached window changes. The host composes audio and plugins into it.
/// </summary>
public partial class MainWindow
{
    private Control? _focusBeforeSettings;

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
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.IsOpen))
            {
                ApplySettingsVisibility();
            }
        };
        ApplySettingsVisibility();

        // Escape closes Settings before the workspace's own shortcuts see it, unless an open drop-down or popup takes it first.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (Settings.IsOpen && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && !IsInOpenPopup(e.Source as Control))
            {
                Settings.Close();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>Whether Escape belongs to a popup first: <paramref name="source"/> is in a popup of its own, or a drop-down in Settings is open.</summary>
    private bool IsInOpenPopup(Control? source) =>
        (source is not null && TopLevel.GetTopLevel(source) != this)
        || SettingsHost.GetVisualDescendants().OfType<ComboBox>().Any(c => c.IsDropDownOpen);

    private void ApplySettingsVisibility()
    {
        var open = Settings.IsOpen;
        SettingsButton.IsChecked = open;

        // Closed, Settings has no content at all: its pages (and the audio page's meter timer) exist only while it is shown.
        SettingsHost.DataContext = open ? Settings : null;
        if (open == SettingsHost.IsVisible)
        {
            return;
        }

        if (open)
        {
            // Put focus back where it was when Settings closes, if that control is still there to take it.
            _focusBeforeSettings = FocusManager?.GetFocusedElement() as Control;
        }

        WorkspaceGrid.IsVisible = !open;
        SettingsHost.IsVisible = open;
        var restore = _focusBeforeSettings is { IsEffectivelyVisible: true } previous && TopLevel.GetTopLevel(previous) is not null ? previous : TrackList;
        Dispatcher.UIThread.Post(() => (open ? SettingsHost.CategoryList : restore).Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// <paramref name="command"/>, closing Settings first when it is open: a shortcut or menu item that edits the project or
    /// changes the workspace brings the workspace back, so nothing changes behind Settings unseen.
    /// </summary>
    internal ICommand InWorkspace(ICommand command) => new ReturningCommand(command, Settings);

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

/// <summary>A command that closes Settings before running another.</summary>
internal sealed class ReturningCommand(ICommand inner, SettingsViewModel settings) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => inner.CanExecuteChanged += value;
        remove => inner.CanExecuteChanged -= value;
    }

    public bool CanExecute(object? parameter) => inner.CanExecute(parameter);

    public void Execute(object? parameter)
    {
        settings.Close();
        inner.Execute(parameter);
    }
}
