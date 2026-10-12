using Bluestone.Application.Sessions;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Files;
using Bluestone.Midi.Timing;
using Bluestone.Presentation;
using Bluestone.Profiles;

namespace Bluestone.Tests.Unit.Presentation;

public sealed class MainViewModelTests : IAsyncLifetime
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bluestone-vm-").FullName;
    private readonly VirtualClock _clock = new(TimeSpan.FromSeconds(5));
    private readonly FakeUi _ui = new();
    private readonly LoopbackMidiProvider _provider;
    private readonly EndpointDirectory _directoryOfEndpoints;
    private readonly ProjectSession _session = new();
    private readonly MainViewModel _vm;
    private readonly LoopbackPort _synth;

    public MainViewModelTests()
    {
        _provider = new LoopbackMidiProvider(_clock);
        _synth = _provider.CreatePort("Synth", "synth");
        _directoryOfEndpoints = new EndpointDirectory([_provider]);
        var catalog = ProfileCatalog.Empty.Add("gm", ProfileLoader.Load("""
            { "format": "bluestone-device-profile", "schemaVersion": 1, "id": "test.gm", "version": "1", "name": "Test GM",
              "notices": ["Not affiliated."],
              "provenance": { "sources": ["t"], "contributors": ["t"], "license": "MIT", "redistributionConfirmed": true, "verification": "unverified" },
              "banks": [ { "id": "main", "name": "Main", "msb": 0, "lsb": 0, "programs": [ { "number": 1, "name": "Piano" } ] } ],
              "sysex": [ { "id": "reset", "name": "Reset", "effect": "reset", "bytes": "F0 7E 7F 09 01 F7" } ],
              "parameterMap": [ { "id": "cutoff", "name": "Filter Cutoff", "kind": "nrpn", "msb": 1, "lsb": 32 } ],
              "initialization": [ { "sysex": "reset" } ] }
            """u8));
        var playback = new PlaybackController(_session, _directoryOfEndpoints, catalog, _clock, startThread: false);
        _vm = new MainViewModel(_session, playback, _directoryOfEndpoints, _ui, new ImmediateDispatcher());
    }

    public async ValueTask InitializeAsync() => await _vm.InitializeAsync();

    public async ValueTask DisposeAsync()
    {
        await _vm.DisposeAsync();
        _directoryOfEndpoints.Dispose();
        _provider.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<string> WriteDemoMidiAsync()
    {
        var track = Track.FromEvents(TrackId.New(), "Piano", [new NoteEvent(Tick.Zero, new TickSpan(480), MidiChannel.FromIndex(0), NoteNumber.MiddleC, Velocity.Max)]);
        var path = Path.Combine(_directory, "demo.mid");
        await File.WriteAllBytesAsync(path, SmfWriter.Write(SmfExporter.Export(Sequence.CreateEmpty(new Ppqn(480)).WithTrack(track)).File), TestContext.Current.CancellationToken);
        return path;
    }

    private async Task<TrackViewModel> ImportAsync()
    {
        _ui.NextOpenPath = await WriteDemoMidiAsync();
        await _vm.ImportMidiCommand.ExecuteAsync(null);
        await Settle();
        return Assert.Single(_vm.Tracks);
    }

    private static Task Settle() => Task.Delay(20, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ImportMidi_ShowsTracksAsOfflineUntilRouted()
    {
        var track = await ImportAsync();

        Assert.Equal("demo", _vm.ProjectName);
        Assert.Equal("Piano", track.Name);
        Assert.True(track.IsOffline);
        Assert.Equal("Offline", track.HealthText);
        Assert.Contains(_vm.Messages, m => m.Text.Contains("Choose an output", StringComparison.Ordinal));
        Assert.Same(track, _vm.SelectedTrack);
    }

    [Fact]
    public async Task ChoosingAnOutput_RoutesTheTrackAndIsUndoable()
    {
        var track = await ImportAsync();

        _vm.Selection.Output = _vm.Selection.OutputChoices.Single(o => o.Name == "Synth");
        await Settle();

        Assert.True(track.IsReady);
        Assert.Equal("No profile → Synth", track.RouteText);
        _vm.UndoCommand.Execute(null);
        await Settle();
        Assert.True(_vm.Tracks.Single().IsOffline);
    }

    [Fact]
    public async Task DisconnectedOutput_StaysVisibleAsAPlaceholder()
    {
        var track = await ImportAsync();
        _vm.Selection.Output = _vm.Selection.OutputChoices.Single(o => o.Name == "Synth");
        await Settle();

        _provider.RemovePort("synth");
        await Settle();

        var current = _vm.Tracks.Single();
        Assert.True(current.IsOffline);
        Assert.False(current.Output.IsAvailable);
        Assert.Equal("Synth", current.Output.Name);
        Assert.Equal("Disconnected", current.Output.Detail);
        Assert.Same(_vm.Selection.Output, _vm.Selection.OutputChoices.Last());
        Assert.Equal("Disconnected", _vm.Selection.Output!.Detail);
    }

    [Fact]
    public async Task ChoosingAProfile_OffersItsVoicesAndNotices()
    {
        var track = await ImportAsync();

        var selection = _vm.Selection;
        Assert.False(selection.CanEditVoice);
        selection.Profile = selection.ProfileChoices.Single(p => p.Name == "Test GM");
        await Settle();
        selection.VoiceBank = selection.Banks.Single(b => b.Name == "Main");
        await Settle();

        Assert.True(selection.CanEditVoice);
        Assert.Equal(["Not affiliated."], selection.ProfileNotices);
        Assert.Equal(128, selection.Programs.Count);
        Assert.Equal("1 · Piano", selection.Programs[0].Name);
        Assert.Equal("Program 2", selection.Programs[1].Name);
        Assert.Equal(new ProgramNumber(0), selection.VoiceProgram!.Program);
        Assert.Equal("main", track.Route!.Voice!.BankId);
    }

    [Fact]
    public async Task InstrumentParameters_FromTheProfile_CanBeAutomatedByName()
    {
        var track = await ImportAsync();
        Assert.False(track.HasHardwareParameters);

        _vm.Selection.Output = _vm.Selection.OutputChoices.Single(o => o.Name == "Synth");
        await Settle();
        _vm.Selection.Profile = _vm.Selection.ProfileChoices.Single(p => p.Name == "Test GM");
        await Settle();
        var current = _vm.Tracks.Single();
        var option = Assert.Single(current.HardwareParameters);
        option.Add.Execute(option);
        await Settle();

        Assert.Equal("Filter Cutoff", option.Name);
        Assert.Equal(("A", 1, "cutoff"), (option.Target.Port, option.Target.Channel.Number, option.Target.InstrumentParameter));
        var lane = Assert.Single(_vm.Tracks.Single().Lanes);
        Assert.Equal("Filter Cutoff · ch 1", lane.Name);
        Assert.Equal(option.Target, lane.Target);
        Assert.True(_vm.Tracks.Single().IsAutomationExpanded);
        _vm.UndoCommand.Execute(null);
        await Settle();
        Assert.Empty(_vm.Tracks.Single().Lanes);
    }

    [Fact]
    public async Task MuteAndRename_GoThroughUndoableCommands()
    {
        var track = await ImportAsync();

        track.IsMuted = true;
        track.Name = "Grand";
        await Settle();

        Assert.True(_session.Project.Sequence.Tracks.Single().IsMuted);
        Assert.Equal("Grand", _session.Project.Sequence.Tracks.Single().Name);
        _vm.UndoCommand.Execute(null);
        await Settle();
        Assert.Equal("Piano", _vm.Tracks.Single().Name);
    }

    [Fact]
    public async Task InitializeInstrument_AsksBeforeResetting()
    {
        var track = await ImportAsync();
        _vm.Selection.Output = _vm.Selection.OutputChoices.Single(o => o.Name == "Synth");
        await Settle();
        _vm.Selection.Profile = _vm.Selection.ProfileChoices.Single(p => p.Name == "Test GM");
        await Settle();

        _ui.ConfirmAnswer = false;
        await _vm.InitializeInstrumentCommand.ExecuteAsync(null);
        Assert.Empty(_synth.Sent);
        Assert.Contains("Reset", _ui.LastConfirmTitle, StringComparison.Ordinal);

        _ui.ConfirmAnswer = true;
        await _vm.InitializeInstrumentCommand.ExecuteAsync(null);
        Assert.Single(_synth.Sent);
    }

    [Fact]
    public async Task ToggleLoop_LoopsFourBarsThenClears()
    {
        await ImportAsync();

        _vm.ToggleLoopCommand.Execute(null);
        await Settle();
        Assert.True(_vm.IsLoopEnabled);
        Assert.Equal(new Tick(4 * 4 * 480), _session.Project.Loop!.End);

        _vm.ToggleLoopCommand.Execute(null);
        await Settle();
        Assert.False(_vm.IsLoopEnabled);
    }

    [Fact]
    public async Task DeletingATrackWithEvents_RequiresConfirmation()
    {
        await ImportAsync();

        _ui.ConfirmAnswer = false;
        await _vm.DeleteTracksCommand.ExecuteAsync(null);
        Assert.Single(_vm.Tracks);

        _ui.ConfirmAnswer = true;
        await _vm.DeleteTracksCommand.ExecuteAsync(null);
        await Settle();
        Assert.Empty(_vm.Tracks);
    }

    [Fact]
    public async Task UnsavedChanges_CancelKeepsTheProject()
    {
        await ImportAsync();
        _ui.SaveChoice = UnsavedChangesChoice.Cancel;

        await _vm.NewProjectCommand.ExecuteAsync(null);

        Assert.Single(_vm.Tracks);
        Assert.False(await _vm.ConfirmCloseAsync());

        _ui.SaveChoice = UnsavedChangesChoice.Discard;
        await _vm.NewProjectCommand.ExecuteAsync(null);
        await Settle();
        Assert.Empty(_vm.Tracks);
    }

    [Fact]
    public async Task SaveAs_WritesTheProjectAndClearsDirty()
    {
        await ImportAsync();
        _ui.NextSavePath = Path.Combine(_directory, "song.bluestone");

        await _vm.SaveCommand.ExecuteAsync(null);
        await Settle();

        Assert.True(File.Exists(_ui.NextSavePath));
        Assert.False(_vm.IsDirty);
        Assert.Equal("demo · Bluestone", _vm.WindowTitle);
    }

    [Fact]
    public async Task OnFrame_FormatsThePlayhead()
    {
        await ImportAsync();
        _vm.SeekTo(480 * 5);
        _vm.Selection.Output = _vm.Selection.OutputChoices.Single(o => o.Name == "Synth");

        _vm.OnFrame();

        Assert.Equal("2.2.000", _vm.PositionText);
        Assert.Equal("0:02.500", _vm.TimeText);
        Assert.Equal("120.0", _vm.TempoText);
        Assert.Equal("4/4", _vm.MeterText);
    }

    [Fact]
    public void NotesAt_LightsTheNotesSoundingAtTheTick()
    {
        static NoteEvent Note(long start, long length, int pitch) =>
            new(new Tick(start), new TickSpan(length), MidiChannel.FromIndex(0), new NoteNumber(pitch), Velocity.Max);
        var track = Track.FromEvents(TrackId.New(), "Piano", [Note(0, 960, 36), Note(0, 480, 60), Note(480, 480, 64), Note(960, 480, 127)]);

        Assert.Equal((UInt128.One << 36) | (UInt128.One << 60), MainViewModel.NotesAt(track, 0));
        Assert.Equal((UInt128.One << 36) | (UInt128.One << 64), MainViewModel.NotesAt(track, 480));
        Assert.Equal(UInt128.One << 127, MainViewModel.NotesAt(track, 960));
        Assert.Equal(UInt128.Zero, MainViewModel.NotesAt(track, 1440));
    }

    private async Task<List<TrackViewModel>> ThreeTracksAsync()
    {
        await ImportAsync();
        _vm.AddTrackCommand.Execute(null);
        _vm.AddTrackCommand.Execute(null);
        await Settle();
        return [.. _vm.Tracks];
    }

    [Fact]
    public async Task MultiSelection_ShowsMixedValuesAndAppliesChangesToAllInOneUndo()
    {
        var tracks = await ThreeTracksAsync();
        _vm.Select(tracks[0]);
        _vm.Selection.Output = _vm.Selection.OutputChoices.Single(o => o.Name == "Synth");
        await Settle();

        _vm.SelectAllTracksCommand.Execute(null);
        Assert.Equal("3 tracks", _vm.Selection.Title);
        Assert.True(_vm.Selection.Output!.IsMixed);

        _vm.Selection.Output = _vm.Selection.OutputChoices.Single(o => o.Name == "Synth");
        _vm.Selection.Channel = _vm.Selection.ChannelChoices.Single(c => c.Channel?.Number == 5);
        _vm.Selection.Transpose = 7;
        await Settle();

        Assert.All(_vm.Tracks, t => Assert.True(t.IsReady));
        Assert.All(_session.Project.Sequence.Tracks.Select(t => TrackOutputs.Read(_session.Project, t.Id)), r => Assert.Equal((5, 7), (r.Channel!.Value.Number, r.Transpose)));
        Assert.Equal("Transpose (3 Tracks)", _session.History.UndoLabel);

        _vm.UndoCommand.Execute(null);
        await Settle();
        Assert.Equal(0, _vm.Selection.Transpose);
        Assert.Equal(5, _vm.Selection.Channel!.Channel!.Value.Number);
    }

    [Fact]
    public async Task MultiSelection_WithDifferentProfiles_DisablesVoice()
    {
        var tracks = await ThreeTracksAsync();
        _vm.Select(tracks[0]);
        _vm.Selection.Profile = _vm.Selection.ProfileChoices.Single(p => p.Name == "Test GM");
        await Settle();

        _vm.SelectAllTracksCommand.Execute(null);

        Assert.True(_vm.Selection.Profile!.IsMixed);
        Assert.False(_vm.Selection.CanEditVoice);
        Assert.Contains("same installed profile", _vm.Selection.VoiceHint, StringComparison.Ordinal);
        Assert.False(_vm.Selection.CanInitialize);
    }

    [Fact]
    public async Task ToggleMuteAndSolo_ActOnTheSelection()
    {
        var tracks = await ThreeTracksAsync();
        _vm.Select(tracks[0]);
        _vm.SelectedTracks.Add(tracks[1]);

        _vm.ToggleMuteCommand.Execute(null);
        await Settle();
        Assert.Equal([true, true, false], _vm.Tracks.Select(t => t.IsMuted));

        _vm.ToggleMuteCommand.Execute(null);
        _vm.ToggleSoloCommand.Execute(null);
        await Settle();
        Assert.All(_vm.Tracks, t => Assert.False(t.IsMuted));
        Assert.Equal([true, true, false], _vm.Tracks.Select(t => t.IsSoloed));
    }

    [Fact]
    public async Task DeleteAndDuplicate_ActOnTheSelection()
    {
        var tracks = await ThreeTracksAsync();
        _vm.Select(tracks[1]);
        _vm.SelectedTracks.Add(tracks[2]);

        _vm.DuplicateTracksCommand.Execute(null);
        await Settle();
        Assert.Equal(["Piano", "Track 2", "Track 2 copy", "Track 3", "Track 3 copy"], _vm.Tracks.Select(t => t.Name));

        _vm.Select(_vm.Tracks[0]);
        _vm.SelectedTracks.Add(_vm.Tracks[1]);
        await _vm.DeleteTracksCommand.ExecuteAsync(null);
        await Settle();
        Assert.Equal(["Track 2 copy", "Track 3", "Track 3 copy"], _vm.Tracks.Select(t => t.Name));
        Assert.Contains("2 tracks", _ui.LastConfirmMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectAdjacent_MovesOrExtendsTheSelection()
    {
        var tracks = await ThreeTracksAsync();
        _vm.Select(tracks[0]);

        _vm.SelectAdjacent(1, extend: false);
        Assert.Equal([tracks[1]], _vm.SelectedTracks);
        _vm.SelectAdjacent(1, extend: true);
        Assert.Equal([tracks[1], tracks[2]], _vm.SelectedTracks);
        _vm.SelectAdjacent(5, extend: false);
        Assert.Equal([tracks[2]], _vm.SelectedTracks);
    }

    [Fact]
    public async Task BarNavigation_MovesByWholeBars()
    {
        await ImportAsync();
        _vm.SeekTo(1920 + 100);

        _vm.PreviousBarCommand.Execute(null);
        Assert.Equal(1920, _vm.PlayheadTick);
        _vm.PreviousBarCommand.Execute(null);
        Assert.Equal(0, _vm.PlayheadTick);
        _vm.NextBarCommand.Execute(null);
        _vm.NextBarCommand.Execute(null);
        Assert.Equal(3840, _vm.PlayheadTick);
        // The end of the song is the end of its last clip, on the bar line after the last note.
        _vm.GoToEndCommand.Execute(null);
        Assert.Equal(1920, _vm.PlayheadTick);
    }

    [Theory]
    [InlineData("song.bluestone", true)]
    [InlineData("SONG.MID", true)]
    [InlineData("tune.midi", true)]
    [InlineData("notes.txt", false)]
    [InlineData("noextension", false)]
    public void CanOpenFile_AcceptsProjectsAndMidiFiles(string path, bool expected) =>
        Assert.Equal(expected, MainViewModel.CanOpenFile(path));

    [Fact]
    public async Task DroppedMidiFile_IsImported()
    {
        await _vm.OpenFileAsync(await WriteDemoMidiAsync());
        await Settle();

        Assert.Equal("Piano", Assert.Single(_vm.Tracks).Name);
    }

    [Fact]
    public async Task DroppedProject_IsOpenedAfterAskingAboutUnsavedChanges()
    {
        await ImportAsync();
        var path = Path.Combine(_directory, "dropped.bluestone");
        await new ProjectSession().SaveAsync(path, TestContext.Current.CancellationToken);

        _ui.SaveChoice = UnsavedChangesChoice.Cancel;
        await _vm.OpenFileAsync(path);
        Assert.Single(_vm.Tracks);

        _ui.SaveChoice = UnsavedChangesChoice.Discard;
        await _vm.OpenFileAsync(path);
        await Settle();
        Assert.Empty(_vm.Tracks);
        Assert.Equal("Untitled", _vm.ProjectName);
    }

    [Fact]
    public async Task DroppedUnsupportedFile_IsExplained()
    {
        await _vm.OpenFileAsync(Path.Combine(_directory, "readme.txt"));

        Assert.Contains(_vm.Messages, m => m.Severity == MessageSeverity.Warning && m.Text.Contains("not a Bluestone project or MIDI file", StringComparison.Ordinal));
    }

    [Fact]
    public void Zoom_StepsAndStaysInRange()
    {
        _vm.ZoomInCommand.Execute(null);
        Assert.Equal(50, _vm.Zoom, precision: 6);
        _vm.ZoomOutCommand.Execute(null);
        Assert.Equal(40, _vm.Zoom, precision: 6);

        _vm.ZoomBy(1000);
        Assert.Equal(MainViewModel.MaxZoom, _vm.Zoom);
        _vm.Zoom = 0;
        Assert.Equal(MainViewModel.MinZoom, _vm.Zoom);
    }

    [Fact]
    public async Task Errors_AreReportedAsMessagesNotThrown()
    {
        var bad = Path.Combine(_directory, "broken.mid");
        await File.WriteAllTextAsync(bad, "not midi", TestContext.Current.CancellationToken);
        _ui.NextOpenPath = bad;

        await _vm.ImportMidiCommand.ExecuteAsync(null);

        Assert.Contains(_vm.Messages, m => m.Severity == MessageSeverity.Error && m.Source == "Import");
    }

    /// <summary>Inserts a device into the active Device View, as Return in the Browser does.</summary>
    private void AddDevice(string name) =>
        Assert.True(_vm.Browser.InsertIntoActive(_vm.Browser.Items.Single(i => i.Name == name)));

    private async Task<(TrackViewModel First, TrackViewModel Second)> TwoTracksAsync()
    {
        var first = await ImportAsync();
        _session.Execute(Bluestone.Application.Editing.ProjectCommands.AddTrack(Track.FromEvents(TrackId.New(), "Strings", [new NoteEvent(Tick.Zero, new TickSpan(480), MidiChannel.FromIndex(1), NoteNumber.MiddleC, Velocity.Max)])));
        await Settle();
        _vm.Select(first);
        await Settle();
        return (first, _vm.Tracks[1]);
    }

    [Fact]
    public async Task TheDeviceView_AddsReordersBypassesAndEditsDevices_ThroughTheProject()
    {
        var track = await ImportAsync();
        var strip = _vm.DeviceView!;
        Assert.True(strip.HasChain);
        Assert.True(strip.IsEmpty);

        AddDevice("Transpose");
        AddDevice("Arpeggiator");
        await Settle();

        Assert.Equal(["Transpose", "Arpeggiator"], strip.Devices.Select(d => d.Name));
        var transpose = strip.Devices[0];
        transpose.Parameters.Single(p => p.Name == "Semitones").Value = 12;
        strip.Devices[1].MoveLeftCommand.Execute(null);
        await Settle();

        Assert.Equal(["Arpeggiator", "Transpose"], strip.Devices.Select(d => d.Name));
        Assert.Equal(12, TrackOutputs.Read(_session.Project, track.Id).Transpose);
        Assert.Equal(12m, _vm.Selection.Transpose);

        strip.Devices[0].IsBypassed = true;
        await Settle();
        Assert.True(_session.Project.ChainOf(track.Id)!.Devices[0].IsBypassed);
        Assert.Equal("Bypassed", strip.Devices[0].Status);

        strip.Devices[0].RemoveCommand.Execute(null);
        await Settle();
        Assert.Equal(["Transpose"], strip.Devices.Select(d => d.Name));
        _vm.UndoCommand.Execute(null);
        await Settle();
        Assert.Equal(2, strip.Devices.Count);
    }

    [Fact]
    public async Task ARoutingIndicator_OpensTheRoutingInspector_AndShowsWhereTheDeviceSends()
    {
        var (first, second) = await TwoTracksAsync();
        var strip = _vm.DeviceView!;
        AddDevice("Arpeggiator");
        await Settle();
        var shown = false;
        _vm.Connections.ShowRequested += (_, _) => shown = true;

        strip.Devices[0].ShowRoutingCommand.Execute(null);
        _vm.Connections.Destination = _vm.Connections.Destinations.Single(d => d.Name == "Track: Strings");
        _vm.Connections.ConnectCommand.Execute(null);
        await Settle();

        Assert.True(shown);
        Assert.Equal("After Arpeggiator", _vm.Connections.Source!.Name);
        Assert.Equal("→ track \"Strings\"", strip.Devices[0].RoutingIndicator);
        Assert.Contains("After Arpeggiator", Assert.Single(_vm.Connections.Outgoing).Text, StringComparison.Ordinal);
        Assert.Equal(first.Id, _session.Project.ChainOf(first.Id)!.Owner.Track);
        Assert.Equal(SignalNode.Track(second.Id), Assert.Single(_session.Project.Connections, c => c.Source.Kind == SignalNodeKind.Device).Destination);
    }

    [Fact]
    public async Task TheRoutingInspector_RefusesFeedback_WithAMessage()
    {
        var (first, second) = await TwoTracksAsync();
        _vm.Connections.Destination = _vm.Connections.Destinations.Single(d => d.Name == "Track: Strings");
        _vm.Connections.ConnectCommand.Execute(null);
        await Settle();
        _vm.Select(second);
        await Settle();
        Assert.Contains("Piano", Assert.Single(_vm.Connections.Incoming).Text, StringComparison.Ordinal);

        _vm.Connections.Destination = _vm.Connections.Destinations.Single(d => d.Name == "Track: Piano");
        _vm.Connections.ConnectCommand.Execute(null);
        await Settle();

        Assert.Single(_session.Project.Connections, c => c.Destination.Kind == SignalNodeKind.Track);
        Assert.Contains(_vm.Messages, m => m.Text.Contains("feed back", StringComparison.Ordinal));
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task ChangingTheRole_IsAppliedOrExplained()
    {
        var track = await ImportAsync();
        Assert.Equal(TrackRole.Instrument, _vm.Selection.Role);

        _vm.Selection.Role = TrackRole.Audio;
        await Settle();
        Assert.Equal(TrackRole.Instrument, _session.Project.Sequence.FindTrack(track.Id)!.Role);
        Assert.Contains(_vm.Messages, m => m.Text.Contains("holds note clips", StringComparison.Ordinal));

        _vm.Selection.Role = TrackRole.Hybrid;
        await Settle();
        Assert.Equal(TrackRole.Hybrid, _session.Project.Sequence.FindTrack(track.Id)!.Role);
        Assert.Equal(TrackRole.Hybrid, track.Role);
    }

    [Fact]
    public async Task AChainPreset_SavedFromOneTrack_LoadsAsNewDevicesOnAnother()
    {
        var (first, second) = await TwoTracksAsync();
        var strip = _vm.DeviceView!;
        AddDevice("Arpeggiator");
        await Settle();
        _ui.NextSavePath = Path.Combine(_directory, "Arp.bluestone-chain");
        await strip.SavePresetCommand.ExecuteAsync(null);

        _vm.Select(second);
        await Settle();
        _ui.NextOpenPath = _ui.NextSavePath;
        await strip.LoadPresetCommand.ExecuteAsync(null);
        await Settle();

        var original = Assert.Single(_session.Project.ChainOf(first.Id)!.Devices);
        var loaded = Assert.Single(_session.Project.ChainOf(second.Id)!.Devices);
        Assert.Equal(original.Definition, loaded.Definition);
        Assert.NotEqual(original.Id, loaded.Id);
        Assert.Contains(_vm.Messages, m => m.Text.Contains("as \"Arp\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARack_IsCreatedEditedInTheDeviceView_FedByATrack_AndRemoved()
    {
        var track = await ImportAsync();

        _vm.Racks.NewRackCommand.Execute(null);
        await Settle();
        var rack = Assert.Single(_session.Project.Chains, c => c.Owner.Kind == Bluestone.Domain.Devices.ChainOwnerKind.Rack);
        Assert.True(_vm.DeviceView!.IsEditingRack);
        Assert.True(_vm.DeviceView.IsLocked);
        Assert.Equal("Rack: Rack 1", _vm.DeviceView.Heading);
        Assert.Equal("Rack 1 output", _vm.Connections.Sources[0].Name);

        AddDevice("Transpose");
        await Settle();
        Assert.Single(_session.Project.FindChain(rack.Id)!.Devices);
        Assert.Null(_session.Project.ChainOf(track.Id));

        _vm.DeviceView.BackToTrackCommand.Execute(null);
        await Settle();
        _vm.Connections.Destination = _vm.Connections.Destinations.Single(d => d.Name == "Rack: Rack 1");
        _vm.Connections.ConnectCommand.Execute(null);
        await Settle();
        Assert.Equal("1 device · 1 input", Assert.Single(_vm.Racks.Racks).Detail);

        _vm.Racks.Racks[0].Name = "XG";
        await Settle();
        Assert.Equal("XG", _session.Project.FindChain(rack.Id)!.Name);

        _vm.Racks.Racks[0].RemoveCommand.Execute(null);
        await Settle();
        Assert.Empty(_vm.Racks.Racks);
        Assert.DoesNotContain(_session.Project.Connections, c => c.Destination.Kind == SignalNodeKind.Rack);
    }

    [Fact]
    public async Task MixerChannels_AreEdited_AFaderDragIsOneUndoStep_AndALoopIsRefused()
    {
        await ImportAsync();
        _vm.Mixer.NewChannelCommand.Execute(null);
        _vm.Mixer.NewChannelCommand.Execute(null);
        await Settle();
        var (first, second) = (_vm.Mixer.Channels[0], _vm.Mixer.Channels[1]);

        first.Gain = -3;
        first.Gain = -6;
        first.Gain = -9;
        await Settle();
        Assert.Equal(-9, _session.Project.Mixer.Channels[0].GainDecibels);
        _vm.UndoCommand.Execute(null);
        await Settle();
        Assert.Equal(0, _session.Project.Mixer.Channels[0].GainDecibels);

        first.Output = first.Outputs.Single(o => o.Name == "Channel 2");
        await Settle();
        second.Output = second.Outputs.Single(o => o.Name == "Channel 1");
        await Settle();

        Assert.Null(_session.Project.Mixer.Channels[1].Output);
        Assert.Equal("Master", _vm.Mixer.Channels[1].Output!.Name);
        Assert.Contains(_vm.Messages, m => m.Text.Contains("feed back", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DraggingAParameterSlider_IsOneUndoStep()
    {
        await ImportAsync();
        AddDevice("Transpose");
        await Settle();
        var semitones = _vm.DeviceView!.Devices[0].Parameters.Single();

        foreach (var value in new[] { 1, 2, 3, 4, 5 })
        {
            semitones.Value = value;
        }

        await Settle();
        Assert.Equal(5, semitones.Value);
        _vm.UndoCommand.Execute(null);
        await Settle();
        Assert.Equal(0, _vm.DeviceView!.Devices[0].Parameters.Single().Value);
        Assert.Equal("Add Device", _session.History.UndoLabel);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();
    }

    private sealed class FakeUi : IUserInteraction
    {
        public string? NextOpenPath { get; set; }

        public string? NextSavePath { get; set; }

        public bool ConfirmAnswer { get; set; } = true;

        public string? LastConfirmTitle { get; private set; }

        public string LastConfirmMessage { get; private set; } = string.Empty;

        public UnsavedChangesChoice SaveChoice { get; set; } = UnsavedChangesChoice.Discard;

        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult(NextOpenPath);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, FileFilter filter) => Task.FromResult(NextSavePath);

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive)
        {
            LastConfirmTitle = title;
            LastConfirmMessage = message;
            return Task.FromResult(ConfirmAnswer);
        }

        public Task<UnsavedChangesChoice> AskToSaveChangesAsync(string projectName) => Task.FromResult(SaveChoice);
    }
}
