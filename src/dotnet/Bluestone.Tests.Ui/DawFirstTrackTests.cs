using Avalonia.Controls;
using Avalonia.VisualTree;
using Bluestone.Application.Editing;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The DAW-first path in the real window: a new track is ready for an instrument, the inspector's "Add an instrument…" opens the
/// Browser on instruments aimed at that track, the inserted plugin is heard at the master and named in the track header and the
/// inspector, and external MIDI stays one deliberate step away, opening by itself for a track that uses hardware.
/// </summary>
public sealed class DawFirstTrackTests
{
    private static readonly PluginIdentity Sine = new("bluestone-reference", "bluestone.reference", "reference.sine", "Reference Sine", "Bluestone", PluginKind.Instrument, "1.0.0");

    private static PluginHostManager Plugins()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "plugin-data", Guid.NewGuid().ToString("N"));
        return new PluginHostManager(new PluginHostOptions { DataDirectory = directory, RequestTimeout = TimeSpan.FromSeconds(15) });
    }

    [Fact]
    public Task ANewTrack_TakesAnInstrumentFromTheInspector_AndIsHeardAtTheMaster_WithNoRoutingOrRoleToChoose() => HeadlessApp.RunAsync(async () =>
    {
        await using var plugins = Plugins();
        await using var app = await AppUnderTest.OpenAsync(plugins: s => new PluginDeviceHost(plugins, s, [Sine]));
        app.Window.Width = 1440;
        app.Window.Height = 900;
        app.ViewModel.Workspace.Browser.CollapseCommand.Execute(null);
        app.Settle();

        app.ViewModel.AddTrackCommand.Execute(null);
        app.Settle();

        var track = app.Session.Project.Sequence.Tracks.Single();
        Assert.Equal(TrackRole.Hybrid, track.Role);
        Assert.Equal("No instrument", app.ViewModel.Tracks[0].StatusLine);
        Assert.Empty(app.ViewModel.Selection.Problems);
        Assert.False(app.Find<ItemsControl>("ProblemList").IsEffectivelyVisible);
        Assert.False(app.ViewModel.Selection.ShowsRole);
        var hardware = app.Find<Expander>("HardwareSection");
        Assert.False(hardware.IsExpanded);
        Assert.Equal("Off", app.Find<TextBlock>("HardwareSummary").Text);

        app.Click(app.Find<Button>("AddInstrumentButton"));

        Assert.True(app.ViewModel.Workspace.Browser.IsDocked);
        Assert.Equal(BrowserCategory.Instruments, app.ViewModel.Browser.SelectedCategory.Category);
        var sine = Assert.Single(app.ViewModel.Browser.Items);
        Assert.True(sine.IsRecommended);
        Assert.Equal(track.Name, app.ViewModel.DeviceView?.Heading);
        Assert.Equal($"Inserts into {track.Name}", app.ViewModel.Browser.TargetText);

        Assert.True(app.ViewModel.Browser.InsertIntoActive(sine));
        app.Settle();
        await AppUnderTest.WaitUntilAsync(() => app.ViewModel.Tracks[0].StatusLine == Sine.DisplayName, "the track header to name its instrument");

        var audio = Assert.Single(app.Session.Project.Connections);
        Assert.Equal((SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Master), (audio.Kind, audio.Source, audio.Destination));
        Assert.True(app.ViewModel.Tracks[0].IsReady);
        Assert.Equal(Sine.DisplayName, app.ViewModel.Selection.Track.InstrumentText);
        Assert.True(app.Find<Button>("InstrumentButton").IsEffectivelyVisible);
        Assert.Equal("1 device", app.ViewModel.Selection.Track.Sound.DevicesText);
        Assert.Equal("Master", app.ViewModel.Selection.Track.AudioDestination?.Name);
        app.SaveFrame("daw-first-instrument-added");

        // One undo removes the device; the track keeps the audio path it was created with.
        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.Empty(app.Session.Project.ChainOf(track.Id)?.Devices ?? []);
        Assert.Single(app.Session.Project.Connections);
    });

    [Fact]
    public Task TheAudioDestination_IsChosenInTheInspector_AsOneUndoStep() => HeadlessApp.RunAsync(async () =>
    {
        var session = new ProjectSession();
        session.Execute(RoutingCommands.AddMixerChannel(Domain.Mixing.MixerChannel.Create("Bus")));
        await using var app = await AppUnderTest.OpenAsync(session);
        app.ViewModel.AddTrackCommand.Execute(null);
        app.Settle();
        var track = app.Session.Project.Sequence.Tracks.Single().Id;

        app.Choose(AppUnderTest.ComboNamed(app.Section("Track"), "Audio output"), o => o is AudioDestinationOption { Name: "Bus" });

        var bus = app.Session.Project.Mixer.Channels.Single().Id;
        Assert.Equal(SignalNode.Mixer(bus), app.Session.Project.Connections.Single(c => c.Source == SignalNode.Track(track)).Destination);
        Assert.Equal("Bus", app.ViewModel.Tracks[0].Sound.AudioDestination);

        app.Choose(AppUnderTest.ComboNamed(app.Section("Track"), "Audio output"), o => o is AudioDestinationOption { Destination: null, IsMixed: false });
        Assert.DoesNotContain(app.Session.Project.Connections, c => c.Source == SignalNode.Track(track));

        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();
        Assert.Equal(SignalNode.Mixer(bus), app.Session.Project.Connections.Single(c => c.Source == SignalNode.Track(track)).Destination);
    });

    [Fact]
    public Task ExternalMidi_OpensByItselfForATrackThatUsesHardware_AndKeepsTheMusiciansChoiceUntilTheSelectionChanges() => HeadlessApp.RunAsync(async () =>
    {
        var session = new ProjectSession();
        var notes = new[] { new NoteEvent(Tick.Zero, new TickSpan(240), MidiChannel.FromNumber(1), new NoteNumber(60), new Velocity(100)) };
        session.Execute(ProjectCommands.AddTrack(Track.FromEvents(TrackId.New(), "Hardware", notes)));
        session.Execute(ProjectCommands.AddTrack(Track.Create("Plugin", TrackRole.Hybrid)));
        await using var app = await AppUnderTest.OpenAsync(session);
        var section = app.Find<Expander>("HardwareSection");
        app.ViewModel.Select(app.ViewModel.Tracks[0]);
        app.Settle();
        Assert.False(section.IsExpanded);
        app.Click(section.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(t => t.Name == "ExpanderHeader"));
        Assert.True(section.IsExpanded);
        app.Choose(AppUnderTest.ComboNamed(section, "MIDI output"), o => o is OutputOption { Name: "Synth" });

        app.ViewModel.Select(app.ViewModel.Tracks[1]);
        app.Settle();
        Assert.False(section.IsExpanded);
        app.ViewModel.Select(app.ViewModel.Tracks[0]);
        app.Settle();
        Assert.True(section.IsExpanded);
        Assert.Equal("Synth", app.Find<TextBlock>("HardwareSummary").Text);
        Assert.Equal("Synth", app.ViewModel.Tracks[0].StatusLine);

        // Closed by hand, it stays closed while the same track is selected, even as the project changes.
        section.IsExpanded = false;
        app.Settle();
        app.Choose(AppUnderTest.ComboNamed(app.Section("Track"), "Audio output"), o => o is AudioDestinationOption { Name: "Master" });
        Assert.False(section.IsExpanded);
    });
}
