using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Bluestone.Application.Editing;
using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Desktop.Views;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Playback;
using Bluestone.Presentation;
using ToggleButton = Avalonia.Controls.Primitives.ToggleButton;
using TrackModel = Bluestone.Domain.Sequencing.Track;

namespace Bluestone.Tests.Ui;

/// <summary>The track-owned mixer in its docked workspace tab and its shared detached window.</summary>
public sealed class MixerConsoleTests
{
    private static async Task<(TrackModel Audio, TrackModel Midi, TrackModel Effect)> SeedAsync(AppUnderTest app)
    {
        var audio = TrackModel.Create("Vocal", TrackRole.Audio);
        var midi = TrackModel.Create("Keys", TrackRole.Instrument);
        var effect = TrackModel.Create("Reverb", TrackRole.Effect);
        app.Session.Execute(new ProjectCommand("Set up mixer tracks", project => project with
        {
            Sequence = new Sequence(project.Sequence.TempoMap, project.Sequence.MeterMap, [audio, midi, effect], project.Sequence.Markers),
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(audio.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(effect.Id), SignalNode.Master),
            ],
        }));
        await AppUnderTest.WaitUntilAsync(() => app.ViewModel.MixerConsole.Tracks.Count == 3 && app.ViewModel.Tracks.Count == 3, "the mixer to show the seeded tracks");
        app.Settle();
        return (audio, midi, effect);
    }

    /// <summary>Puts built-in devices on <paramref name="track"/>'s chain, as the Device view would, in one undo step each.</summary>
    private static async Task<DeviceInstance[]> AddDevicesAsync(AppUnderTest app, TrackId track, params DeviceDefinition[] definitions)
    {
        var added = definitions.Select(definition => DeviceInstance.Create(definition.ToReference())).ToArray();
        foreach (var device in added)
        {
            Assert.True(app.Session.Execute(DeviceCommands.InsertDevice(track, device, BuiltInDevices.Find)), $"{device.DisplayName} should be accepted.");
        }

        await AppUnderTest.WaitUntilAsync(() => app.ViewModel.MixerConsole.Tracks.Single(row => row.Id == track).Inserts.Count == added.Length,
            "the strip to list its inserts");
        app.Settle();
        return added;
    }

    /// <summary>The strip whose accessible summary names <paramref name="track"/>.</summary>
    private static Border StripOf(Control mixer, string track) =>
        Assert.Single(mixer.GetLogicalDescendants().OfType<Border>(), border =>
            AutomationProperties.GetName(border)?.StartsWith($"{track},", StringComparison.Ordinal) == true);

    /// <summary>The one visible control of type <typeparamref name="T"/> with the accessible name <paramref name="name"/>.</summary>
    private static T Visible<T>(Control scope, string name)
        where T : Control =>
        Assert.Single(scope.GetLogicalDescendants().OfType<T>(), control =>
            control.IsEffectivelyVisible && AutomationProperties.GetName(control) == name);

    private static IEnumerable<T> AllVisible<T>(Control scope, string name)
        where T : Control =>
        scope.GetLogicalDescendants().OfType<T>().Where(control =>
            control.IsEffectivelyVisible && AutomationProperties.GetName(control) == name);

    /// <summary>Clicks a flyout button and returns its open flyout's content, which lives in a popup of its own.</summary>
    private static Control OpenFlyout(AppUnderTest app, Button button)
    {
        app.Click(button);
        var flyout = Assert.IsType<Flyout>(button.Flyout);
        Assert.True(flyout.IsOpen, $"{AutomationProperties.GetName(button)} should open its flyout.");
        var content = Assert.IsAssignableFrom<Control>(flyout.Content);
        content.UpdateLayout();
        app.Settle();
        return content;
    }

    private static bool IsBypassed(AppUnderTest app, DeviceId device) => app.Session.Project.FindDevice(device)!.Value.Device.IsBypassed;

    private static double GainOf(AppUnderTest app, TrackId track) => app.Session.Project.Sequence.FindTrack(track)!.AudioStrip!.GainDecibels;

    private static double PanOf(AppUnderTest app, TrackId track) => app.Session.Project.Sequence.FindTrack(track)!.AudioStrip!.Pan;

    private static async Task SetStripAsync(AppUnderTest app, TrackId track, double gain, double pan)
    {
        // Set elsewhere (not with the mixer's merge keys), so the mixer gesture under test starts its own undo step.
        app.Session.Execute(AudioStripCommands.SetTrackGain(track, gain, "test-setup:gain"));
        app.Session.Execute(AudioStripCommands.SetTrackPan(track, pan, "test-setup:pan"));
        await AppUnderTest.WaitUntilAsync(() =>
            app.ViewModel.MixerConsole.Tracks.Single(row => row.Id == track) is { } row && row.Gain == gain && row.Pan == pan, "the strip to follow");
        app.Settle();
    }

    private static MixerConsoleView ShowDockedMixer(AppUnderTest app)
    {
        app.ViewModel.Workspace.Mixer.ShowCommand.Execute(null);
        app.Settle();
        return AppUnderTest.Within<MixerConsoleView>(app.Window, _ => true);
    }

    /// <summary>
    /// The docked mixer with the detail pane dragged taller, as a musician does to see the insert stack and sends: at the
    /// default detail height the pane is shorter than <c>Mixer.FullStripHeight</c> and that band folds away.
    /// </summary>
    private static MixerConsoleView ShowTallDockedMixer(AppUnderTest app)
    {
        app.ViewModel.Workspace.DetailExtent = 640;
        var mixer = ShowDockedMixer(app);
        app.Settle();
        Assert.DoesNotContain("short", mixer.Classes);
        return mixer;
    }

    [Fact]
    public Task DockedMixer_ShowsAudioControlsOnlyForAudioTracks_AndKeepsMasterVisible() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, midi, effect) = await SeedAsync(app);
        var mixer = ShowDockedMixer(app);

        Assert.Equal(LowerPane.Mixer, app.ViewModel.LowerPane);
        Assert.Same(app.ViewModel.MixerConsole, app.ViewModel.Workspace.Mixer.MixerConsole);
        Assert.Equal([audio.Id, midi.Id, app.Session.Project.Sequence.Tracks[2].Id], app.ViewModel.MixerConsole.Tracks.Select(row => row.Id));
        Assert.True(app.ViewModel.MixerConsole.Tracks.Single(row => row.Id == audio.Id).HasAudioStrip);
        Assert.False(app.ViewModel.MixerConsole.Tracks.Single(row => row.Id == midi.Id).HasAudioStrip);
        Assert.True(app.ViewModel.MixerConsole.Tracks.Single(row => row.Id == effect.Id).HasAudioStrip);

        Assert.Equal(2, mixer.GetLogicalDescendants().OfType<ConsoleFader>().Count(bar => bar.IsEffectivelyVisible && AutomationProperties.GetName(bar) != "Master gain"));
        foreach (var track in new[] { audio, effect })
        {
            var strip = StripOf(mixer, track.Name);
            Visible<ConsoleFader>(strip, $"{track.Name} gain");
            Visible<PanKnob>(strip, $"{track.Name} pan");
            Visible<LedLadderMeter>(strip, "Track level meter");
            Visible<ToggleButton>(strip, $"Mute {track.Name}");
            Visible<ToggleButton>(strip, $"Solo {track.Name}");
        }

        var midiStrip = StripOf(mixer, midi.Name);
        Assert.DoesNotContain(midiStrip.GetLogicalDescendants().OfType<ConsoleFader>(), bar => bar.IsEffectivelyVisible);
        Assert.DoesNotContain(midiStrip.GetLogicalDescendants().OfType<PanKnob>(), pan => pan.IsEffectivelyVisible);
        Assert.DoesNotContain(midiStrip.GetLogicalDescendants().OfType<LedLadderMeter>(), rail => rail.IsEffectivelyVisible);
        Assert.Equal(2, mixer.GetLogicalDescendants().OfType<PanKnob>().Count(pan => pan.IsEffectivelyVisible && AutomationProperties.GetName(pan) != "Master pan"));
        Visible<ConsoleFader>(mixer, "Master gain");
        Visible<PanKnob>(mixer, "Master pan");
        Visible<LedLadderMeter>(mixer, "Master level meter");
        Assert.Empty(AllVisible<ToggleButton>(midiStrip, $"Mute {midi.Name}"));
        Assert.Empty(AllVisible<ToggleButton>(midiStrip, $"Solo {midi.Name}"));
        Visible<ToggleButton>(mixer, "Mute Master");
        Visible<ToggleButton>(mixer, "Solo Master");
        Assert.Contains(mixer.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "No audio fader, pan, or meter" && text.IsEffectivelyVisible);
        Assert.Contains(mixer.GetLogicalDescendants().OfType<Border>(), border =>
            AutomationProperties.GetName(border)?.Contains("Keys, Instrument, MIDI only", StringComparison.Ordinal) == true);
    });

    [Fact]
    public Task SelectingMaster_ShowsItsInspectorScopeWithoutSelectingAnArrangementTrack() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var mixer = ShowDockedMixer(app);
        Assert.Empty(app.ViewModel.SelectedTracks);

        app.Click(AppUnderTest.Within<Button>(mixer, button =>
            AutomationProperties.GetName(button) == "Select Master"));
        app.Settle();

        Assert.Empty(app.ViewModel.SelectedTracks);
        Assert.True(app.ViewModel.MixerConsole.Master.IsSelected);
        Assert.True(app.ViewModel.Selection.IsMasterSelected);
        Visible<Button>(mixer, "Master, selected");
        Assert.Empty(AllVisible<Button>(mixer, "Select Master"));

        var inspector = AppUnderTest.Within<InspectorView>(app.Window, _ => true);
        var masterScope = AppUnderTest.Within<StackPanel>(inspector, panel => panel.Name == "MasterInspectorPanel");
        Assert.True(masterScope.IsEffectivelyVisible);
        Assert.Contains(masterScope.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "MASTER OUTPUT");
        Assert.Single(masterScope.GetLogicalDescendants().OfType<Slider>(), slider =>
            slider.IsEffectivelyVisible && AutomationProperties.GetName(slider) == "Master audio gain");
    });

    [Fact]
    public Task TrackStripSelection_AfterMasterScope_StillSelectsTheTrackInArrangerAndInspector() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, midi, _) = await SeedAsync(app);
        var mixer = ShowDockedMixer(app);
        Assert.Single(app.ViewModel.SelectedTracks);

        app.Click(AppUnderTest.Within<Button>(mixer, button =>
            AutomationProperties.GetName(button) == "Select Master"));
        app.Settle();
        Assert.Empty(app.ViewModel.SelectedTracks);
        Assert.True(app.ViewModel.Selection.ShowsMasterInspector);

        var midiTrack = app.ViewModel.Tracks.Single(track => track.Id == midi.Id);
        var selectMidi = AppUnderTest.Within<Button>(mixer, button =>
            button.Content is TextBlock text && text.Text == midiTrack.Name);
        app.Click(selectMidi);
        app.Settle();

        Assert.Equal(midi.Id, Assert.Single(app.ViewModel.SelectedTracks).Id);
        Assert.False(app.ViewModel.MixerConsole.Master.IsSelected);
        Assert.False(app.ViewModel.Selection.IsMasterSelected);
        Assert.True(app.ViewModel.Selection.ShowsTrackSelectionInspector);
        Assert.True(app.ViewModel.MixerConsole.Tracks.Single(row => row.Id == midi.Id).IsSelected);
        Assert.False(app.ViewModel.MixerConsole.Tracks.Single(row => row.Id == audio.Id).IsSelected);
    });

    [Fact]
    public Task MeterRails_HoldThePeakMarkerAsTheLevelFallsToTheFloor() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, _) = await SeedAsync(app);
        var mixer = ShowDockedMixer(app);
        var reading = new MeterReading(0.9f, 0.8f, 0.4f, 0.3f);
        app.ViewModel.MixerConsole.MeterSource = _ => reading;
        var timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        app.ViewModel.MixerConsole.RefreshMeters(timestamp);
        reading = new MeterReading(0.35f, 0.25f, 0.2f, 0.15f);
        app.ViewModel.MixerConsole.RefreshMeters(timestamp + 1);
        app.Settle();

        var expectedHold = Math.Max(MeterReading.ToDecibels(0.9f), MeterReading.ToDecibels(0.8f));
        var faces = mixer.GetLogicalDescendants().OfType<LedLadderMeter>().ToArray();
        var targetFaces = faces.Where(face => face.IsEffectivelyVisible).ToArray();
        Assert.True(targetFaces.Length >= 2, "The visible track and Master meter rails should both be present.");
        Assert.All(targetFaces, face => Assert.Equal(expectedHold, face.PeakHold, 5));

        app.Window.Width = 1100;
        app.Window.Height = 720;
        app.Settle();
        Assert.True(File.Exists(app.SaveFrame("mixer-meter-peak-hold-compact")));
        AssertPeakHoldMarkersAreRendered(app, faces, minimumVisibleMarkers: 2);

        reading = new MeterReading(0.001f, 0.001f, 0.001f, 0.001f);
        app.ViewModel.MixerConsole.RefreshMeters(timestamp + 2);
        app.Settle();
        Assert.All(faces.Where(face => face.IsEffectivelyVisible), face =>
        {
            Assert.True(face.Value <= MixerRail.GainFloor + 0.001);
            Assert.True(face.PeakHold > MixerRail.GainFloor);
        });
        Assert.True(File.Exists(app.SaveFrame("mixer-meter-peak-hold-at-floor-compact")));
        AssertPeakHoldMarkersAreRendered(app, faces, minimumVisibleMarkers: 2);

        app.Window.Width = 1440;
        app.Window.Height = 900;
        app.Settle();
        Assert.True(File.Exists(app.SaveFrame("mixer-meter-peak-hold-normal")));
        AssertPeakHoldMarkersAreRendered(app, faces, minimumVisibleMarkers: 3);
    });

    [Fact]
    public Task TrackAudioFader_MovedSeveralTimes_IsOneUndoStepAndShowsItsValue() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, _) = await SeedAsync(app);
        var mixer = ShowDockedMixer(app);
        var strip = StripOf(mixer, audio.Name);
        var fader = Visible<ConsoleFader>(strip, $"{audio.Name} gain");
        var readout = Visible<TextBlock>(strip, "Track gain in decibels");
        var row = app.ViewModel.MixerConsole.Tracks.Single(candidate => candidate.Id == audio.Id);
        var steps = app.Session.History.UndoCount;

        // A musician clicks the gain bar (which focuses it without moving it), then nudges it from the keyboard.
        app.Click(fader);
        Assert.True(fader.IsFocused, "Clicking the gain bar should focus it for the keyboard.");
        Assert.Equal(0, GainOf(app, audio.Id));
        foreach (var (key, expected) in new[] { (Key.PageDown, -6.0), (Key.PageDown, -12.0), (Key.PageUp, -6.0), (Key.Left, -7.0), (Key.Right, -6.0) })
        {
            app.Press(key);
            Assert.Equal(expected, GainOf(app, audio.Id));
        }

        Assert.Equal(-6, GainOf(app, audio.Id));
        Assert.Equal(-6, fader.Value);
        Assert.Equal("-6.0 dB", row.GainText);
        Assert.Equal("-6.0", readout.Text);
        Assert.Equal(steps + 1, app.Session.History.UndoCount);
        app.SaveFrame("track-owned-mixer-fader");

        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();

        Assert.Equal(0, GainOf(app, audio.Id));
        Assert.Equal(0, fader.Value);
        Assert.Equal("0.0", readout.Text);
    });

    [Fact]
    public Task AudioSendAddedFromTrackStrip_IsRoutedToEffectAndUndoable() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, effect) = await SeedAsync(app);
        var mixer = ShowTallDockedMixer(app);
        var source = app.ViewModel.MixerConsole.Tracks.Single(row => row.Id == audio.Id);
        var strip = StripOf(mixer, audio.Name);
        Assert.Contains(strip.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "+ SEND" && text.IsEffectivelyVisible);
        // An empty strip shows no sends line until it is hovered or focused.
        Assert.Equal(0, strip.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Text == "+ SEND").Opacity);

        var sends = OpenFlyout(app, Visible<Button>(strip, "No sends, add a send"));
        app.Choose(AppUnderTest.ComboNamed(sends, "New send destination"), item =>
            item is MixerEffectOption option && option.Track == effect.Id);
        app.Click(AppUnderTest.Within<Button>(sends, button => Equals(button.Content, "Add")));

        var send = Assert.Single(app.Session.Project.Connections, connection => connection.AudioPurpose == AudioRoutePurpose.Send);
        Assert.Equal(SignalNode.Track(effect.Id), send.Destination);
        Assert.Single(source.Sends);
        Assert.Single(AllVisible<Knob>(sends, $"Send to {effect.Name} level"));
        Assert.Single(AllVisible<Button>(strip, "1 send, show all or add"));

        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.DoesNotContain(app.Session.Project.Connections, connection => connection.AudioPurpose == AudioRoutePurpose.Send);
        Assert.Empty(source.Sends);
        Assert.Contains(strip.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "+ SEND" && text.IsEffectivelyVisible);
    });

    [Fact]
    public Task GainBar_KeyboardStepsAndPages_ChangeGainAsOneUndoStep() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, _) = await SeedAsync(app);
        await SetStripAsync(app, audio.Id, -10, 0);
        var mixer = ShowDockedMixer(app);
        var strip = StripOf(mixer, audio.Name);
        var bar = Visible<ConsoleFader>(strip, $"{audio.Name} gain");
        var readout = Visible<TextBlock>(strip, "Track gain in decibels");
        Assert.Equal(-10, bar.Value);
        var steps = app.Session.History.UndoCount;

        bar.Focus(NavigationMethod.Tab);
        app.Settle();
        app.Press(Key.Right);
        Assert.Equal(-9, GainOf(app, audio.Id));
        app.Press(Key.PageUp);
        Assert.Equal(-3, GainOf(app, audio.Id));
        app.Press(Key.Left, RawInputModifiers.Shift);
        Assert.Equal(-3.1, GainOf(app, audio.Id), 6);
        Assert.Equal("-3.1", readout.Text);
        app.Press(Key.PageDown);
        Assert.Equal(-9.1, GainOf(app, audio.Id), 6);
        Assert.Equal("-9.1", readout.Text);
        Assert.Equal(steps + 1, app.Session.History.UndoCount);

        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.Equal(-10, GainOf(app, audio.Id));
        Assert.Equal(-10, bar.Value);
        Assert.Equal("-10.0", readout.Text);
    });

    [Fact]
    public Task GainBar_ValueKeysReachTheFocusedBar_NotTheWindowShortcuts() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, _) = await SeedAsync(app);
        await SetStripAsync(app, audio.Id, -10, 0);
        var mixer = ShowDockedMixer(app);
        var strip = StripOf(mixer, audio.Name);
        var bar = Visible<ConsoleFader>(strip, $"{audio.Name} gain");
        var tracks = app.Session.Project.Sequence.Tracks.Length;
        var selected = app.ViewModel.SelectedTrack;
        app.ViewModel.NextBarCommand.Execute(null);
        var playhead = app.ViewModel.PlayheadTick;
        Assert.NotEqual(0, playhead);

        bar.Focus(NavigationMethod.Tab);
        app.Settle();
        app.Press(Key.Up);
        Assert.Equal(-9, GainOf(app, audio.Id));
        app.Press(Key.Down);
        app.Press(Key.Down);
        Assert.Equal(-11, GainOf(app, audio.Id));
        Assert.Same(selected, app.ViewModel.SelectedTrack);

        // Delete and Backspace reset the bar to unity gain; they do not delete the track.
        app.Press(Key.Delete);
        Assert.Equal(0, GainOf(app, audio.Id));
        app.Press(Key.PageDown);
        Assert.NotEqual(0, GainOf(app, audio.Id));
        app.Press(Key.Back);
        Assert.Equal(0, GainOf(app, audio.Id));
        Assert.Equal(tracks, app.Session.Project.Sequence.Tracks.Length);

        // Home and End belong to the bar: the transport does not move.
        app.Press(Key.Home);
        app.Press(Key.End);
        Assert.Equal(playhead, app.ViewModel.PlayheadTick);
        Assert.Same(selected, app.ViewModel.SelectedTrack);
    });

    [Fact]
    public Task PanRamp_DeleteAndBackspace_CentreTheFocusedRamp_NotTheWindowShortcuts() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, _) = await SeedAsync(app);
        await SetStripAsync(app, audio.Id, 0, 0.5);
        var mixer = ShowDockedMixer(app);
        var ramp = Visible<PanKnob>(StripOf(mixer, audio.Name), $"{audio.Name} pan");
        var tracks = app.Session.Project.Sequence.Tracks.Length;
        app.ViewModel.NextBarCommand.Execute(null);
        var playhead = app.ViewModel.PlayheadTick;

        ramp.Focus(NavigationMethod.Tab);
        app.Settle();
        app.Press(Key.Delete);
        Assert.Equal(0, PanOf(app, audio.Id));
        app.Press(Key.Right);
        Assert.True(PanOf(app, audio.Id) > 0);
        app.Press(Key.Back);
        Assert.Equal(0, PanOf(app, audio.Id));
        Assert.Equal(tracks, app.Session.Project.Sequence.Tracks.Length);

        app.Press(Key.Home);
        app.Press(Key.End);
        Assert.Equal(playhead, app.ViewModel.PlayheadTick);
    });

    [Fact]
    public Task GainBarHomeAndEnd_GoToTheMinimumAndTheBarCeiling_PanRampToHardLeftAndRight() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, _) = await SeedAsync(app);
        await SetStripAsync(app, audio.Id, -10, 0.5);
        var mixer = ShowDockedMixer(app);
        var strip = StripOf(mixer, audio.Name);
        var bar = Visible<ConsoleFader>(strip, $"{audio.Name} gain");
        var ramp = Visible<PanKnob>(strip, $"{audio.Name} pan");
        Assert.True(AudioStrip.MaxGainDecibels > MixerRail.GainCeiling, "The strip allows more boost than the bar draws.");

        bar.Focus(NavigationMethod.Tab);
        app.Settle();
        app.Press(Key.Home);
        Assert.Equal(AudioStrip.MinGainDecibels, GainOf(app, audio.Id));
        // End goes to the top of the drawn bar (+6 dB), not the strip's full boost.
        app.Press(Key.End);
        Assert.Equal(MixerRail.GainCeiling, GainOf(app, audio.Id));
        Assert.Equal("+6.0 dB", app.ViewModel.MixerConsole.Tracks.Single(row => row.Id == audio.Id).GainText);

        // Higher boost is still an arrow key away, and End from above the ceiling comes back down to it.
        app.Press(Key.Up);
        Assert.Equal(MixerRail.GainCeiling + 1, GainOf(app, audio.Id));
        app.Press(Key.End);
        Assert.Equal(MixerRail.GainCeiling, GainOf(app, audio.Id));

        ramp.Focus(NavigationMethod.Tab);
        app.Settle();
        app.Press(Key.Home);
        Assert.Equal(-1, PanOf(app, audio.Id));
        app.Press(Key.End);
        Assert.Equal(1, PanOf(app, audio.Id));
    });

    [Fact]
    public Task GainBar_PointerDragAndDoubleClick_SetGainAndUndoAsOneStepEach() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, _) = await SeedAsync(app);
        await SetStripAsync(app, audio.Id, -20, 0);
        var mixer = ShowDockedMixer(app);
        var strip = StripOf(mixer, audio.Name);
        var bar = Visible<ConsoleFader>(strip, $"{audio.Name} gain");
        Assert.True(bar.Bounds.Height > 40, $"The gain bar should be tall enough to drag, was {bar.Bounds.Height}.");
        var steps = app.Session.History.UndoCount;
        var centre = new Point(bar.Bounds.Width / 2, bar.Bounds.Height / 2);

        // Dragging up raises the gain; the four intermediate moves are one undo step.
        app.Drag(bar, centre, centre - new Point(0, bar.Bounds.Height / 6));
        var raised = GainOf(app, audio.Id);
        Assert.True(raised > -20, $"Dragging up should raise the gain, was {raised}.");
        Assert.Equal(raised, bar.Value);
        Assert.Equal(steps + 1, app.Session.History.UndoCount);

        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.Equal(-20, GainOf(app, audio.Id));
        Assert.Equal(-20, bar.Value);

        // Dragging down lowers it, never below the strip's minimum. Start elsewhere so it is not read as a double-click.
        var lower = new Point(bar.Bounds.Width / 4, bar.Bounds.Height / 3);
        app.Drag(bar, lower, lower + new Point(0, bar.Bounds.Height * 4));
        Assert.Equal(row(app).MinimumGain, GainOf(app, audio.Id));
        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.Equal(-20, GainOf(app, audio.Id));

        // A double-click returns to unity.
        var elsewhere = new Point(bar.Bounds.Width * 3 / 4, bar.Bounds.Height * 2 / 3);
        app.DoubleClickAt(bar, elsewhere);
        Assert.Equal(0, GainOf(app, audio.Id));
        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.Equal(-20, GainOf(app, audio.Id));

        MixerTrackStripViewModel row(AppUnderTest under) => under.ViewModel.MixerConsole.Tracks.Single(candidate => candidate.Id == audio.Id);
    });

    [Fact]
    public Task PanRamp_KeyboardAndDoubleClick_SetPanAndShowItsReadout() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, _) = await SeedAsync(app);
        await SetStripAsync(app, audio.Id, 0, 0.5);
        var mixer = ShowDockedMixer(app);
        var strip = StripOf(mixer, audio.Name);
        var ramp = Visible<PanKnob>(strip, $"{audio.Name} pan");
        var readout = Assert.Single(strip.GetLogicalDescendants().OfType<TextBlock>(), text =>
            text.IsEffectivelyVisible && !text.Classes.Contains("mixerCaption") && text.Parent is Grid grid && grid.Children.Contains(ramp));
        Assert.Equal("R50", readout.Text);
        var steps = app.Session.History.UndoCount;

        ramp.Focus(NavigationMethod.Tab);
        app.Settle();
        app.Press(Key.Left);
        Assert.Equal(0.48, PanOf(app, audio.Id), 6);
        Assert.Equal("R48", readout.Text);
        app.Press(Key.PageDown);
        app.Press(Key.PageDown);
        app.Press(Key.PageDown);
        Assert.Equal(-0.27, PanOf(app, audio.Id), 6);
        Assert.Equal("L27", readout.Text);
        app.Press(Key.Right);
        Assert.Equal(-0.25, PanOf(app, audio.Id), 6);
        Assert.Equal("L25", readout.Text);
        Assert.Equal(steps + 1, app.Session.History.UndoCount);

        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.Equal(0.5, PanOf(app, audio.Id));
        Assert.Equal("R50", readout.Text);

        // Hard right stops at the end; a double-click centres.
        for (var i = 0; i < 4; i++)
        {
            app.Press(Key.PageUp);
        }

        Assert.Equal(1, PanOf(app, audio.Id));
        Assert.Equal("R100", readout.Text);
        app.DoubleClickAt(ramp, new Point(ramp.Bounds.Width * 0.9, ramp.Bounds.Height / 2));
        Assert.Equal(0, PanOf(app, audio.Id));
        Assert.Equal("<C>", readout.Text);
    });

    [Fact]
    public Task InsertPowerToggle_BypassesTheDevice_AndUndoRestoresIt() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (_, midi, _) = await SeedAsync(app);
        var devices = await AddDevicesAsync(app, midi.Id, BuiltInDevices.Transpose, BuiltInDevices.EventFilter);
        var mixer = ShowTallDockedMixer(app);
        var strip = StripOf(mixer, midi.Name);
        var power = Visible<ToggleButton>(strip, $"{devices[0].DisplayName} power");
        var row = Assert.IsType<Grid>(power.Parent);
        Assert.True(power.IsChecked);
        Assert.DoesNotContain("bypassed", row.Classes);

        app.Click(power);
        Assert.True(IsBypassed(app, devices[0].Id));
        Assert.False(IsBypassed(app, devices[1].Id));
        Assert.False(power.IsChecked);
        Assert.Contains("bypassed", row.Classes);
        Assert.Contains("1 bypassed", app.ViewModel.MixerConsole.Tracks.Single(r => r.Id == midi.Id).ChainSummary, StringComparison.Ordinal);

        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.False(IsBypassed(app, devices[0].Id));
        Assert.True(power.IsChecked);
        Assert.DoesNotContain("bypassed", row.Classes);

        app.ViewModel.RedoCommand.Execute(null);
        app.Settle();
        Assert.True(IsBypassed(app, devices[0].Id));
        Assert.False(power.IsChecked);
    });

    [Fact]
    public Task InsertOverflow_ShowsThreeRows_AndItsFlyoutListsAndOpensEveryDevice() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, midi, _) = await SeedAsync(app);
        var devices = await AddDevicesAsync(app, midi.Id,
            BuiltInDevices.Transpose, BuiltInDevices.EventFilter, BuiltInDevices.Arpeggiator, BuiltInDevices.Transpose, BuiltInDevices.EventFilter);
        var mixer = ShowTallDockedMixer(app);
        var strip = StripOf(mixer, midi.Name);
        app.Click(AppUnderTest.Within<Button>(mixer, button => button.Content is TextBlock text && text.Text == audio.Name));
        Assert.Equal(audio.Id, Assert.Single(app.ViewModel.SelectedTracks).Id);

        Assert.Equal(MixerConsoleViewModel.VisibleInsertRows, strip.GetLogicalDescendants().OfType<ToggleButton>().Count(button =>
            button.IsEffectivelyVisible && AutomationProperties.GetName(button)?.EndsWith(" power", StringComparison.Ordinal) == true));
        var more = Visible<Button>(strip, "Show every device on this track");
        Assert.Contains(more.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "+2 more");

        var list = OpenFlyout(app, more);
        var powers = list.GetLogicalDescendants().OfType<ToggleButton>().Where(button => button.IsEffectivelyVisible).ToArray();
        var opens = list.GetLogicalDescendants().OfType<Button>()
            .Where(button => button.IsEffectivelyVisible && AutomationProperties.GetName(button)?.StartsWith("Open ", StringComparison.Ordinal) == true
                && button.Content is TextBlock)
            .ToArray();
        Assert.Equal(devices.Select(device => $"{device.DisplayName} power"), powers.Select(AutomationProperties.GetName));
        Assert.Equal(devices.Select(device => $"Open {device.DisplayName}"), opens.Select(AutomationProperties.GetName));

        // The fifth device is only reachable here: bypass it, then open it.
        app.Click(powers[4]);
        Assert.True(IsBypassed(app, devices[4].Id));
        app.Click(opens[4]);

        Assert.Equal(midi.Id, Assert.Single(app.ViewModel.SelectedTracks).Id);
        Assert.Equal(LowerPane.Devices, app.ViewModel.LowerPane);
        var deviceView = Assert.IsType<DeviceViewViewModel>(app.ViewModel.Workspace.ActiveDeviceView);
        Assert.Equal(devices.Select(device => device.Id), deviceView.Devices.Select(panel => panel.Id));
        Assert.Equal(devices[4].Id, deviceView.SelectedDevice?.Id);
        Assert.True(deviceView.SelectedDevice!.IsBypassed);
    });

    [Fact]
    public Task AddDeviceAndInsertName_SelectTheTrackAndRevealItsChain() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, midi, _) = await SeedAsync(app);
        var mixer = ShowTallDockedMixer(app);
        app.Click(AppUnderTest.Within<Button>(mixer, button => button.Content is TextBlock text && text.Text == midi.Name));
        Assert.Equal(midi.Id, Assert.Single(app.ViewModel.SelectedTracks).Id);

        var vocal = StripOf(mixer, audio.Name);
        var add = Visible<Button>(vocal, "Add a device to this track");
        Assert.Contains(add.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "+");
        app.Click(add);

        Assert.Equal(audio.Id, Assert.Single(app.ViewModel.SelectedTracks).Id);
        Assert.Equal(LowerPane.Devices, app.ViewModel.LowerPane);
        Assert.NotNull(app.ViewModel.Workspace.ActiveDeviceView);
        Assert.Empty(app.ViewModel.Workspace.ActiveDeviceView!.Devices);

        // With a device on Keys, back in the mixer: its name opens the chain with that device selected.
        var devices = await AddDevicesAsync(app, midi.Id, BuiltInDevices.Transpose, BuiltInDevices.Arpeggiator);
        mixer = ShowTallDockedMixer(app);
        var keys = StripOf(mixer, midi.Name);
        // Two devices leave the well's third row free, so it still offers "+".
        Assert.Single(AllVisible<Button>(keys, "Add a device to this track"));
        app.Click(Visible<Button>(keys, $"Open {devices[1].DisplayName}"));

        Assert.Equal(midi.Id, Assert.Single(app.ViewModel.SelectedTracks).Id);
        Assert.Equal(LowerPane.Devices, app.ViewModel.LowerPane);
        Assert.Equal(devices[1].Id, app.ViewModel.Workspace.ActiveDeviceView?.SelectedDevice?.Id);
    });

    [Fact]
    public Task RecordArm_ArmsTheTrackBothWays_AndIsAbsentForGroups() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, effect) = await SeedAsync(app);
        var group = TrackModel.Create("Bus", TrackRole.Group);
        app.Session.Execute(new ProjectCommand("Add a group", project => project with
        {
            Sequence = project.Sequence.WithTrack(group),
            Connections = project.Connections.Add(SignalConnection.Create(SignalKind.Audio, SignalNode.Track(group.Id), SignalNode.Master)),
        }));
        await AppUnderTest.WaitUntilAsync(() => app.ViewModel.MixerConsole.Tracks.Count == 4, "the group strip");
        var mixer = ShowDockedMixer(app);
        var bus = StripOf(mixer, group.Name);
        Visible<ConsoleFader>(bus, $"{group.Name} gain");
        Assert.Empty(AllVisible<ToggleButton>(bus, $"Record arm {group.Name}"));

        var vocalArm = Visible<ToggleButton>(StripOf(mixer, audio.Name), $"Record arm {audio.Name}");
        var reverbArm = Visible<ToggleButton>(StripOf(mixer, effect.Name), $"Record arm {effect.Name}");
        var vocalRow = app.ViewModel.Tracks.Single(track => track.Id == audio.Id);

        app.Click(vocalArm);
        Assert.Equal(audio.Id, app.ViewModel.ArmedTrack?.Id);
        Assert.True(vocalRow.IsArmed);
        Assert.True(vocalArm.IsChecked);

        // One armed track: arming Reverb in the mixer disarms Vocal everywhere.
        app.Click(reverbArm);
        Assert.Equal(effect.Id, app.ViewModel.ArmedTrack?.Id);
        Assert.False(vocalRow.IsArmed);
        Assert.False(vocalArm.IsChecked);
        Assert.True(reverbArm.IsChecked);

        // Arming from the arrangement shows in the mixer.
        vocalRow.IsArmed = true;
        app.Settle();
        Assert.True(vocalArm.IsChecked);
        Assert.False(reverbArm.IsChecked);

        app.Click(vocalArm);
        Assert.Null(app.ViewModel.ArmedTrack);
        Assert.False(vocalArm.IsChecked);
        Assert.False(vocalRow.IsArmed);

        // An armed track that is deleted leaves nothing armed, and undo brings it back unarmed.
        app.Click(vocalArm);
        Assert.Equal(audio.Id, app.ViewModel.ArmedTrack?.Id);
        app.Session.Execute(ProjectCommands.RemoveTrack(audio.Id));
        await AppUnderTest.WaitUntilAsync(() => app.ViewModel.MixerConsole.Tracks.All(row => row.Id != audio.Id), "the deleted strip to go");
        app.Settle();
        Assert.Null(app.ViewModel.ArmedTrack);
        Assert.DoesNotContain(mixer.GetLogicalDescendants().OfType<Border>(), border =>
            AutomationProperties.GetName(border)?.StartsWith($"{audio.Name},", StringComparison.Ordinal) == true);

        app.ViewModel.UndoCommand.Execute(null);
        await AppUnderTest.WaitUntilAsync(() => app.ViewModel.MixerConsole.Tracks.Any(row => row.Id == audio.Id), "the strip to return");
        app.Settle();
        Assert.Null(app.ViewModel.ArmedTrack);
        Assert.False(Visible<ToggleButton>(StripOf(mixer, audio.Name), $"Record arm {audio.Name}").IsChecked);
        Assert.False(app.ViewModel.MixerConsole.Tracks.Single(row => row.Id == audio.Id).IsArmed);
    });

    [Fact]
    public Task MidiOnlyStrip_HasNoGainBarPanOrMeter_ButKeepsItsInserts() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (_, midi, _) = await SeedAsync(app);
        var devices = await AddDevicesAsync(app, midi.Id, BuiltInDevices.Transpose);
        var mixer = ShowTallDockedMixer(app);
        var keys = StripOf(mixer, midi.Name);

        Assert.DoesNotContain(keys.GetLogicalDescendants().OfType<MixerValueControl>(), control => control.IsEffectivelyVisible);
        Assert.DoesNotContain(keys.GetLogicalDescendants().OfType<LedLadderMeter>(), rail => rail.IsEffectivelyVisible);
        Assert.DoesNotContain(keys.GetLogicalDescendants().OfType<ConsoleScale>(), scale => scale.IsEffectivelyVisible);
        Assert.DoesNotContain(keys.GetLogicalDescendants().OfType<Button>(), button => button.IsEffectivelyVisible
            && AutomationProperties.GetName(button) is { } name
            && (name.Contains("send", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Output:", StringComparison.Ordinal)));
        Assert.Contains(keys.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "MIDI ONLY" && text.IsEffectivelyVisible);
        Visible<ToggleButton>(keys, $"{devices[0].DisplayName} power");
    });

    [Fact]
    public Task ShortPane_FoldsOnlyTheProcessingBand_AndKeepsReadoutsMeterAndOutputOnScreen() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, midi, effect) = await SeedAsync(app);
        await AddDevicesAsync(app, midi.Id, BuiltInDevices.Transpose);
        var view = new MixerConsoleView { DataContext = app.ViewModel.MixerConsole };
        var window = new Window { Width = 1100, Height = 300, Content = view };
        window.Show();
        try
        {
            app.Settle();
            window.UpdateLayout();
            app.Settle();
            var fullHeight = (double)view.FindResource("Mixer.FullStripHeight")!;
            var strips = view.FindControl<Grid>("StripsArea")!;
            Assert.True(strips.Bounds.Height < fullHeight, $"The pane should be short: {strips.Bounds.Height} of {fullHeight}.");
            Assert.Contains("short", view.Classes);
            AssertStripBody(window, view, audio.Name, effect.Name);
            Assert.All(Processing(view), band => Assert.False(band.IsEffectivelyVisible));
            Assert.Empty(AllVisible<Button>(view, "No sends, add a send"));
            Assert.DoesNotContain(view.GetLogicalDescendants().OfType<ToggleButton>(), button =>
                button.IsEffectivelyVisible && AutomationProperties.GetName(button)?.EndsWith(" power", StringComparison.Ordinal) == true);
            app.SaveFrame("mixer-short-pane", window);

            // Taller again: the band returns.
            window.Height = 720;
            app.Settle();
            window.UpdateLayout();
            app.Settle();
            Assert.DoesNotContain("short", view.Classes);
            Assert.All(Processing(view), band => Assert.True(band.IsEffectivelyVisible));
            Assert.Equal(2, AllVisible<Button>(view, "No sends, add a send").Count());
            AssertStripBody(window, view, audio.Name, effect.Name);
        }
        finally
        {
            window.Close();
        }

        static Border[] Processing(Control view) => view.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("mixerBand") && border.Classes.Contains("processing")).ToArray();
    });

    [Fact]
    public Task EveryMeterMode_KeepsOneMeterRailPerAudioStrip() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        await SeedAsync(app);
        var mixer = ShowDockedMixer(app);

        // Pick VU from the header menu, as a musician would.
        var mode = Visible<Button>(mixer, "Mixer meter display mode");
        app.Click(mode);
        var menu = Assert.IsType<MenuFlyout>(mode.Flyout);
        Assert.True(menu.IsOpen);
        app.Click(menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "VU")));
        Assert.Equal(MixerMeterMode.Vu, app.ViewModel.MixerConsole.MeterMode);
        RefreshMeters();
        AssertRails(MixerMeterMode.Vu);

        foreach (var each in Enum.GetValues<MixerMeterMode>())
        {
            app.ViewModel.MixerConsole.SetMeterModeCommand.Execute(each);
            RefreshMeters();
            AssertRails(each);
        }

        // The app's frame loop refreshes the strips every frame; do one frame here.
        void RefreshMeters()
        {
            app.ViewModel.MixerConsole.RefreshMeters(System.Diagnostics.Stopwatch.GetTimestamp());
            app.Settle();
        }

        void AssertRails(MixerMeterMode expected)
        {
            var rails = mixer.GetLogicalDescendants().OfType<LedLadderMeter>().Where(rail => rail.IsEffectivelyVisible).ToArray();
            Assert.Equal(3, rails.Length); // Vocal, Reverb, Master; Keys is MIDI only.
            Assert.All(rails, rail => Assert.Equal(expected, rail.Mode));
            Assert.Equal(3, mixer.GetLogicalDescendants().OfType<ConsoleFader>().Count(bar => bar.IsEffectivelyVisible));
        }
    });

    [Fact]
    public Task InsertStack_FollowsChainEditsMadeElsewhere_UndoRedoAndTrackDeletion() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (_, midi, _) = await SeedAsync(app);
        var devices = await AddDevicesAsync(app, midi.Id,
            BuiltInDevices.Transpose, BuiltInDevices.EventFilter, BuiltInDevices.Arpeggiator, BuiltInDevices.Transpose, BuiltInDevices.EventFilter);
        var mixer = ShowTallDockedMixer(app);
        var keysRow = app.ViewModel.MixerConsole.Tracks.Single(row => row.Id == midi.Id);
        AssertStack(5);

        // Open "+2 more", then the Device view removes the fifth device while the list is open: the list follows.
        var more = Visible<Button>(StripOf(mixer, midi.Name), "Show every device on this track");
        var list = OpenFlyout(app, more);
        await ExecuteElsewhereAsync(DeviceCommands.RemoveDevice(devices[4].Id, BuiltInDevices.Find), () => keysRow.Inserts.Count == 4);
        Assert.Equal(devices.Take(4).Select(device => $"{device.DisplayName} power"), Powers(list).Select(AutomationProperties.GetName));
        ((Flyout)more.Flyout!).Hide();
        app.Settle();
        AssertStack(4);

        // Bypassed elsewhere: the mixer's power toggle and row follow; undo and redo follow too.
        await ExecuteElsewhereAsync(DeviceCommands.SetBypassed(devices[0].Id, true, BuiltInDevices.Find), () => !keysRow.Inserts[0].IsEnabled);
        var power = Visible<ToggleButton>(StripOf(mixer, midi.Name), $"{devices[0].DisplayName} power");
        Assert.False(power.IsChecked);
        Assert.Contains("bypassed", Assert.IsType<Grid>(power.Parent).Classes);
        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.True(power.IsChecked);
        Assert.DoesNotContain("bypassed", Assert.IsType<Grid>(power.Parent).Classes);

        // Undo the removal: the fifth device returns behind "+2 more"; redo removes it again.
        app.ViewModel.UndoCommand.Execute(null);
        await AppUnderTest.WaitUntilAsync(() => keysRow.Inserts.Count == 5, "the removed device to return");
        app.Settle();
        AssertStack(5);
        app.ViewModel.RedoCommand.Execute(null);
        await AppUnderTest.WaitUntilAsync(() => keysRow.Inserts.Count == 4, "the device to be removed again");
        app.Settle();
        AssertStack(4);

        // Bypassing from a Device view panel reaches the project and the mixer.
        app.Click(Visible<Button>(StripOf(mixer, midi.Name), $"Open {devices[1].DisplayName}"));
        var panel = app.ViewModel.Workspace.ActiveDeviceView!.Devices.Single(device => device.Id == devices[1].Id);
        panel.IsBypassed = true;
        app.Settle();
        Assert.True(IsBypassed(app, devices[1].Id));
        mixer = ShowTallDockedMixer(app);
        Assert.False(Visible<ToggleButton>(StripOf(mixer, midi.Name), $"{devices[1].DisplayName} power").IsChecked);

        // Deleting the track removes its strip; undo brings it back with its stack.
        app.Session.Execute(ProjectCommands.RemoveTrack(midi.Id));
        await AppUnderTest.WaitUntilAsync(() => app.ViewModel.MixerConsole.Tracks.All(row => row.Id != midi.Id), "the strip to go");
        app.Settle();
        Assert.DoesNotContain(mixer.GetLogicalDescendants().OfType<Border>(), border =>
            AutomationProperties.GetName(border)?.StartsWith($"{midi.Name},", StringComparison.Ordinal) == true);
        app.ViewModel.UndoCommand.Execute(null);
        await AppUnderTest.WaitUntilAsync(() => app.ViewModel.MixerConsole.Tracks.Any(row => row.Id == midi.Id), "the strip to return");
        app.Settle();
        keysRow = app.ViewModel.MixerConsole.Tracks.Single(row => row.Id == midi.Id);
        AssertStack(4);
        Assert.False(Visible<ToggleButton>(StripOf(mixer, midi.Name), $"{devices[1].DisplayName} power").IsChecked);

        async Task ExecuteElsewhereAsync(IProjectCommand command, Func<bool> until)
        {
            Assert.True(app.Session.Execute(command));
            await AppUnderTest.WaitUntilAsync(until, "the mixer to follow the chain edit");
            app.Settle();
        }

        static ToggleButton[] Powers(Control scope) => scope.GetLogicalDescendants().OfType<ToggleButton>()
            .Where(button => button.IsEffectivelyVisible && AutomationProperties.GetName(button)?.EndsWith(" power", StringComparison.Ordinal) == true)
            .ToArray();

        void AssertStack(int count)
        {
            var strip = StripOf(mixer, midi.Name);
            Assert.Equal(Math.Min(count, MixerConsoleViewModel.VisibleInsertRows), Powers(strip).Length);
            var overflow = AllVisible<Button>(strip, "Show every device on this track").ToArray();
            if (count > MixerConsoleViewModel.VisibleInsertRows)
            {
                Assert.Contains(Assert.Single(overflow).GetLogicalDescendants().OfType<TextBlock>(),
                    text => text.Text == $"+{count - MixerConsoleViewModel.VisibleInsertRows} more");
            }
            else
            {
                Assert.Empty(overflow);
            }
        }
    });

    [Fact]
    public Task InsertName_SpaceTogglesPower_AndThePowerSwitchIsNotATabStop() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (_, midi, _) = await SeedAsync(app);
        var devices = await AddDevicesAsync(app, midi.Id, BuiltInDevices.Transpose, BuiltInDevices.Arpeggiator);

        // The mixer as the detached window hosts it: its own top level, without the main window's shortcuts.
        var view = new MixerConsoleView { DataContext = app.ViewModel.MixerConsole };
        var window = new Window { Width = 1100, Height = 720, Content = view };
        window.Show();
        try
        {
            app.Settle();
            window.UpdateLayout();
            app.Settle();
            var keys = StripOf(view, midi.Name);
            var power = Visible<ToggleButton>(keys, $"{devices[0].DisplayName} power");
            var name = Visible<Button>(keys, $"Open {devices[0].DisplayName}");
            Assert.False(power.IsTabStop);
            Assert.True(name.IsTabStop);
            var steps = app.Session.History.UndoCount;
            var pane = app.ViewModel.LowerPane;

            name.Focus(NavigationMethod.Tab);
            app.Settle();
            Press(Key.Space);
            Assert.True(IsBypassed(app, devices[0].Id));
            Assert.False(IsBypassed(app, devices[1].Id));
            Assert.False(power.IsChecked);
            Assert.Equal(pane, app.ViewModel.LowerPane); // Space did not also open the device.
            Assert.True(name.IsFocused);
            Assert.Equal(steps + 1, app.Session.History.UndoCount);

            Press(Key.Space);
            Assert.False(IsBypassed(app, devices[0].Id));
            Assert.True(power.IsChecked);

            // Tab from the first name lands on the next insert's name, never on a power switch.
            Press(Key.Tab);
            Assert.True(Visible<Button>(keys, $"Open {devices[1].DisplayName}").IsFocused);

            app.ViewModel.UndoCommand.Execute(null);
            app.Settle();
            Assert.True(IsBypassed(app, devices[0].Id));
        }
        finally
        {
            window.Close();
        }

        void Press(Key key)
        {
            window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
            app.Settle();
        }
    });

    [Fact]
    public Task DockedInsertName_Space_ReachesTheFocusedName_NotPlayPause() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (_, midi, _) = await SeedAsync(app);
        var devices = await AddDevicesAsync(app, midi.Id, BuiltInDevices.Transpose);
        var mixer = ShowDockedMixer(app);
        var name = Visible<Button>(StripOf(mixer, midi.Name), $"Open {devices[0].DisplayName}");
        var tracks = app.ViewModel.Tracks.Select(track => track.Name).ToArray();

        name.Focus(NavigationMethod.Tab);
        app.Settle();
        app.Press(Key.Space);
        Assert.True(IsBypassed(app, devices[0].Id),
            $"Space on a focused insert name should bypass it (focused: {name.IsFocused}, pane: {app.ViewModel.LowerPane}).");
        Assert.False(TransportStarted(app), "Space on an insert name must not also start playback.");
        Assert.False(app.ViewModel.IsPlaying);

        Assert.True(name.IsFocused);
        Assert.Equal(tracks, app.ViewModel.Tracks.Select(track => track.Name));

        // The check above can see playback: Space on another mixer button (not an insert name) still plays.
        var master = Visible<Button>(mixer, "Select Master");
        master.Focus(NavigationMethod.Tab);
        app.Settle();
        Assert.True(master.IsFocused);
        app.Press(Key.Space);
        Assert.True(TransportStarted(app), "Space on a non-insert button should still start playback.");
        Assert.True(IsBypassed(app, devices[0].Id));
    });

    [Fact]
    public Task OverflowInsertName_SpaceInTheDeviceList_TogglesPower_NotPlayback() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (_, midi, _) = await SeedAsync(app);
        var devices = await AddDevicesAsync(app, midi.Id,
            BuiltInDevices.Transpose, BuiltInDevices.EventFilter, BuiltInDevices.Arpeggiator, BuiltInDevices.Transpose, BuiltInDevices.EventFilter);
        var mixer = ShowTallDockedMixer(app);
        var list = OpenFlyout(app, Visible<Button>(StripOf(mixer, midi.Name), "Show every device on this track"));
        var fourth = Assert.Single(list.GetLogicalDescendants().OfType<Button>(), button =>
            button.IsEffectivelyVisible && button.Classes.Contains("insertName") && ReferenceEquals(button.DataContext,
                app.ViewModel.MixerConsole.Tracks.Single(row => row.Id == midi.Id).Inserts[3]));
        var power = Assert.Single(list.GetLogicalDescendants().OfType<ToggleButton>(), toggle =>
            toggle.IsEffectivelyVisible && ReferenceEquals(toggle.DataContext, fourth.DataContext));
        var steps = app.Session.History.UndoCount;
        var pane = app.ViewModel.LowerPane;

        fourth.Focus(NavigationMethod.Tab);
        app.Settle();
        Assert.True(fourth.IsFocused);
        var top = TopLevel.GetTopLevel(fourth)!;
        top.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        top.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        app.Settle();

        Assert.True(IsBypassed(app, devices[3].Id), "Space on the fourth insert's name in the list should bypass it.");
        Assert.All(devices.Where((_, index) => index != 3), device => Assert.False(IsBypassed(app, device.Id)));
        Assert.False(power.IsChecked);
        Assert.False(TransportStarted(app), "Space on an insert name in the list must not start playback.");
        Assert.Equal(pane, app.ViewModel.LowerPane);
        Assert.Equal(steps + 1, app.Session.History.UndoCount);

        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.False(IsBypassed(app, devices[3].Id));
    });


    [Fact]
    public Task GainBarWheel_VerticalAdjusts_ShiftOrHorizontalPassesThroughToTheStripRow() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, _) = await SeedAsync(app);
        await SetStripAsync(app, audio.Id, -10, 0);
        var mixer = ShowDockedMixer(app);
        var bar = Visible<ConsoleFader>(StripOf(mixer, audio.Name), $"{audio.Name} gain");
        var at = app.InWindow(bar, new Point(bar.Bounds.Width / 2, bar.Bounds.Height / 2));
        bool? handled = null;
        Assert.IsAssignableFrom<Control>(bar.Parent).AddHandler(InputElement.PointerWheelChangedEvent,
            (_, e) => handled = e.Handled, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

        app.Window.MouseWheel(at, new Vector(0, 1));
        app.Settle();
        Assert.Equal(-9, GainOf(app, audio.Id));
        Assert.True(handled);

        handled = null;
        app.Window.MouseWheel(at, new Vector(0, 1), RawInputModifiers.Shift);
        app.Settle();
        Assert.Equal(-9, GainOf(app, audio.Id));
        Assert.False(handled, "A Shift wheel should reach the strip row's scroller.");

        handled = null;
        app.Window.MouseWheel(at, new Vector(2, 0.5));
        app.Settle();
        Assert.Equal(-9, GainOf(app, audio.Id));
        Assert.False(handled, "A mostly horizontal wheel should reach the strip row's scroller.");
    });

    [Fact]
    public Task GainBarReset_AfterAnAdjustment_IsItsOwnUndoStep() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, _) = await SeedAsync(app);
        await SetStripAsync(app, audio.Id, -10, 0);
        var mixer = ShowDockedMixer(app);
        var bar = Visible<ConsoleFader>(StripOf(mixer, audio.Name), $"{audio.Name} gain");

        // Keys, then Delete at once (inside the history's merge window): Undo brings back the adjusted value.
        bar.Focus(NavigationMethod.Tab);
        app.Settle();
        app.Press(Key.Up);
        app.Press(Key.Up);
        Assert.Equal(-8, GainOf(app, audio.Id));
        app.Press(Key.Delete);
        Assert.Equal(0, GainOf(app, audio.Id));
        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.Equal(-8, GainOf(app, audio.Id));
        Assert.Equal(-8, bar.Value);
        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.Equal(-10, GainOf(app, audio.Id));

        // A drag, then Delete.
        var centre = new Point(bar.Bounds.Width / 2, bar.Bounds.Height / 2);
        app.Drag(bar, centre, centre - new Point(0, bar.Bounds.Height / 6));
        var dragged = GainOf(app, audio.Id);
        Assert.True(dragged > -10, $"Dragging up should raise the gain, was {dragged}.");
        bar.Focus(NavigationMethod.Tab);
        app.Settle();
        app.Press(Key.Back);
        Assert.Equal(0, GainOf(app, audio.Id));
        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.Equal(dragged, GainOf(app, audio.Id));
    });

    [Fact]
    public Task GainBarKeys_BelowTheBarFloor_StepFromTheRealValue() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, _) = await SeedAsync(app);
        await SetStripAsync(app, audio.Id, -80, 0);
        var mixer = ShowDockedMixer(app);
        var bar = Visible<ConsoleFader>(StripOf(mixer, audio.Name), $"{audio.Name} gain");
        Assert.True(-80 < MixerRail.GainFloor);

        bar.Focus(NavigationMethod.Tab);
        app.Settle();
        app.Press(Key.Up);
        Assert.Equal(-79, GainOf(app, audio.Id));
        app.Press(Key.Down, RawInputModifiers.Shift);
        Assert.Equal(-79.1, GainOf(app, audio.Id), 6);
    });

    [Fact]
    public Task GainBar_CtrlArrowChord_LeavesTheGainAlone() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, _) = await SeedAsync(app);
        await SetStripAsync(app, audio.Id, -10, 0);
        var mixer = ShowDockedMixer(app);
        var bar = Visible<ConsoleFader>(StripOf(mixer, audio.Name), $"{audio.Name} gain");
        var steps = app.Session.History.UndoCount;

        bar.Focus(NavigationMethod.Tab);
        app.Settle();
        app.Press(Key.Up, RawInputModifiers.Control);
        app.Press(Key.Down, RawInputModifiers.Control);
        app.Press(Key.PageUp, RawInputModifiers.Control);
        app.Press(Key.Up, RawInputModifiers.Alt);
        Assert.Equal(-10, GainOf(app, audio.Id));
        Assert.Equal(steps, app.Session.History.UndoCount);

        // Without the chord the bar still takes the key.
        bar.Focus(NavigationMethod.Tab);
        app.Settle();
        app.Press(Key.Up);
        Assert.Equal(-9, GainOf(app, audio.Id));
    });

    [Fact]
    public Task GainBarAutomation_OnADisabledStrip_IsReadOnlyAndRefusesAValue() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, _) = await SeedAsync(app);
        await SetStripAsync(app, audio.Id, -10, 0);
        var mixer = ShowDockedMixer(app);
        var strip = StripOf(mixer, audio.Name);
        var bar = Visible<ConsoleFader>(strip, $"{audio.Name} gain");
        var range = Assert.IsAssignableFrom<IRangeValueProvider>(ControlAutomationPeer.CreatePeerForElement(bar));
        Assert.False(range.IsReadOnly);

        // Disabled through its strip, not on itself: the bar is effectively disabled.
        strip.IsEnabled = false;
        app.Settle();
        Assert.True(bar.IsEnabled);
        Assert.False(bar.IsEffectivelyEnabled);
        Assert.True(range.IsReadOnly);
        Assert.Throws<ElementNotEnabledException>(() => range.SetValue(-3));
        app.Settle();
        Assert.Equal(-10, GainOf(app, audio.Id));
        Assert.Equal(-10, bar.Value);

        strip.IsEnabled = true;
        app.Settle();
        range.SetValue(-3);
        app.Settle();
        Assert.Equal(-3, GainOf(app, audio.Id));
    });

    [Fact]
    public Task ClipLight_LatchesOverZeroDbfs_AndClickingTheMeterResetsIt() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, _) = await SeedAsync(app);
        var mixer = ShowDockedMixer(app);
        var strip = StripOf(mixer, audio.Name);
        var row = app.ViewModel.MixerConsole.Tracks.Single(candidate => candidate.Id == audio.Id);
        var rail = Assert.Single(strip.GetLogicalDescendants().OfType<LedLadderMeter>(), candidate => candidate.IsEffectivelyVisible);
        var reading = new MeterReading(0.5f, 0.5f, 0.3f, 0.3f);
        app.ViewModel.MixerConsole.MeterSource = _ => reading;
        var timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        app.ViewModel.MixerConsole.RefreshMeters(timestamp);
        app.Settle();
        Assert.Empty(AllVisible<Button>(strip, "Clipped – click to reset"));
        Assert.False(rail.IsClipped);

        // One peak over 0 dBFS lights it, and it stays lit after the level falls.
        reading = new MeterReading(1.2f, 0.5f, 0.6f, 0.3f);
        app.ViewModel.MixerConsole.RefreshMeters(timestamp + 1);
        reading = new MeterReading(0.1f, 0.1f, 0.05f, 0.05f);
        app.ViewModel.MixerConsole.RefreshMeters(timestamp + 2);
        app.Settle();
        Assert.True(row.IsClipped);
        Assert.True(rail.IsClipped);
        var clip = Visible<Button>(strip, "Clipped – click to reset");

        app.Click(clip);
        Assert.False(row.IsClipped);
        Assert.False(rail.IsClipped);
        Assert.Empty(AllVisible<Button>(strip, "Clipped – click to reset"));

        app.ViewModel.MixerConsole.RefreshMeters(timestamp + 3);
        app.Settle();
        Assert.Empty(AllVisible<Button>(strip, "Clipped – click to reset"));
    });
    /// <summary>Whether the transport is playing; the window under test has no playback thread, so this pumps the engine.</summary>
    private static bool TransportStarted(AppUnderTest app)
    {
        app.Playback.Engine.Pump();
        return app.Playback.Engine.State == TransportState.Playing;
    }

    [Fact]
    public Task DefaultDockedMixer_KeepsItsInsertStackAndSends() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, midi, _) = await SeedAsync(app);
        var devices = await AddDevicesAsync(app, midi.Id, BuiltInDevices.Transpose);
        Assert.Equal(WorkspaceViewModel.DefaultDetailExtent, app.ViewModel.Workspace.DetailExtent);
        var mixer = ShowDockedMixer(app);

        Assert.DoesNotContain("short", mixer.Classes);
        Visible<ToggleButton>(StripOf(mixer, midi.Name), $"{devices[0].DisplayName} power");
        Visible<Button>(StripOf(mixer, audio.Name), "No sends, add a send");
        Assert.Empty(AllVisible<Button>(mixer, "Show devices and sends"));
    });

    [Fact]
    public Task StripHeightTiers_FoldTheProcessingBandIntoACueOnlyBelowTheFullStripHeight() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, midi, effect) = await SeedAsync(app);
        await AddDevicesAsync(app, midi.Id, BuiltInDevices.Transpose, BuiltInDevices.Arpeggiator);
        var view = new MixerConsoleView { DataContext = app.ViewModel.MixerConsole };
        var window = new Window { Width = 1100, Height = 300, Content = view };
        window.Show();
        try
        {
            var full = (double)view.FindResource("Mixer.FullStripHeight")!;
            var roomy = (double)view.FindResource("Mixer.RoomyStripHeight")!;
            var strips = view.FindControl<Grid>("StripsArea")!;
            var seen = new HashSet<string>();
            foreach (var height in new[] { 300.0, 380, 440, 520, 720 })
            {
                window.Height = height;
                app.Settle();
                window.UpdateLayout();
                app.Settle();
                var area = strips.Bounds.Height;
                var isShort = area < full;
                var isTight = area < roomy;
                seen.Add(isShort ? "short" : isTight ? "tight" : "full");
                Assert.Equal(isShort, view.Classes.Contains("short"));
                Assert.Equal(isTight, view.Classes.Contains("tight"));
                Assert.Equal(isShort ? 4 : 0, AllVisible<Button>(view, "Show devices and sends").Count() + AllVisible<Button>(view, "Show Master devices").Count());
                Assert.Equal(isShort ? 0 : 2, AllVisible<Button>(view, "No sends, add a send").Count());
                AssertStripBody(window, view, audio.Name, effect.Name);
            }

            Assert.Equal(["full", "short", "tight"], seen.Order());

            // Folded: Keys' cue line names its devices and opens the full chain list; Vocal's opens its sends too.
            window.Height = 300;
            app.Settle();
            window.UpdateLayout();
            app.Settle();
            var keysCue = Visible<Button>(StripOf(view, midi.Name), "Show devices and sends");
            Assert.Contains(keysCue.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "2 devices ›");
            var vocalCue = Visible<Button>(StripOf(view, audio.Name), "Show devices and sends");
            Assert.Contains(vocalCue.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "No devices · no sends ›");

            var keysList = OpenFlyout(app, keysCue);
            Assert.Equal(2, keysList.GetLogicalDescendants().OfType<ToggleButton>().Count(button =>
                button.IsEffectivelyVisible && AutomationProperties.GetName(button)?.EndsWith(" power", StringComparison.Ordinal) == true));
            Assert.Null(keysList.GetLogicalDescendants().OfType<ComboBox>().FirstOrDefault(box =>
                box.IsEffectivelyVisible && AutomationProperties.GetName(box) == "New send destination"));
            ((Flyout)keysCue.Flyout!).Hide();
            app.Settle();

            var vocalList = OpenFlyout(app, vocalCue);
            app.Choose(AppUnderTest.ComboNamed(vocalList, "New send destination"), item =>
                item is MixerEffectOption option && option.Track == effect.Id);
            app.Click(AppUnderTest.Within<Button>(vocalList, button => Equals(button.Content, "Add")));
            Assert.Single(app.Session.Project.Connections, connection => connection.AudioPurpose == AudioRoutePurpose.Send);
            Assert.Single(AllVisible<Knob>(vocalList, $"Send to {effect.Name} level"));
            Assert.Contains(vocalCue.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "No devices · 1 send ›");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task SendKnob_ExposesARangeValue_AndOwnsHomeEndAndDelete() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, effect) = await SeedAsync(app);
        Assert.True(app.Session.Execute(RoutingCommands.AddAudioSend(audio.Id, effect.Id, -12, false, BuiltInDevices.Find)));
        await AppUnderTest.WaitUntilAsync(() => app.ViewModel.MixerConsole.Tracks.Single(row => row.Id == audio.Id).Sends.Count == 1, "the send knob");
        var mixer = ShowDockedMixer(app);
        var sends = OpenFlyout(app, Visible<Button>(StripOf(mixer, audio.Name), "1 send, show all or add"));
        var knob = Visible<Knob>(sends, $"Send to {effect.Name} level");

        var peer = ControlAutomationPeer.CreatePeerForElement(knob);
        Assert.Equal(AutomationControlType.Slider, peer.GetAutomationControlType());
        var range = Assert.IsAssignableFrom<IRangeValueProvider>(peer);
        Assert.Equal(AudioStrip.MinGainDecibels, range.Minimum);
        Assert.Equal(AudioStrip.MaxGainDecibels, range.Maximum);
        Assert.Equal(-12, range.Value);
        Assert.Equal("-12.0 dB", peer.GetHelpText());

        // A screen reader sets the value: the project's send follows and the spoken value has its unit.
        range.SetValue(-21);
        app.Settle();
        Assert.Equal(-21, SendLevel(), 6);
        Assert.Equal(-21, range.Value, 6);
        Assert.Equal("-21.0 dB", peer.GetHelpText());

        // Focused in the main window, Home/End/Delete belong to the knob, not the transport or Delete Tracks.
        var tracks = app.Session.Project.Sequence.Tracks.Length;
        app.ViewModel.NextBarCommand.Execute(null);
        var playhead = app.ViewModel.PlayheadTick;
        knob.Focus(NavigationMethod.Tab);
        app.Settle();
        app.Press(Key.Home);
        Assert.Equal(AudioStrip.MinGainDecibels, SendLevel());
        app.Press(Key.End);
        Assert.Equal(AudioStrip.MaxGainDecibels, SendLevel());
        app.Press(Key.Delete);
        Assert.Equal(0, SendLevel());
        app.Press(Key.Back);
        Assert.Equal(0, SendLevel());
        Assert.Equal(tracks, app.Session.Project.Sequence.Tracks.Length);
        Assert.Equal(playhead, app.ViewModel.PlayheadTick);

        double SendLevel() => app.Session.Project.Connections.Single(connection => connection.AudioPurpose == AudioRoutePurpose.Send).SendLevelDecibels;
    });

    [Fact]
    public Task StripWithThreeSends_ShowsTwoKnobs_AndTheListShowsAllThree() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, effect) = await SeedAsync(app);
        var more = new[] { TrackModel.Create("Delay", TrackRole.Effect), TrackModel.Create("Chorus", TrackRole.Effect) };
        app.Session.Execute(new ProjectCommand("Add returns", project => project with
        {
            Sequence = more.Aggregate(project.Sequence, (sequence, track) => sequence.WithTrack(track)),
            Connections = project.Connections.AddRange(more.Select(track =>
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Master))),
        }));
        var targets = new[] { effect, more[0], more[1] };
        foreach (var target in targets)
        {
            Assert.True(app.Session.Execute(RoutingCommands.AddAudioSend(audio.Id, target.Id, -6, false, BuiltInDevices.Find)));
        }

        var row = app.ViewModel.MixerConsole.Tracks.Single(candidate => candidate.Id == audio.Id);
        await AppUnderTest.WaitUntilAsync(() => row.Sends.Count == 3, "three sends");
        var mixer = ShowDockedMixer(app);
        var strip = StripOf(mixer, audio.Name);

        // The strip face carries a count, not knobs: every send level lives in the sends list.
        Assert.DoesNotContain(strip.GetLogicalDescendants().OfType<Knob>(), knob => knob.IsEffectivelyVisible);
        var header = Visible<Button>(strip, "3 sends, show all or add");
        Assert.Contains(header.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "3 SENDS" && text.IsEffectivelyVisible);

        var list = OpenFlyout(app, header);
        Assert.Equal(targets.Select(target => $"Send to {target.Name} level"),
            list.GetLogicalDescendants().OfType<Knob>().Where(knob => knob.IsEffectivelyVisible).Select(AutomationProperties.GetName));

        // Removing one from the list leaves two.
        app.Click(list.GetLogicalDescendants().OfType<Button>().First(button =>
            button.IsEffectivelyVisible && AutomationProperties.GetName(button) == "Remove audio send"));
        await AppUnderTest.WaitUntilAsync(() => row.Sends.Count == 2, "the send to go");
        ((Flyout)header.Flyout!).Hide();
        app.Settle();
        Assert.Contains(strip.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "2 SENDS" && text.IsEffectivelyVisible);
        Assert.DoesNotContain(Visible<Button>(strip, "2 sends, show all or add").GetLogicalDescendants().OfType<TextBlock>(),
            text => text.IsEffectivelyVisible && text.Text?.StartsWith('+') == true);
    });

    [Fact]
    public Task PeakRmsRail_FillsToTheLouderRms_AndHoldsThePeakMarker() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, _) = await SeedAsync(app);
        var mixer = ShowDockedMixer(app);
        Assert.Equal(MixerMeterMode.PeakRms, app.ViewModel.MixerConsole.MeterMode);
        var rail = Visible<LedLadderMeter>(StripOf(mixer, audio.Name), "Track level meter");
        var reading = new MeterReading(0.9f, 0.6f, 0.2f, 0.5f);
        app.ViewModel.MixerConsole.MeterSource = _ => reading;
        var timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        app.ViewModel.MixerConsole.RefreshMeters(timestamp);
        app.Settle();

        // The fill is the louder side's RMS (the right here), not either peak; the marker is the louder held peak.
        Assert.Equal(MeterReading.ToDecibels(0.5f), rail.Value, 5);
        Assert.Equal(MeterReading.ToDecibels(0.9f), rail.PeakHold, 5);
        Assert.True(rail.PeakHold - rail.Value > 4, $"Peak {rail.PeakHold} should sit well above the RMS fill {rail.Value}.");

        // Quieter: the fill falls with the RMS while the marker holds.
        reading = new MeterReading(0.3f, 0.3f, 0.1f, 0.05f);
        app.ViewModel.MixerConsole.RefreshMeters(timestamp + 1);
        app.Settle();
        Assert.Equal(MeterReading.ToDecibels(0.1f), rail.Value, 5);
        Assert.Equal(MeterReading.ToDecibels(0.9f), rail.PeakHold, 5);
    });

    private static void AssertStripBody(Window window, Control view, params string[] audioTracks)
    {
        var limit = window.Bounds.Height;
        foreach (var track in audioTracks)
        {
            var strip = StripOf(view, track);
            OnScreen(Visible<PanKnob>(strip, $"{track} pan"));
            OnScreen(Visible<ToggleButton>(strip, $"Mute {track}"));
            OnScreen(Visible<TextBlock>(strip, "Track gain in decibels"));
            OnScreen(Visible<TextBlock>(strip, "Track peak in dBFS"));
            OnScreen(Visible<ConsoleFader>(strip, $"{track} gain"));
            OnScreen(Visible<LedLadderMeter>(strip, "Track level meter"));
            OnScreen(Assert.Single(strip.GetLogicalDescendants().OfType<Button>(), button =>
                button.IsEffectivelyVisible && AutomationProperties.GetName(button)?.StartsWith("Output: ", StringComparison.Ordinal) == true));
        }

        OnScreen(Visible<LedLadderMeter>(view, "Master level meter"));
        OnScreen(Visible<TextBlock>(view, "Master gain in decibels"));

        void OnScreen(Control control)
        {
            var top = control.TranslatePoint(new Point(0, 0), window) ?? throw new InvalidOperationException($"{control} is not in the window.");
            Assert.True(control.Bounds.Height > 0, $"{AutomationProperties.GetName(control)} should have height.");
            Assert.True(top.Y >= 0 && top.Y + control.Bounds.Height <= limit + 0.5,
                $"{AutomationProperties.GetName(control)} should be on screen: {top.Y}+{control.Bounds.Height} of {limit}.");
        }
    }

    [Fact]
    public Task MixerWorkspaceView_CanDetachAndDockTheSameConsole() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        ShowDockedMixer(app);
        var workspaceView = app.ViewModel.Workspace.Mixer;

        workspaceView.DetachCommand.Execute(null);
        app.Settle();

        Assert.True(workspaceView.IsDetached);
        Assert.Equal(LowerPane.Devices, app.ViewModel.LowerPane);
        var detached = Assert.Single(app.Window.OwnedWindows.OfType<ViewWindow>());
        var detachedMixer = AppUnderTest.Within<MixerConsoleView>(detached, _ => true);
        Assert.Same(app.ViewModel.MixerConsole, detachedMixer.DataContext);

        workspaceView.DockCommand.Execute(null);
        app.Settle();
        Assert.True(workspaceView.IsDocked);
        Assert.Empty(app.Window.OwnedWindows.OfType<ViewWindow>());

        workspaceView.ShowCommand.Execute(null);
        app.Settle();
        Assert.Equal(LowerPane.Mixer, app.ViewModel.LowerPane);
        Assert.Same(app.ViewModel.MixerConsole, AppUnderTest.Within<MixerConsoleView>(app.Window, _ => true).DataContext);
    });

    [Fact]
    public Task DetachedMixer_CommandZ_UndoesAndShiftCommandZ_Redoes() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, _) = await SeedAsync(app);
        ShowDockedMixer(app);
        app.ViewModel.Workspace.Mixer.DetachCommand.Execute(null);
        app.Settle();
        var detached = Assert.Single(app.Window.OwnedWindows.OfType<ViewWindow>());
        app.Session.Execute(AudioStripCommands.SetTrackGain(audio.Id, -10, "test-setup:gain"));
        app.Settle();
        Assert.Equal(-10, GainOf(app, audio.Id));

        // The keys go to the detached window, whose key route does not include the main window's bindings.
        detached.KeyPress(Key.Z, AppUnderTest.Command, PhysicalKey.None, null);
        detached.KeyRelease(Key.Z, AppUnderTest.Command, PhysicalKey.None, null);
        app.Settle();
        Assert.Equal(0, GainOf(app, audio.Id));

        detached.KeyPress(Key.Z, AppUnderTest.Command | RawInputModifiers.Shift, PhysicalKey.None, null);
        detached.KeyRelease(Key.Z, AppUnderTest.Command | RawInputModifiers.Shift, PhysicalKey.None, null);
        app.Settle();
        Assert.Equal(-10, GainOf(app, audio.Id));
    });

    private static void AssertPeakHoldMarkersAreRendered(AppUnderTest app, IReadOnlyList<LedLadderMeter> faces, int minimumVisibleMarkers)
    {
        app.Window.UpdateLayout();
        using var frame = app.Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        var pixels = Pixels(frame);
        var size = frame.PixelSize;
        var stride = size.Width * 4;
        var markerFaces = 0;
        foreach (var face in faces)
        {
            var origin = face.TranslatePoint(new Avalonia.Point(0, 0), app.Window);
            if (!face.IsEffectivelyVisible || origin is null
                || origin.Value.X < 0 || origin.Value.Y < 0
                || origin.Value.X + face.Bounds.Width > size.Width
                || origin.Value.Y + face.Bounds.Height > size.Height)
            {
                continue;
            }

            var left = (int)Math.Floor(origin.Value.X);
            var top = (int)Math.Floor(origin.Value.Y);
            var right = Math.Min(size.Width, (int)Math.Ceiling(origin.Value.X + face.Bounds.Width));
            var bottom = Math.Min(size.Height, (int)Math.Ceiling(origin.Value.Y + face.Bounds.Height));
            var hasPeakHoldColor = false;
            for (var y = top; y < bottom && !hasPeakHoldColor; y++)
            {
                for (var x = left; x < right; x++)
                {
                    var offset = (y * stride) + (x * 4);
                    // A held cell is its gradient colour lifted toward white: brighter than any lit cell.
                    if (pixels[offset] + pixels[offset + 1] + pixels[offset + 2] >= 560)
                    {
                        hasPeakHoldColor = true;
                        break;
                    }
                }
            }

            if (hasPeakHoldColor)
            {
                markerFaces++;
            }
        }

        Assert.True(markerFaces >= minimumVisibleMarkers,
            $"Expected at least {minimumVisibleMarkers} visible meter peak-hold markers, found {markerFaces}.");
    }

    private static byte[] Pixels(Bitmap frame)
    {
        var size = frame.PixelSize;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            frame.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        return pixels;
    }
}
