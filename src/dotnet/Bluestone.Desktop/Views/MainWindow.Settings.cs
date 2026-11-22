using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bluestone.Desktop.Controls;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>
/// Settings is shown over the workspace without changing its regions, tabs, sizes, or detached windows.
/// The host composes audio and plugins into it.
/// </summary>
public partial class MainWindow
{
    private Control? _focusBeforeSettings;
    private bool _settingsDismissWithoutRestore;
    private bool _settingsWindowClosed;
    private int _settingsFocusVersion;

    /// <summary>The Settings surface: its pages are the audio and plugin view models the host attaches.</summary>
    public SettingsViewModel Settings { get; } = new();

    /// <summary>Gives Settings the audio page; without it the page says audio is not available.</summary>
    public void AttachAudio(AudioViewModel audio) => Settings.Audio = audio;

    /// <summary>Opens Settings at <paramref name="page"/>, or where it was last.</summary>
    internal void ShowSettings(SettingsPage? page = null) => Settings.Open(page);

    /// <summary>Opens Settings ▸ Audio (Window ▸ Audio Settings…).</summary>
    internal void ShowAudioSettings() => ShowSettings(SettingsPage.Audio);

    /// <summary>Opens Settings ▸ Plugins (Window ▸ Plugins…).</summary>
    internal void ShowPlugins() => ShowSettings(SettingsPage.Plugins);

    private void AttachSettings(MainViewModel vm)
    {
        Settings.Plugins = vm.Plugins;
        Settings.Display = vm.Display;
        vm.Browser.PluginSettingsRequested += (_, _) => ShowPlugins();
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.IsOpen))
            {
                ApplySettingsVisibility();
            }
        };
        ApplySettingsVisibility();

        // Dismissing a nested popup must not swallow a click on the workspace or the Settings gear. Popups are set up when
        // Settings opens or changes page and before input inside it can open one, not on every layout pass.
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.IsOpen) or nameof(SettingsViewModel.SelectedCategory))
            {
                Dispatcher.UIThread.Post(ConfigureSettingsPopups, DispatcherPriority.Loaded);
            }
        };
        SettingsHost.AddHandler(PointerPressedEvent, (_, _) => ConfigureSettingsPopups(), RoutingStrategies.Tunnel, handledEventsToo: true);
        SettingsHost.AddHandler(KeyDownEvent, (_, _) => ConfigureSettingsPopups(), RoutingStrategies.Tunnel, handledEventsToo: true);

        // Workspace commands close Settings before acting on the workspace or project.
        WorkspaceTools.AddHandler(Button.ClickEvent, (_, _) =>
        {
            if (Settings.IsOpen)
            {
                Settings.Close();
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!Settings.IsOpen || e.Source is not Visual source)
            {
                return;
            }

            var inPopup = IsInSettingsPopup(source);
            var inSettings = IsWithin(source, SettingsHost) || new Rect(SettingsHost.Bounds.Size).Contains(e.GetPosition(SettingsHost));
            if (inSettings && !inPopup && OpenSettingsPicker() is { } picker)
            {
                picker.Dismiss();
                e.Handled = true;
                return;
            }

            // A click elsewhere in Settings only closes an open drop-down, as it does the picker; it presses nothing under it.
            if (inSettings && !inPopup && OpenSettingsPopup() is { } dropDown)
            {
                dropDown.Close();
                e.Handled = true;
                return;
            }

            if (inSettings || inPopup || IsWithin(source, SettingsButton)
                || new Rect(SettingsButton.Bounds.Size).Contains(e.GetPosition(SettingsButton)))
            {
                return;
            }

            CloseSettingsWithoutRestoringFocus();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        AddHandler(GotFocusEvent, (_, e) =>
        {
            if (Settings.IsOpen && e.NavigationMethod != NavigationMethod.Pointer && e.Source is Visual source && !IsInSettings(source))
            {
                CloseSettingsWithoutRestoringFocus();
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        Closed += (_, _) =>
        {
            _settingsWindowClosed = true;
            _settingsFocusVersion++;
            Settings.Close();
        };

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

    /// <summary>Whether Escape belongs to a popup first: <paramref name="source"/> is in a popup of its own, or a drop-down or other popup in Settings (a color picker) is open.</summary>
    private bool IsInOpenPopup(Control? source) =>
        (source is not null && TopLevel.GetTopLevel(source) != this)
        || SettingsHost.GetVisualDescendants().OfType<Popup>().Any(p => p.IsOpen);

    private bool IsInSettings(Visual source) => IsWithin(source, SettingsHost) || IsInSettingsPopup(source);

    private bool IsInSettingsPopup(Visual source) =>
        SettingsHost.GetVisualDescendants().OfType<Popup>().Any(p => p.IsOpen && p.Child is { } child && IsWithin(source, child));

    private ColourPickerPopup? OpenSettingsPicker() =>
        SettingsHost.GetVisualDescendants().OfType<ColourPickerPopup>().FirstOrDefault(p => p.IsOpen);

    // Only a light-dismiss popup: one that stays open on a click outside must not swallow clicks in Settings.
    private Popup? OpenSettingsPopup() =>
        SettingsHost.GetVisualDescendants().OfType<Popup>().FirstOrDefault(p => p.IsOpen && p.IsLightDismissEnabled);

    private void ConfigureSettingsPopups()
    {
        if (!Settings.IsOpen)
        {
            return;
        }

        foreach (var popup in SettingsHost.GetVisualDescendants().OfType<Popup>())
        {
            if (!popup.OverlayDismissEventPassThrough)
            {
                popup.SetCurrentValue(Popup.OverlayDismissEventPassThroughProperty, true);
            }

            if (!ReferenceEquals(popup.OverlayInputPassThroughElement, SettingsButton))
            {
                popup.SetCurrentValue(Popup.OverlayInputPassThroughElementProperty, SettingsButton);
            }
        }
    }

    private static bool IsWithin(Visual source, Visual container) =>
        ReferenceEquals(source, container) || source.GetVisualAncestors().Contains(container);

    private void CloseSettingsWithoutRestoringFocus()
    {
        _settingsDismissWithoutRestore = true;
        try
        {
            OpenSettingsPicker()?.DismissWithoutRestoringFocus();
            Settings.Close();
        }
        finally
        {
            _settingsDismissWithoutRestore = false;
        }
    }

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

        var version = ++_settingsFocusVersion;
        if (open)
        {
            // Put focus back where it was when Settings closes, if that control is still there to take it.
            if (FocusManager?.GetFocusedElement() is not Control focused || !IsInSettings(focused))
            {
                _focusBeforeSettings = FocusManager?.GetFocusedElement() as Control;
            }
        }

        SettingsHost.IsVisible = open;
        if (_settingsWindowClosed || (!open && _settingsDismissWithoutRestore))
        {
            return;
        }

        var focusedWhenPosted = FocusManager?.GetFocusedElement();
        Dispatcher.UIThread.Post(() =>
        {
            if (_settingsWindowClosed || version != _settingsFocusVersion || Settings.IsOpen != open
                || !ReferenceEquals(FocusManager?.GetFocusedElement(), focusedWhenPosted))
            {
                return;
            }

            if (open)
            {
                if (!SettingsHost.CategoryList.Focus(NavigationMethod.Tab))
                {
                    SettingsHost.SearchBox.Focus(NavigationMethod.Tab);
                }
            }
            else if (_focusBeforeSettings is not { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Focusable: true } previous
                || TopLevel.GetTopLevel(previous) != this || !previous.Focus(NavigationMethod.Tab))
            {
                if (!Timeline.Focus(NavigationMethod.Tab))
                {
                    SettingsButton.Focus(NavigationMethod.Tab);
                }
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// <paramref name="command"/>, closing Settings first when it is open: a shortcut or menu item that edits the project or
    /// changes the workspace leaves those changes unobscured.
    /// </summary>
    internal ICommand InWorkspace(ICommand command) => new ReturningCommand(command, () => Settings.IsOpen, Settings.Close);

    /// <summary>
    /// <paramref name="command"/> (cut, copy, paste, select all), which edits text in a Settings field where it is, and otherwise
    /// closes Settings before it acts on tracks, clips, or notes.
    /// </summary>
    internal ICommand InWorkspaceUnlessEditingText(ICommand command) =>
        new ReturningCommand(
            command,
            () => Settings.IsOpen && !(FocusManager?.GetFocusedElement() is TextBox field && IsInSettings(field)),
            Settings.Close);

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

/// <summary>A command that runs <paramref name="leave"/> (closing Settings) before another, when <paramref name="shouldLeave"/> says so.</summary>
internal sealed class ReturningCommand(ICommand inner, Func<bool> shouldLeave, Action leave) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => inner.CanExecuteChanged += value;
        remove => inner.CanExecuteChanged -= value;
    }

    public bool CanExecute(object? parameter) => inner.CanExecute(parameter);

    public void Execute(object? parameter)
    {
        if (shouldLeave())
        {
            leave();
        }

        inner.Execute(parameter);
    }
}
