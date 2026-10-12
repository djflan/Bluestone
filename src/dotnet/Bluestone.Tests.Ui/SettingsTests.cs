using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Bluestone.Application.Audio;
using Bluestone.Desktop.Views;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// Settings in the real window: it takes the workspace's place and gives it back exactly as it was, opens from the keyboard and
/// the control bar, narrows its categories by search, and its pages' controls reach their view models.
/// </summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bluestone-settings", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private AudioViewModel Audio(AppUnderTest app, AudioPlayback audio) =>
        new(audio, new AudioSettingsStore(Path.Combine(_directory, "audio-settings.json")), new ImmediateDispatcher(), () => []);

    [Fact]
    public Task Settings_TakesTheWorkspacesPlace_AndGivesItBackUnchanged_WithoutTouchingTheProject() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        app.Window.Width = 1440;
        app.Window.Height = 900;
        using var audio = new AudioPlayback(app.Playback, app.Session, _ => null);
        app.Window.AttachAudio(Audio(app, audio));
        app.ViewModel.AddTrackCommand.Execute(null);
        app.ViewModel.Workspace.Browser.ShowCommand.Execute(null);
        app.ViewModel.Workspace.DetailExtent = 300;
        app.Settle();
        var project = app.Session.Project;
        var undo = app.ViewModel.UndoCommand.CanExecute(null);
        var workspace = app.ViewModel.Workspace;
        var before = (workspace.LeftExtent, workspace.RightExtent, workspace.DetailExtent, workspace.Browser.IsDocked, workspace.Inspector.IsDocked,
            app.ViewModel.LowerPane, app.ViewModel.SelectedTrack, workspace.ActiveDeviceView);
        var detailRow = app.Find<Grid>("MainArea").RowDefinitions[3].Height;

        app.Click(app.Find<Avalonia.Controls.Primitives.ToggleButton>("SettingsButton"));

        Assert.True(app.Window.Settings.IsOpen);
        Assert.False(app.Find<Grid>("WorkspaceGrid").IsVisible);
        Assert.True(app.Find<SettingsView>("SettingsHost").IsEffectivelyVisible);
        Assert.Equal(SettingsPage.Audio, app.Window.Settings.SelectedCategory?.Page);
        Assert.Single(app.Window.GetVisualDescendants().OfType<AudioSettingsPage>(), p => p.IsEffectivelyVisible);
        await app.SettleTransitionsAsync();
        app.SaveFrame("settings-audio");

        app.Press(Key.Escape);

        Assert.False(app.Window.Settings.IsOpen);
        Assert.True(app.Find<Grid>("WorkspaceGrid").IsVisible);
        Assert.Equal(before, (workspace.LeftExtent, workspace.RightExtent, workspace.DetailExtent, workspace.Browser.IsDocked, workspace.Inspector.IsDocked,
            app.ViewModel.LowerPane, app.ViewModel.SelectedTrack, workspace.ActiveDeviceView));
        Assert.Equal(detailRow, app.Find<Grid>("MainArea").RowDefinitions[3].Height);
        Assert.Same(project, app.Session.Project);
        Assert.Equal(undo, app.ViewModel.UndoCommand.CanExecute(null));

        // The keyboard opens it where it was.
        app.Press(Key.OemComma, AppUnderTest.Command);
        Assert.True(app.Window.Settings.IsOpen);
        Assert.Equal(SettingsPage.Audio, app.Window.Settings.SelectedCategory?.Page);
    });

    [Fact]
    public Task TheSearch_NarrowsTheCategories_AndTheAudioPagesControlsWork() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        using var audio = new AudioPlayback(app.Playback, app.Session, _ => null);
        var audioSettings = Audio(app, audio);
        app.Window.AttachAudio(audioSettings);
        app.Window.ShowSettings();
        app.Settle();
        var settings = app.Window.Settings;
        var host = app.Find<SettingsView>("SettingsHost");
        Assert.Equal(["Audio", "Plugins"], settings.Categories.Select(c => c.Name));

        settings.SearchText = "quarantine";
        app.Settle();
        Assert.Equal(SettingsPage.Plugins, Assert.Single(settings.Categories).Page);
        Assert.Equal("Plugin hosting is not available in this session.", AppUnderTest.Within<TextBlock>(host, t => t.Text == "Plugin hosting is not available in this session.").Text);

        settings.SearchText = "buffer";
        app.Settle();
        Assert.Equal(SettingsPage.Audio, Assert.Single(settings.Categories).Page);

        settings.SearchText = "nothing like this";
        app.Settle();
        Assert.True(settings.IsEmpty);
        app.Click(AppUnderTest.Within<Button>(host, b => Avalonia.Automation.AutomationProperties.GetName(b) == "Clear search"));
        Assert.Equal(2, settings.Categories.Count);

        settings.Open(SettingsPage.Audio);
        app.Settle();
        var page = app.Window.GetVisualDescendants().OfType<AudioSettingsPage>().Single(p => p.IsEffectivelyVisible);
        app.Choose(AppUnderTest.ComboNamed(page, "Buffer size"), b => b is 512);
        Assert.Equal(512, audioSettings.BufferFrames);
        app.Choose(AppUnderTest.ComboNamed(page, "Sample rate"), r => r is 44_100);
        Assert.Equal(44_100, audioSettings.SampleRate);
        Assert.False(AppUnderTest.Within<Button>(page, b => Equals(b.Content, "Stop audio")).IsEffectivelyEnabled);
    });

    [Fact]
    public Task Escape_ClosesAnOpenDropDownFirst_ThenSettings_AndFocusReturnsWhereItWas() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        using var audio = new AudioPlayback(app.Playback, app.Session, _ => null);
        app.Window.AttachAudio(Audio(app, audio));
        app.ViewModel.Workspace.Browser.ShowCommand.Execute(null);
        app.Settle();
        var search = AppUnderTest.Within<TextBox>(app.Find<BrowserView>("DockedBrowser"), t => t.Name == "SearchBox");
        search.Focus();
        app.Settle();

        app.Press(Key.OemComma, AppUnderTest.Command);
        await app.SettleTransitionsAsync();
        var page = app.Window.GetVisualDescendants().OfType<AudioSettingsPage>().Single(p => p.IsEffectivelyVisible);
        var buffer = AppUnderTest.ComboNamed(page, "Buffer size");
        app.Click(buffer);
        Assert.True(buffer.IsDropDownOpen);

        app.Press(Key.Escape);
        Assert.False(buffer.IsDropDownOpen);
        Assert.True(app.Window.Settings.IsOpen);

        app.Press(Key.Escape);
        await app.SettleTransitionsAsync();
        Assert.False(app.Window.Settings.IsOpen);
        Assert.Same(search, app.Window.FocusManager?.GetFocusedElement());
    });

    [Fact]
    public Task WhileSettingsIsOpen_WorkspaceShortcutsDoNothing_AndItsPagesExistOnlyWhileShown() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        using var audio = new AudioPlayback(app.Playback, app.Session, _ => null);
        app.Window.AttachAudio(Audio(app, audio));
        app.ViewModel.AddTrackCommand.Execute(null);
        app.Settle();
        Assert.Empty(app.Window.GetVisualDescendants().OfType<AudioSettingsPage>());

        app.Press(Key.OemComma, AppUnderTest.Command);
        await app.SettleTransitionsAsync();
        Assert.Single(app.Window.GetVisualDescendants().OfType<AudioSettingsPage>());
        var inspector = app.ViewModel.IsInspectorVisible;
        var browser = app.ViewModel.Workspace.Browser.IsDocked;
        var muted = app.ViewModel.Tracks[0].IsMuted;

        app.Press(Key.I);
        app.Press(Key.B, RawInputModifiers.Shift);
        app.Press(Key.M);

        Assert.Equal(inspector, app.ViewModel.IsInspectorVisible);
        Assert.Equal(browser, app.ViewModel.Workspace.Browser.IsDocked);
        Assert.Equal(muted, app.ViewModel.Tracks[0].IsMuted);

        // A command shortcut that edits the project brings the workspace back first, so the edit is seen.
        app.Press(Key.T, AppUnderTest.Command);
        Assert.False(app.Window.Settings.IsOpen);
        Assert.True(app.Find<Grid>("WorkspaceGrid").IsVisible);
        Assert.Equal(2, app.ViewModel.Tracks.Count);

        await app.SettleTransitionsAsync();
        Assert.Empty(app.Window.GetVisualDescendants().OfType<AudioSettingsPage>());
        app.Press(Key.I);
        Assert.NotEqual(inspector, app.ViewModel.IsInspectorVisible);
    });

    [Fact]
    public Task SelectAll_EditsTheFocusedSettingsField_ButOtherwiseReturnsToTheWorkspaceFirst() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(nativeMenuBar: true);
        var menu = NativeMenu.GetMenu(app.Window)!;
        var selectAll = menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Edit").Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Select All");
        app.Window.ShowSettings();
        await app.SettleTransitionsAsync();
        var search = AppUnderTest.Within<TextBox>(app.Find<SettingsView>("SettingsHost"), t => t.Name == "SearchBox");

        search.Focus();
        app.Settle();
        selectAll.Command!.Execute(null);
        Assert.True(app.Window.Settings.IsOpen);

        var done = AppUnderTest.Within<Button>(app.Find<SettingsView>("SettingsHost"), b => b.Name == "CloseSettingsButton");
        Assert.True(done.Focus());
        app.Settle();
        selectAll.Command!.Execute(null);
        Assert.False(app.Window.Settings.IsOpen);
    });

    [Fact]
    public Task AnEmptyInstrumentsCategory_LeadsToThePluginSettings() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        app.ViewModel.Workspace.Browser.ShowCommand.Execute(null);
        app.ViewModel.Browser.ShowCategory(BrowserCategory.Instruments);
        app.Settle();

        app.Click(AppUnderTest.Within<Button>(app.Find<BrowserView>("DockedBrowser"), b => b.Name == "PluginSettingsLink"));

        Assert.True(app.Window.Settings.IsOpen);
        Assert.Equal(SettingsPage.Plugins, app.Window.Settings.SelectedCategory?.Page);
    });

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();
    }
}
