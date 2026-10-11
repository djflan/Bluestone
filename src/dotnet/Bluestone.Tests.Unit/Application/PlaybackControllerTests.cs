using Bluestone.Application.Editing;
using Bluestone.Application.Routing;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Profiles;

namespace Bluestone.Tests.Unit.Application;

public sealed class PlaybackControllerTests : IAsyncDisposable
{
    private readonly VirtualClock _clock = new(TimeSpan.FromSeconds(10));
    private readonly LoopbackMidiProvider _provider;
    private readonly EndpointDirectory _directory;
    private readonly ProjectSession _session = new();
    private readonly PlaybackController _controller;

    public PlaybackControllerTests()
    {
        _provider = new LoopbackMidiProvider(_clock);
        _directory = new EndpointDirectory([_provider]);
        _controller = new PlaybackController(_session, _directory, ProfileCatalog.Empty, _clock, startThread: false);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _controller.DisposeAsync();
        _directory.Dispose();
        _provider.Dispose();
    }

    private Track AddRoutedTrack(LoopbackPort port, params TrackEvent[] events)
    {
        var track = Track.FromEvents(TrackId.New(), "t", events);
        _session.Execute(ProjectCommands.AddTrack(track));
        _session.Execute(ProjectCommands.SetTrackOutput(track.Id, new TrackOutput { Endpoint = new EndpointReference(LoopbackMidiProvider.ProviderId, port.OutputId.Value, port.Name) }));
        return track;
    }

    private static NoteEvent Note(long at, int note = 60) => new(new Tick(at), new TickSpan(10), MidiChannel.FromIndex(0), new NoteNumber(note), Velocity.Max);

    [Fact]
    public async Task Play_ResolvesRoutesOpensOutputsAndPlays()
    {
        var port = _provider.CreatePort("Synth", "s");
        AddRoutedTrack(port, Note(0));

        await _controller.PlayAsync(Tick.Zero, Ct);
        _controller.Engine.Pump();

        Assert.Equal(EndpointBindingKind.Bound, Assert.Single(_controller.Tracks).Port!.Endpoint.Kind);
        Assert.Equal(2, port.Sent.Count);
    }

    [Fact]
    public async Task EditsWhilePlaying_AreHeardAfterRefresh()
    {
        var port = _provider.CreatePort("Synth", "s");
        var track = AddRoutedTrack(port);
        await _controller.PlayAsync(Tick.Zero, Ct);
        _controller.Engine.Pump();

        // 1000 ticks at 960 PPQN and 120 BPM is about 521 ms in.
        _session.Execute(ProjectCommands.AddEvent(track.Id, ClipId.New(), Note(1000, 64)));
        await _controller.RefreshAsync(Ct);
        _clock.Advance(TimeSpan.FromMilliseconds(530));
        _controller.Engine.Pump();

        Assert.Contains(port.Sent, m => m.Bytes[0] == 0x90 && m.Bytes[1] == 64);
    }

    [Fact]
    public async Task MissingEndpoint_IsReportedAndRecoversWhenItReturns()
    {
        var port = _provider.CreatePort("Synth", "s");
        AddRoutedTrack(port, Note(0));
        await _controller.RefreshAsync(Ct);

        _provider.RemovePort("s");
        await _controller.RefreshAsync(Ct);
        Assert.Equal(EndpointBindingKind.Missing, Assert.Single(_controller.Tracks).Port!.Endpoint.Kind);

        var replugged = _provider.CreatePort("Synth", "s");
        await _controller.PlayAsync(Tick.Zero, Ct);
        _controller.Engine.Pump();

        Assert.Equal(EndpointBindingKind.Bound, Assert.Single(_controller.Tracks).Port!.Endpoint.Kind);
        Assert.Equal(2, replugged.Sent.Count);
    }

    [Fact]
    public async Task InitializeInstrument_SendsOnlyConfirmedTemplates()
    {
        var port = _provider.CreatePort("Synth", "s");
        var track = AddRoutedTrack(port);
        var catalog = ProfileCatalog.Empty.Add("p", ProfileLoader.Load("""
            { "format": "bluestone-device-profile", "schemaVersion": 1, "id": "p.one", "version": "1", "name": "P",
              "provenance": { "sources": ["t"], "contributors": ["t"], "license": "MIT", "redistributionConfirmed": true, "verification": "unverified" },
              "sysex": [ { "id": "on", "name": "System On", "effect": "reset", "bytes": "F0 7E 7F 09 01 F7" } ],
              "initialization": [ { "sysex": "on" } ] }
            """u8));
        _controller.UseProfiles(catalog);
        _session.Execute(ProjectCommands.SetTrackOutput(track.Id, TrackOutputs.Read(_session.Project, track.Id) with { Profile = new ProfileReference("p.one") }));
        await _controller.RefreshAsync(Ct);

        Assert.Equal(0, await _controller.InitializeInstrumentAsync(track.Id, _ => Task.FromResult(false), Ct));
        Assert.Empty(port.Sent);

        Assert.Equal(1, await _controller.InitializeInstrumentAsync(track.Id, t => Task.FromResult(t.Name == "System On"), Ct));
        Assert.Equal([0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7], Assert.Single(port.Sent).Bytes);
    }

    [Fact]
    public async Task OpeningAProject_NeverSendsAnything()
    {
        var port = _provider.CreatePort("Synth", "s");
        AddRoutedTrack(port, Note(0));

        await _controller.RefreshAsync(Ct);
        _controller.Engine.Pump();

        Assert.Empty(port.Sent);
    }
}
