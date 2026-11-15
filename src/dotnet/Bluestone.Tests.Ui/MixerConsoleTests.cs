using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Bluestone.Application.Editing;
using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Desktop.Views;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
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
        await Task.Delay(20, TestContext.Current.CancellationToken);
        app.Settle();
        return (audio, midi, effect);
    }

    private static MixerConsoleView ShowDockedMixer(AppUnderTest app)
    {
        app.ViewModel.Workspace.Mixer.ShowCommand.Execute(null);
        app.Settle();
        return AppUnderTest.Within<MixerConsoleView>(app.Window, _ => true);
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

        Assert.Equal(2, mixer.GetLogicalDescendants().OfType<Slider>().Count(slider =>
            slider.IsEffectivelyVisible && AutomationProperties.GetName(slider) == "Track audio gain"));
        foreach (var track in new[] { audio, effect })
        {
            var strip = Assert.Single(mixer.GetLogicalDescendants().OfType<Border>(), border =>
                AutomationProperties.GetName(border)?.StartsWith($"{track.Name},", StringComparison.Ordinal) == true);
            Assert.Single(strip.GetLogicalDescendants().OfType<Slider>(), slider =>
                slider.IsEffectivelyVisible && AutomationProperties.GetName(slider) == "Track audio gain");
        }

        var midiStrip = Assert.Single(mixer.GetLogicalDescendants().OfType<Border>(), border =>
            AutomationProperties.GetName(border)?.StartsWith($"{midi.Name},", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(midiStrip.GetLogicalDescendants().OfType<Slider>(), slider =>
            slider.IsEffectivelyVisible && AutomationProperties.GetName(slider) == "Track audio gain");
        Assert.Equal(2, mixer.GetLogicalDescendants().OfType<Slider>().Count(slider =>
            slider.IsEffectivelyVisible && AutomationProperties.GetName(slider) == "Track audio pan"));
        Assert.Single(mixer.GetLogicalDescendants().OfType<Slider>(), slider =>
            slider.IsEffectivelyVisible && AutomationProperties.GetName(slider) == "Master audio gain");
        Assert.Single(mixer.GetLogicalDescendants().OfType<Slider>(), slider =>
            slider.IsEffectivelyVisible && AutomationProperties.GetName(slider) == "Master audio pan");
        Assert.Equal(2, mixer.GetLogicalDescendants().OfType<ToggleButton>().Count(button =>
            button.IsEffectivelyVisible && AutomationProperties.GetName(button) == "Mute track audio"));
        Assert.Equal(2, mixer.GetLogicalDescendants().OfType<ToggleButton>().Count(button =>
            button.IsEffectivelyVisible && AutomationProperties.GetName(button) == "Solo track audio"));
        Assert.Single(mixer.GetLogicalDescendants().OfType<ToggleButton>(), button =>
            button.IsEffectivelyVisible && AutomationProperties.GetName(button) == "Mute master audio");
        Assert.Single(mixer.GetLogicalDescendants().OfType<ToggleButton>(), button =>
            button.IsEffectivelyVisible && AutomationProperties.GetName(button) == "Solo master audio");
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
            AutomationProperties.GetName(button) == "Select Master in Inspector"));
        app.Settle();

        Assert.Empty(app.ViewModel.SelectedTracks);
        Assert.True(app.ViewModel.MixerConsole.Master.IsSelected);
        Assert.True(app.ViewModel.Selection.IsMasterSelected);

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
            AutomationProperties.GetName(button) == "Select Master in Inspector"));
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
    public Task MixerMeterFaces_ShowHeldPeakMarkersOnBarAndRoundScales() => HeadlessApp.RunAsync(async () =>
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

        var expectedLeftHold = MeterReading.ToDecibels(0.9f);
        var expectedRightHold = MeterReading.ToDecibels(0.8f);
        var faces = mixer.GetLogicalDescendants().OfType<MixerMeterFace>().ToArray();
        var targetFaces = faces.Where(face => face.IsEffectivelyVisible).ToArray();
        Assert.True(targetFaces.Length >= 2, "The visible track and Master meter faces should both be present.");
        Assert.All(targetFaces, face =>
        {
            Assert.Equal(expectedLeftHold, face.LeftPeakHold, 5);
            Assert.Equal(expectedRightHold, face.RightPeakHold, 5);
        });

        app.Window.Width = 1100;
        app.Window.Height = 720;
        app.ViewModel.MixerConsole.MeterPresentation = MixerMeterPresentation.Bar;
        app.Settle();
        Assert.True(File.Exists(app.SaveFrame("mixer-meter-peak-hold-bar-compact")));
        AssertPeakHoldMarkersAreRendered(app, faces, minimumVisibleMarkers: 2);

        reading = new MeterReading(0.001f, 0.001f, 0.001f, 0.001f);
        app.ViewModel.MixerConsole.RefreshMeters(timestamp + 2);
        app.Settle();
        Assert.All(faces.Where(face => face.IsEffectivelyVisible), face =>
        {
            Assert.Equal(face.PeakFloor, face.LeftValue, 5);
            Assert.Equal(face.PeakFloor, face.RightValue, 5);
            Assert.True(face.LeftPeakHold > face.PeakFloor);
            Assert.True(face.RightPeakHold > face.PeakFloor);
        });
        Assert.True(File.Exists(app.SaveFrame("mixer-meter-peak-hold-bar-at-floor-compact")));
        AssertPeakHoldMarkersAreRendered(app, faces, minimumVisibleMarkers: 2);

        app.ViewModel.MixerConsole.MeterPresentation = MixerMeterPresentation.Round;
        app.Settle();
        Assert.True(File.Exists(app.SaveFrame("mixer-meter-peak-hold-round-compact")));
        AssertPeakHoldMarkersAreRendered(app, faces, minimumVisibleMarkers: 2);

        app.Window.Width = 1440;
        app.Window.Height = 900;
        app.ViewModel.MixerConsole.MeterPresentation = MixerMeterPresentation.Bar;
        app.Settle();
        Assert.True(File.Exists(app.SaveFrame("mixer-meter-peak-hold-bar-normal")));
        AssertPeakHoldMarkersAreRendered(app, faces, minimumVisibleMarkers: 3);
        app.ViewModel.MixerConsole.MeterPresentation = MixerMeterPresentation.Round;
        app.Settle();
        Assert.True(File.Exists(app.SaveFrame("mixer-meter-peak-hold-round-normal")));
        AssertPeakHoldMarkersAreRendered(app, faces, minimumVisibleMarkers: 3);
    });

    [Fact]
    public Task TrackAudioFader_MovedSeveralTimes_IsOneUndoStepAndShowsItsValue() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, _) = await SeedAsync(app);
        var mixer = ShowDockedMixer(app);
        var fader = AppUnderTest.Within<Slider>(mixer, slider =>
            AutomationProperties.GetName(slider) == "Track audio gain" && slider.IsEffectivelyVisible);
        var row = app.ViewModel.MixerConsole.Tracks.Single(strip => strip.Id == audio.Id);

        foreach (var value in new[] { -2.0, -4.0, -6.0 })
        {
            fader.Value = value;
            app.Settle();
        }

        Assert.Equal(-6, app.Session.Project.Sequence.FindTrack(audio.Id)!.AudioStrip!.GainDecibels);
        Assert.Equal("-6.0 dB", row.GainText);
        app.SaveFrame("track-owned-mixer-fader");

        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();

        Assert.Equal(0, app.Session.Project.Sequence.FindTrack(audio.Id)!.AudioStrip!.GainDecibels);
        Assert.Equal(0, fader.Value);
    });

    [Fact]
    public Task AudioSendAddedFromTrackStrip_IsRoutedToEffectAndUndoable() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var (audio, _, effect) = await SeedAsync(app);
        var mixer = ShowDockedMixer(app);
        var source = app.ViewModel.MixerConsole.Tracks.Single(row => row.Id == audio.Id);
        AppUnderTest.Within<Expander>(mixer, expander => Equals(expander.Header, "Sends")).IsExpanded = true;
        app.Settle();

        app.Choose(AppUnderTest.ComboNamed(mixer, "New send destination"), item =>
            item is MixerEffectOption option && option.Track == effect.Id);
        app.Click(AppUnderTest.Within<Button>(mixer, button => Equals(button.Content, "Add")));

        var send = Assert.Single(app.Session.Project.Connections, connection => connection.AudioPurpose == AudioRoutePurpose.Send);
        Assert.Equal(SignalNode.Track(effect.Id), send.Destination);
        Assert.Single(source.Sends);
        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.DoesNotContain(app.Session.Project.Connections, connection => connection.AudioPurpose == AudioRoutePurpose.Send);
        Assert.Empty(source.Sends);
    });

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

    private static void AssertPeakHoldMarkersAreRendered(AppUnderTest app, IReadOnlyList<MixerMeterFace> faces, int minimumVisibleMarkers)
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
                    if (pixels[offset] >= 210 && pixels[offset + 1] >= 210 && pixels[offset + 2] >= 210)
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
