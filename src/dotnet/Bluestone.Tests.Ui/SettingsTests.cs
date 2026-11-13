using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Bluestone.Application.Audio;
using Bluestone.Audio.Devices;
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

    [Theory]
    [InlineData(1040)]
    [InlineData(1440)]
    public Task TheAudioPage_ShowsWhatTheDeviceRunsAt_ResamplingAndDropouts(double width) => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        app.Window.Width = width;
        app.Window.Height = 900;
        var backend = new DroppingBackend();
        using var audio = new AudioPlayback(app.Playback, app.Session, _ => null, backend: backend, checkInterval: Timeout.InfiniteTimeSpan);
        var audioSettings = new AudioViewModel(audio, new AudioSettingsStore(Path.Combine(_directory, "audio-settings.json")), new ImmediateDispatcher(), backend.ListDevices, listHosts: () => ["WASAPI"]);
        app.Window.AttachAudio(audioSettings);
        app.Window.ShowSettings(SettingsPage.Audio);
        await app.SettleTransitionsAsync();
        var page = app.Window.GetVisualDescendants().OfType<AudioSettingsPage>().Single(p => p.IsEffectivelyVisible);
        app.Choose(AppUnderTest.ComboNamed(page, "Sample rate"), r => r is 44_100);
        app.Choose(AppUnderTest.ComboNamed(page, "Buffer size"), b => b is AudioSettings.Automatic);
        audioSettings.StartCommand.Execute(null);
        audioSettings.RefreshMeters();
        app.Settle();

        TextBlock Named(string name) => AppUnderTest.Within<TextBlock>(page, t => Avalonia.Automation.AutomationProperties.GetName(t) == name);
        Assert.True(Named("Resampling warning").IsEffectivelyVisible);
        Assert.Equal("Running at 44.1 kHz · 441 samples / 10.00 ms", Named("Device format").Text);
        var health = Named("Audio dropouts");
        Assert.True(health.IsEffectivelyVisible);
        Assert.Contains("warning", health.Classes);
        Assert.Equal("Automatic", AppUnderTest.ComboNamed(page, "Buffer size").GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible).Text);
        Assert.True(File.Exists(app.SaveFrame($"settings-audio-format-{(int)width}")));
        audioSettings.StopCommand.Execute(null);
    });

    // A WASAPI device mixed at 48 kHz, opened at 44.1 kHz, whose callback has come late twice.
    private sealed class DroppingBackend : IAudioBackend
    {
        public IReadOnlyList<AudioDeviceInfo> ListDevices() =>
            [new("wasapi:speakers", "Speakers", "WASAPI", true, 48_000, 2, [44_100, 48_000], [2], 64, 4096)];

        public IAudioOutput Open(IAudioRenderer renderer, AudioStreamRequest request) => new Output(request.SampleRate);

        private sealed class Output(int rate) : IAudioOutput
        {
            public int SampleRate => rate;

            public int Channels => 2;

            public int QueuedFrames => 0;

            public string? RenderThreadPolicy => null;

            public AudioStreamStatus Status => new(AudioStreamState.Running, rate, 2, 0, 441, 441, 441, 1000, 441_000, 0, 2, 0, TimeSpan.Zero, AudioStreamError.None, 0, TimeSpan.Zero, TimeSpan.Zero, 0, 0, 0, 0)
            {
                DeviceBufferFrames = 441,
                CallbackThreadPolicy = AudioCallbackThreadPolicy.MmcssProAudio,
            };

            public void SetQueuedFrames(int frames)
            {
            }

            public void Start()
            {
            }

            public void Dispose()
            {
            }
        }
    }

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
        Assert.Equal(["Audio", "Plugins", "Display"], settings.Categories.Select(c => c.Name));

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
        Assert.Equal(3, settings.Categories.Count);

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
    public Task ChangingTheAudioDevice_KeepsTheRateAndBufferChoicesSelected_WithoutBindingErrors() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        using var audio = new AudioPlayback(app.Playback, app.Session, _ => null);
        AudioDeviceInfo[] devices =
        [
            new("wasapi:speakers", "Speakers", "WASAPI", true, 48_000, 2, [44_100, 48_000, 96_000], [2], 80, 3840),
            new("wasapi:monitor", "Monitor", "WASAPI", false, 48_000, 2, [44_100, 48_000, 96_000], [2], 80, 3840),
            new("wasapi:headset", "Headset", "WASAPI", false, 48_000, 2, [48_000], [2], 512, 1024),
        ];
        var audioSettings = new AudioViewModel(audio, new AudioSettingsStore(Path.Combine(_directory, "audio-settings.json")), new ImmediateDispatcher(), () => devices, listHosts: () => ["WASAPI"]);
        app.Window.AttachAudio(audioSettings);
        app.Window.ShowSettings(SettingsPage.Audio);
        await app.SettleTransitionsAsync();
        var page = app.Window.GetVisualDescendants().OfType<AudioSettingsPage>().Single(p => p.IsEffectivelyVisible);
        var device = AppUnderTest.ComboNamed(page, "Audio device");
        var rate = AppUnderTest.ComboNamed(page, "Sample rate");
        var buffer = AppUnderTest.ComboNamed(page, "Buffer size");
        app.Choose(rate, r => r is 44_100);
        app.Choose(buffer, b => b is 128);

        void AssertSelected(int expectedRate, int expectedBuffer)
        {
            Assert.Equal(expectedRate, rate.SelectedItem);
            Assert.Equal(expectedBuffer, buffer.SelectedItem);
            Assert.Equal((expectedRate, expectedBuffer), (audioSettings.SampleRate, audioSettings.BufferFrames));
            Assert.False(DataValidationErrors.GetHasErrors(rate), $"Sample rate shows {string.Join(", ", DataValidationErrors.GetErrors(rate) ?? [])}");
            Assert.False(DataValidationErrors.GetHasErrors(buffer), $"Buffer size shows {string.Join(", ", DataValidationErrors.GetErrors(buffer) ?? [])}");
        }

        // The same capabilities: the choices are kept.
        app.Choose(device, d => d is AudioDeviceOption { Id: "wasapi:monitor" });
        AssertSelected(44_100, 128);

        // Narrower capabilities: the rate follows the device (Automatic) and the nearest buffer is chosen.
        app.Choose(device, d => d is AudioDeviceOption { Id: "wasapi:headset" });
        AssertSelected(AudioSettings.Automatic, 512);

        app.Choose(device, d => d is AudioDeviceOption { Id: "wasapi:speakers" });
        AssertSelected(AudioSettings.Automatic, 512);

        app.Click(AppUnderTest.Within<Button>(page, b => Avalonia.Automation.AutomationProperties.GetName(b) == "Refresh audio devices"));
        AssertSelected(AudioSettings.Automatic, 512);

        audioSettings.SampleRate = 96_000;
        audioSettings.BufferFrames = 256;
        app.Settle();
        AssertSelected(96_000, 256);
        Assert.False(audioSettings.IsRunning);
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
    public Task TheControlBarsUndo_BringsTheWorkspaceBack_AndTheMenuBarCanOpenAnotherSettingsPage() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        using var audio = new AudioPlayback(app.Playback, app.Session, _ => null);
        app.Window.AttachAudio(Audio(app, audio));
        app.ViewModel.AddTrackCommand.Execute(null);
        app.Window.ShowSettings(SettingsPage.Plugins);
        app.Settle();

        app.ChooseFromMenu("Window", "Audio Settings…");
        Assert.True(app.Window.Settings.IsOpen);
        Assert.Equal(SettingsPage.Audio, app.Window.Settings.SelectedCategory?.Page);

        app.Click(AppUnderTest.Within<Button>(app.Window, b => Avalonia.Automation.AutomationProperties.GetName(b) == "Undo"));

        Assert.False(app.Window.Settings.IsOpen);
        Assert.Empty(app.ViewModel.Tracks);
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
