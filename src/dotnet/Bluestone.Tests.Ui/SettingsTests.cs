using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Bluestone.Application.Audio;
using Bluestone.Audio.Devices;
using Bluestone.Desktop.Controls;
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
    public Task TheDisplayPage_AddsMovesAndRemovesGradientStops_AndSavesEachEdit() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        app.Window.Width = 1440;
        app.Window.Height = 900;
        var store = new DisplaySettingsStore(Path.Combine(_directory, "display-settings.json"));
        var display = app.ViewModel.Display;
        display.Use(store);
        Desktop.App.FollowSignalGradient(display);
        try
        {
            app.Window.ShowSettings(SettingsPage.Display);
            app.Settle();
            var page = app.Window.GetVisualDescendants().OfType<DisplaySettingsPage>().Single(p => p.IsEffectivelyVisible);
            var editor = AppUnderTest.Within<SignalGradientEditor>(page, _ => true);
            TextKey ActionKey(string name) => AppUnderTest.Within<TextKey>(page, k => Avalonia.Automation.AutomationProperties.GetName(k) == name);
            string[] Names() => [.. editor.Handles.Select(h => Avalonia.Automation.AutomationProperties.GetName(h) ?? string.Empty)];
            Assert.Equal(["Gradient stop 1 at 0%", "Gradient stop 2 at 40%", "Gradient stop 3 at 62%", "Gradient stop 4 at 78%", "Gradient stop 5 at 86%", "Gradient stop 6 at 100%"], Names());
            Assert.Same(display.Mixer, editor.DataContext);

            // Add: a stop in the widest gap (0% to 40%), selected, saved, and on every meter.
            app.Click(ActionKey("Add gradient stop"));
            Assert.False(ActionKey("Add gradient stop").IsChecked, "An action key springs back.");
            Assert.Equal(7, store.Load().MixerGradient.Stops.Length);
            Assert.Equal("Gradient stop 2 at 20%", Names()[1]);
            Assert.Equal("20%", AppUnderTest.Within<TextBlock>(page, t => t.Name == "StopPositionText").Text);
            Assert.Equal(7, Desktop.Theme.MeterGradient.Mixer.Stops.Count);

            // Move: drag the new stop to 30%, then nudge it with the keyboard (1%, and 5% with Shift).
            var handle = editor.Handles[1];
            var from = new Point(handle.Bounds.Center.X, handle.Bounds.Center.Y);
            app.Drag(editor, from, new Point(editor.XAt(0.3), from.Y));
            Assert.Equal(0.3, store.Load().MixerGradient.Stops[1].Position, 2);
            Assert.True(editor.Handles[1].IsFocused);
            app.Press(Key.Right);
            Assert.Equal(0.31, store.Load().MixerGradient.Stops[1].Position, 3);
            app.Press(Key.Right, RawInputModifiers.Shift);
            Assert.Equal(0.36, store.Load().MixerGradient.Stops[1].Position, 3);
            Assert.Equal("Gradient stop 2 at 36%", Names()[1]);
            Assert.Equal(0.36, Desktop.Theme.MeterGradient.Mixer.Stops[1].Position, 3);

            // A swatch recolours the selected stop.
            app.Click(AppUnderTest.Within<Button>(page, b => Avalonia.Automation.AutomationProperties.GetName(b) == "Color #4CB873"));
            Assert.Equal(new SignalGradientStop("#4CB873", 0.36), store.Load().MixerGradient.Stops[1]);
            app.Window.MouseMove(new Point(1300, 800), RawInputModifiers.None);
            await app.SettleTransitionsAsync();
            var frame = app.SaveFrame("settings-display-gradient-editor");
            Assert.True(File.Exists(frame));
            var copyTo = Environment.GetEnvironmentVariable("BLUESTONE_GRADIENT_EDITOR_PNG");
            if (!string.IsNullOrEmpty(copyTo))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copyTo)!);
                File.Copy(frame, copyTo, overwrite: true);
            }

            // Remove: the selected stop goes; at two stops the key is disabled.
            app.Click(ActionKey("Remove gradient stop"));
            Assert.Equal(6, store.Load().MixerGradient.Stops.Length);
            Assert.DoesNotContain(store.Load().MixerGradient.Stops, s => s.Colour == "#4CB873");
            while (display.Mixer.CanRemoveGradientStop)
            {
                app.Click(ActionKey("Remove gradient stop"));
            }

            Assert.Equal(2, store.Load().MixerGradient.Stops.Length);
            Assert.False(ActionKey("Remove gradient stop").IsEffectivelyEnabled);
            Assert.Equal(2, editor.Handles.Count);

            app.Click(ActionKey("Reset gradient to default"));
            Assert.Equal(SignalGradient.Default, store.Load().MixerGradient);
            Assert.Equal(6, editor.Handles.Count);
        }
        finally
        {
            display.Mixer.ResetGradientCommand.Execute(null);
        }
    });

    [Theory]
    [InlineData(1040)]
    [InlineData(1440)]
    public Task SettingsHeaders_AreOneHeight_WithOneBottomLine_AndNoDescriptionLine(double width) => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        app.Window.Width = width;
        foreach (var page in new[] { SettingsPage.Display, SettingsPage.Audio })
        {
            app.Window.ShowSettings(page);
            app.Settle();
            var header = app.Find<Border>("SettingsHeader");
            var nav = app.Find<Border>("NavHeader");
            Assert.Equal(nav.Bounds.Height, header.Bounds.Height);
            Assert.Equal(30, header.Bounds.Height);
            Assert.Equal(nav.TranslatePoint(new Point(0, nav.Bounds.Height), app.Window)!.Value.Y, header.TranslatePoint(new Point(0, header.Bounds.Height), app.Window)!.Value.Y);
            Assert.DoesNotContain(header.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == app.Window.Settings.SelectedCategory?.Description);
            Assert.Contains(header.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Content as string == "Done");
        }
    });

    [Fact]
    public Task TheDisplayStage_EditsOnlyTheChosenIndicator_AndItsLedMotionSwitchesAreReachable() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        app.Window.Width = 1440;
        app.Window.Height = 900;
        var store = new DisplaySettingsStore(Path.Combine(_directory, "display-settings.json"));
        var display = app.ViewModel.Display;
        display.Use(store);
        Desktop.App.FollowSignalGradient(display);
        try
        {
            app.Window.ShowSettings(SettingsPage.Display);
            app.Settle();
            var page = app.Window.GetVisualDescendants().OfType<DisplaySettingsPage>().Single(p => p.IsEffectivelyVisible);
            var editor = AppUnderTest.Within<SignalGradientEditor>(page, _ => true);
            var midi = AppUnderTest.Within<Control>(page, c => c.Name == "MidiPanel");
            Button Swatch(string hex) => AppUnderTest.Within<Button>(page, b => Avalonia.Automation.AutomationProperties.GetName(b) == $"Color {hex}");
            Assert.False(midi.IsEffectivelyVisible, "The MIDI light belongs to the device chain only.");

            // A click on the gain tile edits the gain LEDs alone: the mixer and chain keep their colors, on screen and saved.
            app.Click(ColourPickerTests.StageTile(page, "Gain LEDs"));
            Assert.Same(display.Gain, editor.DataContext);
            Assert.False(midi.IsEffectivelyVisible);
            display.Gain.SelectedGradientStopIndex = 0;
            app.Click(Swatch("#B65CFF"));
            var saved = store.Load();
            Assert.Equal("#B65CFF", saved.GainGradient.Stops[0].Colour);
            Assert.Equal((SignalGradient.Default, SignalGradient.Default), (saved.MixerGradient, saved.ChainGradient));
            Assert.Equal(Avalonia.Media.Color.Parse("#B65CFF"), Desktop.Theme.MeterGradient.Gain.Stops[0].Color);
            Assert.NotEqual(Avalonia.Media.Color.Parse("#B65CFF"), Desktop.Theme.MeterGradient.Mixer.Stops[0].Color);

            // The segmented fallback reaches the chain from the keyboard's side: its own stops, and the MIDI light row.
            app.Click(AppUnderTest.Within<RadioButton>(page, r => Avalonia.Automation.AutomationProperties.GetName(r) == "Edit device chain colors"));
            Assert.Same(display.Chain, editor.DataContext);
            Assert.True(ColourPickerTests.StageTile(page, "Device chain").IsSelected);
            Assert.Equal("Device chain", ControlAutomationPeer.CreatePeerForElement(ColourPickerTests.StageTile(page, "Device chain")).GetName());
            Assert.True(midi.IsEffectivelyVisible);
            display.Chain.SelectedGradientStopIndex = 1;
            app.Click(Swatch("#E0A43A"));
            saved = store.Load();
            Assert.Equal("#E0A43A", saved.ChainGradient.Stops[1].Colour);
            Assert.Equal(SignalGradient.Default, saved.MixerGradient);
            Assert.Equal("#B65CFF", saved.GainGradient.Stops[0].Colour);

            // Arrow keys move between the tiles, as the radios follow.
            ColourPickerTests.StageTile(page, "Device chain").Focus(NavigationMethod.Tab);
            app.Press(Key.Left);
            Assert.Same(display.Mixer, editor.DataContext);
            Assert.True(AppUnderTest.Within<RadioButton>(page, r => Avalonia.Automation.AutomationProperties.GetName(r) == "Edit mixer meter colors").IsChecked);

            // LED fade and warm-up: each a switch on the page that turns its setting off and saves it.
            var fade = AppUnderTest.Within<TextKey>(page, k => k.Name == "LedFadeKey");
            var warmup = AppUnderTest.Within<TextKey>(page, k => k.Name == "LedWarmupKey");
            Assert.Equal(("LED fade", "LED warm-up"), (Avalonia.Automation.AutomationProperties.GetName(fade), Avalonia.Automation.AutomationProperties.GetName(warmup)));
            Assert.True(fade.IsChecked == true && warmup.IsChecked == true);
            app.Click(fade);
            Assert.False(display.LedFade);
            Assert.False(store.Load().LedFade);
            Assert.True(store.Load().LedWarmup);
            app.Click(warmup);
            Assert.False(store.Load().LedWarmup);
            app.Click(fade);
            app.Click(warmup);
            Assert.True(store.Load() is { LedFade: true, LedWarmup: true });
        }
        finally
        {
            foreach (var each in display.Editors)
            {
                each.ResetGradientCommand.Execute(null);
            }

            display.LedFade = true;
            display.LedWarmup = true;
        }
    });

    [Fact]
    public Task TheGradientEditor_SavesADragOnceOnRelease_ClampsNudges_AndAnnouncesSlidersAndButtons() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        app.Window.Width = 1440;
        app.Window.Height = 900;
        var path = Path.Combine(_directory, "display-settings.json");
        var store = new DisplaySettingsStore(path);
        var display = app.ViewModel.Display;
        display.Use(store);
        Desktop.App.FollowSignalGradient(display);
        try
        {
            app.Window.ShowSettings(SettingsPage.Display);
            app.Settle();
            var page = app.Window.GetVisualDescendants().OfType<DisplaySettingsPage>().Single(p => p.IsEffectivelyVisible);
            var editor = AppUnderTest.Within<SignalGradientEditor>(page, _ => true);

            // A drag previews on the meters as it goes, but writes the file only once, on release.
            store.Save(store.Load());
            var saved = File.ReadAllText(path);
            var handle = editor.Handles[1];
            var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), app.Window)!.Value;
            app.Window.MouseMove(start, RawInputModifiers.None);
            app.Window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
            for (var position = 0.35; position > 0.24; position -= 0.05)
            {
                var at = new Point(editor.TranslatePoint(new Point(editor.XAt(position), 0), app.Window)!.Value.X, start.Y);
                app.Window.MouseMove(at, RawInputModifiers.LeftMouseButton);
                app.Settle();
                Assert.Equal(position, display.Mixer.Gradient.Stops[1].Position, 2);
                Assert.Equal(position, Desktop.Theme.MeterGradient.Mixer.Stops[1].Position, 2);
                Assert.Equal(saved, File.ReadAllText(path));
            }

            app.Window.MouseUp(new Point(editor.TranslatePoint(new Point(editor.XAt(0.25), 0), app.Window)!.Value.X, start.Y), MouseButton.Left, RawInputModifiers.None);
            app.Settle();
            Assert.Equal(0.25, store.Load().MixerGradient.Stops[1].Position, 2);

            // Nudges stop at the ends of the scale, by 1% or by 5% with Shift.
            editor.Handles[0].Focus();
            app.Press(Key.Left);
            app.Press(Key.Left, RawInputModifiers.Shift);
            Assert.Equal(0, store.Load().MixerGradient.Stops[0].Position);
            Assert.Equal("Gradient stop 1 at 0%", Avalonia.Automation.AutomationProperties.GetName(editor.Handles[0]));
            var last = editor.Handles.Count - 1;
            editor.Handles[last].Focus();
            app.Press(Key.Right);
            app.Press(Key.Right, RawInputModifiers.Shift);
            Assert.Equal(1, store.Load().MixerGradient.Stops[last].Position);
            Assert.Equal($"Gradient stop {last + 1} at 100%", Avalonia.Automation.AutomationProperties.GetName(editor.Handles[last]));

            // A stop is a slider over 0 to 100 described by its colour; setting it moves the stop.
            var stopPeer = ControlAutomationPeer.CreatePeerForElement(editor.Handles[2]);
            Assert.Equal(AutomationControlType.Slider, stopPeer.GetAutomationControlType());
            Assert.StartsWith(display.Mixer.Gradient.Stops[2].Colour + ". Left or Right to move", stopPeer.GetHelpText(), StringComparison.Ordinal);
            var range = Assert.IsAssignableFrom<IRangeValueProvider>(stopPeer);
            Assert.Equal((0, 100, 62), (range.Minimum, range.Maximum, range.Value));
            range.SetValue(70);
            Assert.Equal(0.7, store.Load().MixerGradient.Stops[2].Position, 3);

            // The gradient's actions are buttons, not toggles, and stay unchecked.
            var add = AppUnderTest.Within<TextKey>(page, k => Avalonia.Automation.AutomationProperties.GetName(k) == "Add gradient stop");
            var addPeer = ControlAutomationPeer.CreatePeerForElement(add);
            Assert.Equal(AutomationControlType.Button, addPeer.GetAutomationControlType());
            Assert.IsAssignableFrom<IInvokeProvider>(addPeer);
            Assert.False(addPeer is IToggleProvider, "An action key must not offer a toggle.");
            app.Click(add);
            Assert.False(add.IsChecked == true, "An action key never stays checked.");
            Assert.Equal(7, store.Load().MixerGradient.Stops.Length);
        }
        finally
        {
            display.Mixer.ResetGradientCommand.Execute(null);
        }
    });

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
