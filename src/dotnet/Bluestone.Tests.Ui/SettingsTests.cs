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
