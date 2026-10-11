using System.Diagnostics;
using Bluestone.Application.Audio;
using Bluestone.Application.Editing;
using Bluestone.Application.Live;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Audio;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Midi.Wire;
using Bluestone.Playback;
using Bluestone.Plugins;
using Bluestone.Profiles;
using Bluestone.Signal;
using Bluestone.Tests.Integration.Plugins;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Audio;

/// <summary>
/// Live input through plugins in real worker processes (ADR 0035): a plugin MIDI effect's own instance transforms the
/// notes and a plugin instrument plays them through the audio engine, driven by the offline driver on a virtual clock
/// that both the live input and the audio share, so latency is measured exactly.
/// </summary>
[Collection(PluginProcessTests.Name)]
public sealed class LiveInputPluginTests
{
    private const int Rate = 48_000;
    private const int Block = 256;
    private static readonly TimeSpan Origin = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ANoteThroughAPluginMidiEffect_IntoAPluginInstrument_IsHeardTransposed_AfterTheMeasuredLatency()
    {
        await using var host = new PluginTestHost();
        await using var rig = await Rig.CreateAsync(host, transpose: 7);
        rig.Driver.Render(4 * Block, Block);

        // The key is played 5 ms before the next block is heard (as if 5 ms of audio were queued).
        var heard = Stopwatch.GetElapsedTime(0, rig.Driver.NextTimestamp);
        rig.Clock.Advance(heard - TimeSpan.FromMilliseconds(5) - rig.Clock.Now);
        Assert.True(rig.Live.Post(rig.Track, ChannelMessage.NoteOn(MidiChannel.FromIndex(0), new NoteNumber(60), new Velocity(127)), rig.Clock.Now));
        var left = Left(rig.Driver.Render(Rate / 2, Block));

        // In the next block the effect gets the key; a block later the instrument gets its note; a block after that it sounds.
        var first = Array.FindIndex(left, s => s != 0f);
        Assert.InRange(first, 2 * Block, (2 * Block) + 2);
        var latency = rig.Live.Latency.Read();
        Assert.Equal(1, latency.Count);
        var expected = TimeSpan.FromMilliseconds(5) + TimeSpan.FromSeconds(2.0 * Block / Rate);
        Assert.InRange(latency.Min, expected - TimeSpan.FromMicroseconds(1), expected + TimeSpan.FromMicroseconds(1));

        // G4 (392 Hz): about 39 upward zero crossings in 0.1 s.
        var crossings = 0;
        for (var i = 4800; i < 4800 + (Rate / 10); i++)
        {
            crossings += left[i - 1] < 0 && left[i] >= 0 ? 1 : 0;
        }

        Assert.InRange(crossings, 38, 40);

        rig.Live.Post(rig.Track, ChannelMessage.NoteOff(MidiChannel.FromIndex(0), new NoteNumber(60), Velocity.DefaultRelease), rig.Clock.Now);
        rig.Driver.Render(8 * Block, Block);
        Assert.All(rig.Driver.Render(4 * Block, Block), s => Assert.Equal(0f, s));
    }

    [Fact]
    public async Task BypassingTheEffect_WhileAKeyIsHeld_ReleasesTheInstrumentsNote()
    {
        await using var host = new PluginTestHost();
        await using var rig = await Rig.CreateAsync(host, transpose: 12);
        rig.Driver.Render(2 * Block, Block);
        rig.Live.Post(rig.Track, ChannelMessage.NoteOn(MidiChannel.FromIndex(0), new NoteNumber(60), new Velocity(127)), rig.Clock.Now);
        Assert.Contains(rig.Driver.Render(8 * Block, Block), s => s != 0f);

        rig.Session.Execute(DeviceCommands.SetBypassed(rig.Effect.Id, true, rig.Devices.Lookup));
        rig.Update();

        // The route changed: the note the old route sounds is released, though the key is still down.
        rig.Driver.Render(6 * Block, Block);
        Assert.All(rig.Driver.Render(8 * Block, Block), s => Assert.Equal(0f, s));
        Assert.Equal(LiveTargetKind.Software, rig.Live.PlanOf(rig.Track)!.Target);
        Assert.Empty(rig.Live.PlanOf(rig.Track)!.Stages);
    }

    [Fact]
    public async Task ThroughAnArpeggiator_IntoAPluginInstrument_StepsLandOnTheirSampleInTheAudioBlock()
    {
        await using var host = new PluginTestHost();
        await using var rig = await Rig.CreateAsync(host, transpose: 0, arpeggiator: BuiltInDevices.CreateArpeggiator(rate: 2, gate: 0.25));
        rig.Driver.Render(2 * Block, Block);

        // 480 PPQN at 120 BPM (the audio transport's tempo while it plays): sixteenths are 120 ticks, 6000 frames.
        rig.Engine.Transport.Play(0);
        rig.Live.Post(rig.Track, ChannelMessage.NoteOn(MidiChannel.FromIndex(0), new NoteNumber(69), new Velocity(127)), rig.Clock.Now);
        var left = Left(rig.Driver.Render(Rate, Block));

        // Each step starts sounding where its frame falls, after the effect's and the instrument's pipeline blocks.
        var starts = Enumerable.Range(1, left.Length - 1).Where(i => left[i - 1] == 0f && left[i] != 0f && left[(i - 2)..i].All(s => s == 0f)).Take(4).ToList();
        Assert.Equal(4, starts.Count);
        Assert.All(starts.Zip(starts.Skip(1)), p => Assert.InRange(p.Second - p.First, 5999, 6001));
    }

    [Fact]
    public async Task AKilledEffectWorker_ReleasesTheNoteItPutOutToAnExternalInstrument()
    {
        await using var host = new PluginTestHost();
        var clock = new VirtualClock(Origin);
        using var provider = new LoopbackMidiProvider(clock);
        var synth = provider.CreatePort("Synth", "synth");
        using var directory = new EndpointDirectory([provider]);
        var session = new ProjectSession();
        var track = Track.FromEvents(TrackId.New(), "Lead", []);
        session.Execute(ProjectCommands.AddTrack(track));
        session.Execute(ProjectCommands.SetTrackOutput(track.Id, new TrackOutput { Endpoint = new EndpointReference(LoopbackMidiProvider.ProviderId, synth.OutputId.Value, synth.Name) }));
        var effect = DeviceInstance.Create(PluginDeviceHost.DefinitionOf(Transpose).ToReference()).WithParameter(new ParameterId(0), ControlValue.FromFraction(NormalizedFor(5)));
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Transpose], Rate, Block);
        var devices = bridge.Extend(DeviceCatalog.BuiltIn);
        session.Execute(DeviceCommands.InsertDevice(track.Id, effect, devices.Lookup, 0));
        await bridge.SyncAsync(session.Project, Ct);
        var instance = bridge.InstanceOf(effect.Id)!;
        host.Track(instance);
        await using var controller = new PlaybackController(session, directory, ProfileCatalog.Empty, clock, startThread: false);
        controller.UseDevices(devices);
        controller.Live.UsePlugins(bridge.InstanceOf);
        await controller.RefreshAsync(Ct);

        controller.Live.Post(track.Id, ChannelMessage.NoteOn(MidiChannel.FromIndex(0), new NoteNumber(60), new Velocity(100)), clock.Now);
        Pump(controller, clock, milliseconds: 10);
        Assert.Equal(["904164"], synth.Sent.Select(m => Convert.ToHexString(m.Bytes)));

        KillProcess(instance.WorkerProcessId!.Value);
        await WaitForStateAsync(instance, PluginInstanceState.Unavailable);
        Pump(controller, clock, milliseconds: 10);
        controller.Live.Post(track.Id, ChannelMessage.NoteOff(MidiChannel.FromIndex(0), new NoteNumber(60), Velocity.DefaultRelease), clock.Now);
        Pump(controller, clock, milliseconds: 10);

        // The effect's note is released when its worker dies; the key's own release then finds nothing sounding.
        Assert.Equal(["904164", "804140"], synth.Sent.Select(m => Convert.ToHexString(m.Bytes)));
    }

    [Fact]
    public async Task AKilledInstrumentWorker_IsSilent_AndAfterARestartNothingHangs()
    {
        await using var host = new PluginTestHost(o => o with { CollectWait = TimeSpan.FromMilliseconds(5) });
        await using var rig = await Rig.CreateAsync(host, transpose: 0);
        rig.Live.Post(rig.Track, ChannelMessage.NoteOn(MidiChannel.FromIndex(0), new NoteNumber(60), new Velocity(127)), rig.Clock.Now);
        Assert.Contains(rig.Driver.Render(10 * Block, Block), s => s != 0f);

        KillProcess(rig.Instrument.WorkerProcessId!.Value);
        await WaitForStateAsync(rig.Instrument, PluginInstanceState.Unavailable);
        Assert.All(rig.Driver.Render(4 * Block, Block), s => Assert.Equal(0f, s));

        await rig.Instrument.RestartAsync(Ct);
        host.Track(rig.Instrument);
        rig.Live.Post(rig.Track, ChannelMessage.NoteOff(MidiChannel.FromIndex(0), new NoteNumber(60), Velocity.DefaultRelease), rig.Clock.Now);
        rig.Driver.Render(8 * Block, Block);
        Assert.All(rig.Driver.Render(8 * Block, Block), s => Assert.Equal(0f, s));

        rig.Live.Post(rig.Track, ChannelMessage.NoteOn(MidiChannel.FromIndex(0), new NoteNumber(64), new Velocity(127)), rig.Clock.Now);
        Assert.Contains(rig.Driver.Render(10 * Block, Block), s => s != 0f);
    }

    [Fact]
    public async Task TwoTracksPlayingOnePluginInstrument_AreBothHeard_InTurnAndTogether()
    {
        // Track A has no instrument; its events go to track Z, which holds the Reference Sine.
        await using var host = new PluginTestHost();
        var session = new ProjectSession();
        var clock = new VirtualClock(Origin);
        var a = Domain.Sequencing.Track.FromEvents(TrackId.New(), "Keys", []);
        var z = Domain.Sequencing.Track.FromEvents(TrackId.New(), "Synth", []);
        var sine = DeviceInstance.Create(PluginDeviceHost.DefinitionOf(Sine).ToReference());
        var project = session.Project with
        {
            Sequence = new Sequence(TempoMap.Constant(new Ppqn(480), Tempo.Default), MeterMap.Constant(new Ppqn(480), TimeSignature.CommonTime), [a, z], []),
            Connections =
            [
                SignalConnection.Create(SignalKind.Events, SignalNode.Track(a.Id), SignalNode.Track(z.Id)),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(z.Id), SignalNode.Master),
            ],
        };
        project = project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(z.Id)) with { Devices = [sine] });
        session.Execute(new ProjectCommand("Set up", _ => project));
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Sine], Rate, Block);
        await bridge.SyncAsync(session.Project, Ct);
        host.Track(bridge.InstanceOf(sine.Id)!);
        var devices = bridge.Extend(DeviceCatalog.BuiltIn);
        using var playback = new PlaybackEngine(clock, project.Sequence.TempoMap);
        using var live = new LiveInput(playback, clock);
        live.UsePlugins(bridge.InstanceOf);
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        var driver = new OfflineAudioDriver(engine, serviceStreams: false);
        using var graphs = new ProjectAudioGraph(Rate, Block, bridge.InstanceOf);
        var result = graphs.Build(session.Project, SignalGraph.Evaluate(session.Project, devices), devices, null);
        engine.Publish(result.Graph);
        live.Update(session.Project, devices, (_, _) => null, new Dictionary<EndpointId, IMidiOutput>());
        live.UseInstruments(graphs.PluginNodes, node => result.Graph.CompensationOf(node) ?? 0);
        var channel = MidiChannel.FromIndex(0);
        float Peak() => Left(driver.Render(8 * Block, Block))[(4 * Block)..].Max();
        float Play(TrackId track, int note, int velocity)
        {
            Assert.True(live.Post(track, ChannelMessage.NoteOn(channel, new NoteNumber(note), new Velocity(velocity)), clock.Now));
            var peak = Peak();
            live.Post(track, ChannelMessage.NoteOff(channel, new NoteNumber(note), Velocity.DefaultRelease), clock.Now);
            driver.Render(8 * Block, Block);
            return peak;
        }

        Assert.True(Play(a.Id, 60, 127) > 0.2f);
        Assert.True(Play(z.Id, 64, 127) > 0.2f);
        Assert.True(Play(a.Id, 67, 127) > 0.2f);
        Assert.All(driver.Render(4 * Block, Block), s => Assert.Equal(0f, s));

        // Together: A at velocity 127 (peak 0.25) and Z at 64 (peak about 0.126).
        live.Post(a.Id, ChannelMessage.NoteOn(channel, new NoteNumber(60), new Velocity(127)), clock.Now);
        live.Post(z.Id, ChannelMessage.NoteOn(channel, new NoteNumber(72), new Velocity(64)), clock.Now);
        Assert.True(Peak() > 0.3f);
    }

    private static void Pump(PlaybackController controller, VirtualClock clock, int milliseconds)
    {
        for (var i = 0; i < milliseconds; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            controller.Live.Pump();
            controller.Engine.Pump();
        }
    }

    // The reference Transpose maps [0, 1] to -24..+24 semitones.
    private static double NormalizedFor(int semitones) => (semitones + 24) / 48.0;

    private static float[] Left(float[] interleaved) => [.. interleaved.Where((_, i) => i % 2 == 0)];

    /// <summary>
    /// A track with [arpeggiator?] → Reference Transpose → Reference Sine, its audio to the master, the project's audio
    /// graph in an engine driven offline, and live input on the same virtual clock.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private PluginDeviceHost _bridge = null!;
        private ProjectAudioGraph _graphs = null!;

        public ProjectSession Session { get; } = new();

        public VirtualClock Clock { get; } = new(Origin);

        public PlaybackEngine Playback { get; private set; } = null!;

        public LiveInput Live { get; private set; } = null!;

        public AudioEngine Engine { get; private set; } = null!;

        public OfflineAudioDriver Driver { get; private set; } = null!;

        public DeviceCatalog Devices { get; private set; } = null!;

        public TrackId Track { get; private set; }

        public DeviceInstance Effect { get; private set; } = null!;

        public PluginInstance Instrument { get; private set; } = null!;

        public static async Task<Rig> CreateAsync(PluginTestHost host, int transpose, DeviceInstance? arpeggiator = null)
        {
            var rig = new Rig();
            var track = Domain.Sequencing.Track.FromEvents(TrackId.New(), "Lead", []);
            rig.Track = track.Id;
            rig.Effect = DeviceInstance.Create(PluginDeviceHost.DefinitionOf(Transpose).ToReference()).WithParameter(new ParameterId(0), ControlValue.FromFraction(NormalizedFor(transpose)));
            var sine = DeviceInstance.Create(PluginDeviceHost.DefinitionOf(Sine).ToReference());
            DeviceInstance[] chain = arpeggiator is null ? [rig.Effect, sine] : [arpeggiator, rig.Effect, sine];
            var project = rig.Session.Project with
            {
                Sequence = new Sequence(TempoMap.Constant(new Ppqn(480), Tempo.Default), MeterMap.Constant(new Ppqn(480), TimeSignature.CommonTime), [track], []),
                Connections = [SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Master)],
            };
            project = project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(track.Id)) with { Devices = [.. chain] });
            rig.Session.Execute(new ProjectCommand("Set up", _ => project));
            rig._bridge = new PluginDeviceHost(host.Manager, rig.Session, [Sine, Transpose], Rate, Block);
            await rig._bridge.SyncAsync(rig.Session.Project, Ct);
            rig.Instrument = rig._bridge.InstanceOf(sine.Id)!;
            host.Track(rig.Instrument);
            host.Track(rig._bridge.InstanceOf(rig.Effect.Id)!);
            rig.Devices = rig._bridge.Extend(DeviceCatalog.BuiltIn);
            rig.Playback = new PlaybackEngine(rig.Clock, project.Sequence.TempoMap);
            rig.Live = new LiveInput(rig.Playback, rig.Clock);
            rig.Live.UsePlugins(rig._bridge.InstanceOf);
            rig.Engine = new AudioEngine(Rate, Block, streamFromDisk: false);
            rig.Driver = new OfflineAudioDriver(rig.Engine, serviceStreams: false) { StartTimestamp = (long)(Origin.TotalSeconds * Stopwatch.Frequency) };
            rig._graphs = new ProjectAudioGraph(Rate, Block, rig._bridge.InstanceOf);
            rig.Update();
            rig.Live.Monitored = rig.Track;
            return rig;
        }

        /// <summary>Rebuilds the audio graph and the live routes from the project, as a refresh does.</summary>
        public void Update()
        {
            var project = Session.Project;
            var result = _graphs.Build(project, SignalGraph.Evaluate(project, Devices), Devices, null);
            Engine.Publish(result.Graph);
            Live.Update(project, Devices, (_, _) => null, new Dictionary<EndpointId, IMidiOutput>());
            Live.UseInstruments(_graphs.PluginNodes, node => result.Graph.CompensationOf(node) ?? 0);
        }

        public async ValueTask DisposeAsync()
        {
            Live.Dispose();
            Engine.Dispose();
            _graphs.Dispose();
            Playback.Dispose();
            await _bridge.DisposeAsync();
        }
    }
}
