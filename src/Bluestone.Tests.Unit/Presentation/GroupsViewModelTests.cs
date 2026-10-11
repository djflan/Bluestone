using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Presentation;
using Bluestone.Profiles;
using Bluestone.Signal;

namespace Bluestone.Tests.Unit.Presentation;

/// <summary>Grouping the selected tracks, membership in the track rows, folding, and group mute (ADR 0030).</summary>
public sealed class GroupsViewModelTests : IAsyncLifetime
{
    private readonly VirtualClock _clock = new(TimeSpan.FromSeconds(5));
    private readonly LoopbackMidiProvider _provider;
    private readonly EndpointDirectory _endpoints;
    private readonly ProjectSession _session = new();
    private readonly MainViewModel _vm;
    private readonly LoopbackPort _synth;

    public GroupsViewModelTests()
    {
        _provider = new LoopbackMidiProvider(_clock);
        _synth = _provider.CreatePort("Synth", "synth");
        _endpoints = new EndpointDirectory([_provider]);
        var playback = new PlaybackController(_session, _endpoints, ProfileCatalog.Empty, _clock, startThread: false);
        _vm = new MainViewModel(_session, playback, _endpoints, new NoUi(), new ImmediateDispatcher());
    }

    private GroupsViewModel Groups => _vm.Groups;

    public async ValueTask InitializeAsync()
    {
        await _vm.InitializeAsync();
        var synth = new TrackOutput { Endpoint = new EndpointReference(LoopbackMidiProvider.ProviderId, _synth.OutputId.Value, "Synth") };
        foreach (var (name, note) in new[] { ("Bass", 40), ("Keys", 60), ("Lead", 72) })
        {
            var track = Track.FromEvents(TrackId.New(), name, [new NoteEvent(Tick.Zero, new TickSpan(240), MidiChannel.FromIndex(0), new NoteNumber(note), new Velocity(100))]);
            _session.Execute(ProjectCommands.AddTrack(track));
            _session.Execute(ProjectCommands.SetTrackOutput(track.Id, synth));
        }

        await Settle();
    }

    public async ValueTask DisposeAsync()
    {
        await _vm.DisposeAsync();
        _endpoints.Dispose();
        _provider.Dispose();
    }

    private static Task Settle() => Task.Delay(20, TestContext.Current.CancellationToken);

    private TrackViewModel Row(string name) => _vm.Tracks.Single(t => t.Name == name);

    private void SelectRows(params string[] names)
    {
        _vm.SelectedTracks.Clear();
        foreach (var name in names)
        {
            _vm.SelectedTracks.Add(Row(name));
        }
    }

    private async Task<TrackViewModel> GroupBassAndKeysAsync()
    {
        SelectRows("Bass", "Keys");
        Groups.GroupSelectedCommand.Execute(null);
        await Settle();
        return Row("Group 1");
    }

    [Fact]
    public async Task GroupSelected_MakesAGroupAboveTheTracks_SelectsIt_AndShowsMembership()
    {
        var group = await GroupBassAndKeysAsync();

        Assert.Equal(["Group 1", "Bass", "Keys", "Lead"], _vm.Tracks.Select(t => t.Name));
        Assert.Same(group, _vm.SelectedTrack);
        Assert.True(group.IsGroup);
        Assert.Equal("2 tracks", group.MembersText);
        Assert.Equal("Group 1", Row("Bass").GroupName);
        Assert.Equal("→ Group 1", Row("Bass").StatusLine);
        Assert.Equal(GroupsViewModel.IndentPerLevel, Row("Keys").Indent);
        Assert.False(Row("Lead").IsInGroup);
        Assert.Equal([group], Groups.Available);
        Assert.Equal("Group Tracks", _session.History.UndoLabel);
    }

    [Fact]
    public async Task Folding_HidesTheMembersRows_MovesTheSelectionToTheGroup_AndIsUndoable()
    {
        var group = await GroupBassAndKeysAsync();
        SelectRows("Keys");

        group.IsFolded = true;
        await Settle();

        Assert.True(_session.Project.Sequence.FindTrack(group.Id)!.IsFolded);
        Assert.False(Row("Bass").IsShown);
        Assert.Equal(0, Row("Bass").RowHeight);
        Assert.True(Groups.HiddenTracks.SetEquals([Row("Bass").Id, Row("Keys").Id]));
        Assert.Same(group, _vm.SelectedTrack);

        _vm.UndoCommand.Execute(null);
        await Settle();

        Assert.False(group.IsFolded);
        Assert.True(Row("Bass").IsShown);
        Assert.Empty(Groups.HiddenTracks);
    }

    [Fact]
    public async Task SelectingTheNextTrack_SkipsFoldedRows()
    {
        var group = await GroupBassAndKeysAsync();
        group.IsFolded = true;
        await Settle();

        _vm.SelectAdjacent(1, extend: false);

        Assert.Equal("Lead", _vm.SelectedTrack!.Name);
    }

    [Fact]
    public async Task MutingTheGroup_SilencesItsMembers_AndMarksTheirRows()
    {
        var group = await GroupBassAndKeysAsync();

        group.IsMuted = true;
        await Settle();

        Assert.True(Row("Bass").IsMutedByGroup);
        Assert.False(Row("Bass").IsMuted);
        Assert.False(Row("Lead").IsMutedByGroup);
        var sequence = _session.Project.Sequence;
        Assert.False(GroupTracks.Plays(sequence, sequence.FindTrack(Row("Keys").Id)!));
        Assert.True(GroupTracks.Plays(sequence, sequence.FindTrack(Row("Lead").Id)!));
    }

    [Fact]
    public async Task AddToGroup_AndRemoveFromGroup_ActOnTheSelection()
    {
        var group = await GroupBassAndKeysAsync();
        SelectRows("Lead");

        Groups.AddSelectedToCommand.Execute(group);
        await Settle();

        Assert.Equal("Group 1", Row("Lead").GroupName);
        Assert.True(Groups.CanRemoveSelected);
        Assert.Equal("3 tracks", group.MembersText);

        Groups.RemoveSelectedCommand.Execute(null);
        await Settle();

        Assert.False(Row("Lead").IsInGroup);
        Assert.False(Groups.CanRemoveSelected);
        Assert.Equal("Synth", Row("Lead").StatusLine);
    }

    [Fact]
    public async Task ARefusedGrouping_IsExplainedInAMessage()
    {
        var group = await GroupBassAndKeysAsync();
        _session.Execute(RoutingCommands.Connect(SignalConnection.Create(SignalKind.Events, SignalNode.Track(group.Id), SignalNode.Track(Row("Lead").Id)), DeviceCatalog.BuiltIn.Lookup));
        SelectRows("Lead");

        Groups.AddSelectedToCommand.Execute(group);
        await Settle();

        Assert.False(Row("Lead").IsInGroup);
        Assert.Contains(_vm.Messages, m => m.Source == "Add To Group" && m.Text.Contains("feed back", StringComparison.Ordinal));
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();
    }

    private sealed class NoUi : IUserInteraction
    {
        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, FileFilter filter) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive) => Task.FromResult(true);

        public Task<UnsavedChangesChoice> AskToSaveChangesAsync(string projectName) => Task.FromResult(UnsavedChangesChoice.Discard);
    }
}
