using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Presentation;
using Bluestone.Profiles;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The inspector's Track, Routing, and Voice sections in the real window: choices made with the mouse and values typed
/// with the keyboard reach the project's routing model (ADR 0023), and a refused change is explained.
/// </summary>
public sealed class TrackInspectorTests
{
    private static readonly ProfileCatalog Profiles = ProfileCatalog.Empty.Add("gm", ProfileLoader.Load("""
        { "format": "bluestone-device-profile", "schemaVersion": 1, "id": "test.gm", "version": "1", "name": "Test GM",
          "provenance": { "sources": ["t"], "contributors": ["t"], "license": "MIT", "redistributionConfirmed": true, "verification": "unverified" },
          "banks": [ { "id": "main", "name": "Main", "msb": 0, "lsb": 0, "programs": [ { "number": 1, "name": "Piano" }, { "number": 5, "name": "Strings" } ] } ] }
        """u8));

    internal static ProjectSession Tracks(params string[] names)
    {
        var session = new ProjectSession();
        foreach (var name in names)
        {
            session.Execute(ProjectCommands.AddTrack(Track.FromEvents(TrackId.New(), name, [new NoteEvent(Tick.Zero, new TickSpan(240), MidiChannel.FromNumber(1), new NoteNumber(60), new Velocity(100))])));
        }

        return session;
    }

    /// <summary>Clicks a track's row in the track list, as a musician selects a track.</summary>
    internal static void SelectTrack(AppUnderTest app, int index)
    {
        var list = app.Find<ListBox>("TrackList");
        var row = list.ContainerFromIndex(index) ?? throw new InvalidOperationException($"Track row {index} is not realized.");
        app.ClickAt(row, new Point(14, 20));
        Assert.Same(app.ViewModel.Tracks[index], app.ViewModel.SelectedTrack);
    }

    [Fact]
    public Task OutputInstrumentChannelAndVoice_ChosenWithTheMouse_AreTheTracksConnectionToAnInstrument() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(Tracks("Lead"), profiles: Profiles);
        SelectTrack(app, 0);
        var routing = app.Section("External MIDI");
        Assert.False(routing.IsExpanded);
        routing.IsExpanded = true;
        app.Settle();
        var lead = app.Session.Project.Sequence.Tracks[0].Id;

        app.Choose(AppUnderTest.ComboNamed(routing, "MIDI output"), o => o is OutputOption { Name: "Synth" });
        app.Choose(AppUnderTest.ComboNamed(routing, "Instrument profile"), o => o is ProfileOption { Name: "Test GM" });
        app.Choose(AppUnderTest.ComboNamed(routing, "Channel"), o => o is ChannelOption { Channel.Number: 3 });
        Assert.Equal("Synth · Ch 3 · Test GM", app.ViewModel.Selection.HardwareSummary);
        var voice = routing;
        app.Choose(AppUnderTest.ComboNamed(voice, "Voice bank"), o => o is BankOption { Id: "main" });
        app.Choose(AppUnderTest.ComboNamed(voice, "Voice program"), o => o is ProgramOption { Program.Number: 5 });

        var project = app.Session.Project;
        var connection = Assert.Single(project.Connections);
        Assert.Equal(SignalNode.Track(lead), connection.Source);
        var instrument = project.Instruments.Single(i => SignalNode.ExternalPart(i.Id, i.Ports[0].Id) == connection.Destination);
        Assert.Equal(app.Port.OutputId.Value, instrument.Ports[0].Endpoint?.EndpointKey);
        Assert.Equal("test.gm", instrument.Profile?.ProfileId);
        Assert.Equal(MidiChannel.FromNumber(3), connection.Mapping.Force);
        Assert.Equal("main", connection.Voice?.BankId);
        Assert.Equal(5, connection.Voice?.Program.Number);
        Assert.Equal("Synth", app.ViewModel.Tracks[0].Output?.Name);
        app.SaveFrame("track-routing");

        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();

        // One step back: the bank as first chosen, with its first program.
        Assert.Equal(1, app.Session.Project.Connections.Single().Voice?.Program.Number);
    });

    [Fact]
    public Task Transpose_TypedIntoTheInspector_IsATransposeDeviceInTheTracksChain() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(Tracks("Lead"));
        SelectTrack(app, 0);
        app.Section("External MIDI").IsExpanded = true;
        app.Settle();
        var transpose = AppUnderTest.Within<NumericUpDown>(app.Section("External MIDI"), n => Avalonia.Automation.AutomationProperties.GetName(n) == "Transpose in semitones");
        var field = transpose.GetVisualDescendants().OfType<TextBox>().First();

        app.Click(field);
        app.Press(Key.A, AppUnderTest.Command);
        app.Type("-5");
        app.Press(Key.Enter);

        var chain = app.Session.Project.ChainOf(app.Session.Project.Sequence.Tracks[0].Id);
        var device = Assert.Single(chain!.Devices);
        Assert.True(BuiltInDevices.IsTranspose(device));
        Assert.Equal(-5, BuiltInDevices.TransposeOf(device));
        Assert.Equal(-5, app.ViewModel.Tracks[0].Route.Transpose);
        Assert.Equal(-5m, transpose.Value);
    });

    [Fact]
    public Task Role_ShownForAnInstrumentTrack_IsChosenWithTheMouse_ARefusedRoleIsExplained_AndAHybridTrackLeavesItToTheContextMenu() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(Tracks("Lead"));
        SelectTrack(app, 0);
        var role = AppUnderTest.ComboNamed(app.Section("Track"), "Track role");
        Assert.Equal(TrackRole.Instrument, role.SelectedItem);
        Assert.True(role.IsEffectivelyVisible);

        app.Choose(role, r => r is TrackRole.Effect);

        Assert.Equal(TrackRole.Instrument, app.Session.Project.Sequence.Tracks[0].Role);
        var message = Assert.Single(app.ViewModel.Messages, m => m.Source == "Track Role");
        Assert.Equal(message.Text, app.Find<TextBlock>("LastMessage").Text);
        Assert.Equal(TrackRole.Instrument, role.SelectedItem);
        app.SaveFrame("track-role-refused");

        app.Choose(role, r => r is TrackRole.Hybrid);

        Assert.Equal(TrackRole.Hybrid, app.Session.Project.Sequence.Tracks[0].Role);
        Assert.False(role.IsEffectivelyVisible);

        // The track's context menu still offers every role.
        app.ViewModel.Tracks[0].SetRoleCommand.Execute(TrackRole.Instrument);
        app.Settle();
        Assert.Equal(TrackRole.Instrument, app.Session.Project.Sequence.Tracks[0].Role);
        Assert.True(role.IsEffectivelyVisible);
    });
}
