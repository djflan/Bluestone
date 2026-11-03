using Bluestone.Application.Audio;
using Bluestone.Application.Editing;
using Bluestone.Application.Engine;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Application.Status;
using Bluestone.Audio.Files;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Engine.Protocol;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Profiles;
using Bluestone.Signal;
using Bluestone.Tests.Integration.Plugins;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Engine;

/// <summary>
/// Live input into a plugin instrument rendered in the engine's device callback (ADR 0044 step 7), through a real worker:
/// a key played at a known time is heard at the first frame of the next block, with no pipeline block while the
/// instrument is monitored (depth 1) and one block later when it is not (depth 2); the graph's delay compensation follows;
/// the engine's latency meter reports the exact figure; and all of it comes back after the engine is killed. The engine's
/// audio device is the test stand-in, and the key is posted by the engine's own test render at a chosen callback.
/// </summary>
[Collection(EngineProcessTests.Name)]
public sealed class EngineLiveTests
{
    private const int Rate = 48_000;
    private const int Block = 256;
    private const int KeyFrame = 4 * Block;
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AKeyIntoAMonitoredInstrument_IsHeardAtTheNextBlocksFirstFrame_WithNoPipelineBlock_AndTheMeterReportsIt()
    {
        await using var host = new PluginTestHost();
        await using var rig = await Rig.StartAsync(host, withClip: false);

        await rig.MonitorAsync(true);
        Assert.Equal(0, rig.Audio.Stream!.LatencyFrames);
        var left = await rig.PlayAKeyAsync();
        Assert.InRange(FirstSound(left), KeyFrame, KeyFrame + 2);

        // Input to heard: the lead before the block, and nothing after it (no pipeline, no compensation, no device latency).
        await WaitUntilAsync(() => rig.Audio.LiveLatency.Count == 1, Patience, "the engine's live latency figure");
        var latency = rig.Audio.LiveLatency;
        Assert.InRange(latency.Min, Lead - TimeSpan.FromMicroseconds(1), Lead + TimeSpan.FromMicroseconds(1));
        Assert.Equal(latency.Min, latency.Max);

        // Not monitored, the instrument is back at depth 2: one pipeline block later, and the meter says so.
        await rig.MonitorAsync(false);
        Assert.Equal(Block, rig.Audio.Stream!.LatencyFrames);
        left = await rig.PlayAKeyAsync();
        Assert.InRange(FirstSound(left), KeyFrame + Block, KeyFrame + Block + 2);
        await WaitUntilAsync(() => rig.Audio.LiveLatency.Count == 2, Patience, "the second figure");
        Assert.InRange(rig.Audio.LiveLatency.Max, Lead + Seconds(Block) - TimeSpan.FromMicroseconds(1), Lead + Seconds(Block) + TimeSpan.FromMicroseconds(1));
    }

    [Fact]
    public async Task ThroughAPluginMidiEffect_TheKeyIsStillHeardAtTheNextBlocksFirstFrame_Transposed()
    {
        await using var host = new PluginTestHost();
        await using var rig = await Rig.StartAsync(host, withClip: false, transpose: 7);
        Assert.Single(rig.Controller.Live.PlanOf(rig.Synth)!.Stages);

        // The effect plays only live input, so the engine runs it at depth 1 too: it adds no block either.
        await rig.MonitorAsync(true);
        var left = await rig.PlayAKeyAsync();
        Assert.InRange(FirstSound(left), KeyFrame, KeyFrame + 2);

        // A4 + 7 semitones is E5 (659 Hz, not A4's 440 Hz): about 21 upward zero crossings in the 6 blocks (32 ms) after
        // the first, while the key is down.
        var crossings = 0;
        for (var i = KeyFrame + Block; i < KeyFrame + (7 * Block); i++)
        {
            crossings += left[i - 1] < 0 && left[i] >= 0 ? 1 : 0;
        }

        Assert.InRange(crossings, 20, 22);
    }

    [Fact]
    public async Task ARouteThroughAPluginMidiEffect_ToAnExternalInstrument_RunsInTheEngine()
    {
        await using var host = new PluginTestHost();
        await using var rig = await Rig.StartAsync(host, withClip: false, transpose: 5, external: true);
        var runner = rig.Controller.Live.RunnerOf(rig.Synth)!;
        Assert.True(runner.IsRemote);
        Assert.Equal(Application.Live.LiveTargetKind.External, runner.Plan.Target);

        Assert.True(rig.Controller.Live.Post(rig.Synth, Midi.Wire.ChannelMessage.NoteOn(Domain.Midi.MidiChannel.FromIndex(0), new Domain.Midi.NoteNumber(60), new Domain.Midi.Velocity(100)), SystemMonotonicClock.Instance.Now));
        await WaitUntilAsync(() => rig.Port!.Sent.Any(m => m.Bytes is [0x90, 65, 100]), Patience, "the transposed note from the engine");
        rig.Controller.Live.Post(rig.Synth, Midi.Wire.ChannelMessage.NoteOff(Domain.Midi.MidiChannel.FromIndex(0), new Domain.Midi.NoteNumber(60), Domain.Midi.Velocity.DefaultRelease), SystemMonotonicClock.Instance.Now);
        await WaitUntilAsync(() => rig.Port!.Sent.Any(m => m.Bytes is [0x80, 65, _] or [0x90, 65, 0]), Patience, "its release");
    }

    [Fact]
    public async Task TheGraphsCompensation_FollowsMonitoring_AsTheInstrumentsDepthChanges()
    {
        await using var host = new PluginTestHost();
        await using var rig = await Rig.StartAsync(host, withClip: true);

        // At depth 2 the clip is delayed by the instrument's pipeline block, so both line up. Each render outlasts the
        // half-second clip, so no clip audio is left in the compensation's delay line for the next.
        const int Blocks = 100;
        Assert.Equal(Block, rig.Audio.Stream!.LatencyFrames);
        Assert.InRange(FirstSound(await rig.RenderAsync([], Blocks)), Block, Block + 1);

        // Monitored, the instrument adds nothing, and nor does the compensation.
        await rig.MonitorAsync(true);
        Assert.Equal(0, rig.Audio.Stream!.LatencyFrames);
        Assert.InRange(FirstSound(await rig.RenderAsync([], Blocks)), 0, 1);

        await rig.MonitorAsync(false);
        Assert.Equal(Block, rig.Audio.Stream!.LatencyFrames);
        Assert.InRange(FirstSound(await rig.RenderAsync([], Blocks)), Block, Block + 1);
    }

    [Fact]
    public async Task AKilledEngine_IsRestarted_AndLiveInputIsHeardAgainAtDepth1()
    {
        await using var host = new PluginTestHost();
        await using var rig = await Rig.StartAsync(host, withClip: false);
        await rig.MonitorAsync(true);
        Assert.InRange(FirstSound(await rig.PlayAKeyAsync()), KeyFrame, KeyFrame + 2);
        var first = rig.Supervisor.ProcessId!.Value;

        KillProcess(first);
        await WaitUntilAsync(() => rig.Supervisor.State == EngineState.Ready && rig.Supervisor.ProcessId is { } next && next != first, Patience, "a new engine");
        await WaitUntilAsync(() => rig.Audio.IsRunning && rig.Audio.Stream?.LatencyFrames == 0, Patience, "the new engine's graph at depth 1");
        await rig.WaitForANewGraphAsync();

        Assert.InRange(FirstSound(await rig.PlayAKeyAsync()), KeyFrame, KeyFrame + 2);
        await WaitUntilAsync(() => rig.Audio.LiveLatency.Count == 1, Patience, "the new engine's live latency figure");
        Assert.InRange(rig.Audio.LiveLatency.Min, Lead - TimeSpan.FromMicroseconds(1), Lead + TimeSpan.FromMicroseconds(1));
    }

    private static TimeSpan Seconds(int frames) => TimeSpan.FromSeconds((double)frames / Rate);

    private static int FirstSound(float[] left) => Array.FindIndex(left, s => s != 0f);

    /// <summary>A track playing the reference sine (and maybe a clip track), the engine with its clients, and the plugin bridge.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _files;
        private readonly LoopbackMidiProvider _provider = new(SystemMonotonicClock.Instance);
        private readonly EndpointDirectory _endpoints;
        private readonly EnginePlayback _playback;
        private long _renders;

        private Rig(PluginTestHost host, bool withClip, int? transpose, bool external)
        {
            _files = Path.Combine(host.Directory, "engine-live");
            Directory.CreateDirectory(_files);
            var synth = Track.FromEvents(TrackId.New(), "Lead", []);
            var device = DeviceInstance.Create(PluginDeviceHost.DefinitionOf(Sine).ToReference());
            List<DeviceInstance> chain = [];
            if (transpose is { } semitones)
            {
                // The reference Transpose maps [0, 1] to -24..+24 semitones.
                chain.Add(DeviceInstance.Create(PluginDeviceHost.DefinitionOf(Transpose).ToReference()).WithParameter(new ParameterId(0), Domain.Midi.ControlValue.FromFraction((semitones + 24) / 48.0)));
            }

            if (!external)
            {
                chain.Add(device);
            }

            Synth = synth.Id;
            List<Track> tracks = [synth];
            List<SignalConnection> connections = [SignalConnection.Create(SignalKind.Audio, SignalNode.Track(synth.Id), SignalNode.Master)];
            if (withClip)
            {
                var clip = Path.Combine(_files, "tone.wav");
                using (var writer = new WaveFileWriter(clip, Rate, 2))
                {
                    var samples = new float[Rate / 2 * 2];
                    for (var i = 0; i < Rate / 2; i++)
                    {
                        samples[2 * i] = samples[(2 * i) + 1] = (float)(0.4 * Math.Sin(2 * Math.PI * 330 * i / Rate));
                    }

                    writer.Write(samples);
                }

                var audio = Track.Create("Clip", TrackRole.Audio).WithClip(new AudioClip(ClipId.New(), Tick.Zero, new TickSpan(480), TickSpan.Zero, new AudioSource(clip, Rate / 2, Rate, 2)));
                tracks.Add(audio);
                connections.Add(SignalConnection.Create(SignalKind.Audio, SignalNode.Track(audio.Id), SignalNode.Master));
            }

            var project = Session.Project with
            {
                Sequence = new Sequence(TempoMap.Constant(new Ppqn(480), Tempo.Default), MeterMap.Constant(new Ppqn(480), TimeSignature.CommonTime), [.. tracks], []),
                Mixer = Mixer.Empty,
                Connections = [.. connections],
            };
            project = project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(synth.Id)) with { Devices = [.. chain] });
            Session.Execute(new ProjectCommand("Set up", _ => project));
            if (external)
            {
                Port = _provider.CreatePort("Synth", "synth");
                Session.Execute(ProjectCommands.SetTrackOutput(synth.Id, new TrackOutput { Endpoint = new EndpointReference(LoopbackMidiProvider.ProviderId, Port.OutputId.Value, Port.Name) }));
            }

            _endpoints = new EndpointDirectory([_provider]);
            Supervisor = new EngineSupervisor(new EngineSupervisorOptions { AllowTestCommands = true, TestAudio = true }, new ActivityLog());
            _playback = new EnginePlayback(Supervisor, new EnginePlaybackOptions { PlanDirectory = Path.Combine(_files, "plans") });
            Controller = new PlaybackController(Session, _endpoints, ProfileCatalog.Empty, _playback);
            Devices = new PluginDeviceHost(host.Manager, Session, [Sine, Transpose], Rate, Block);
            Controller.UseDevices(Devices.Extend(DeviceCatalog.BuiltIn));
            Controller.Live.UsePlugins(Devices.InstanceOf);
            Audio = new EngineAudio(Supervisor, Controller, Session, Devices.InstanceOf, options: new EngineAudioOptions { GraphDirectory = Path.Combine(_files, "graphs"), MaxBlockFrames = Block });
        }

        public ProjectSession Session { get; } = new();

        public EngineSupervisor Supervisor { get; }

        public PlaybackController Controller { get; }

        public EngineAudio Audio { get; }

        public PluginDeviceHost Devices { get; }

        public TrackId Synth { get; }

        /// <summary>The external instrument's port, when the route goes to one.</summary>
        public LoopbackPort? Port { get; }

        public static async Task<Rig> StartAsync(PluginTestHost host, bool withClip, int? transpose = null, bool external = false)
        {
            var rig = new Rig(host, withClip, transpose, external);
            await rig.Devices.SyncAsync(rig.Session.Project, Ct);
            foreach (var device in rig.Session.Project.ChainOf(rig.Synth)!.Devices)
            {
                host.Track(rig.Devices.InstanceOf(device.Id)!);
            }

            await rig.Controller.RefreshAsync(Ct);
            await rig.Supervisor.StartAsync();
            Assert.Equal(EngineState.Ready, rig.Supervisor.State);
            rig.Audio.Start(new AudioSettings(true, null, Rate, Block));
            await WaitUntilAsync(() => rig.Audio.IsRunning, Patience, "the engine to open the audio device");
            await rig.WaitForANewGraphAsync();
            Assert.Empty(rig.Audio.GraphProblems);

            // The synth track's live route runs in the engine, into the instrument.
            rig.Controller.Live.Prepare(rig.Synth);
            Assert.True(rig.Controller.Live.RunnerOf(rig.Synth)!.IsRemote);
            return rig;
        }

        /// <summary>Makes the synth track the thru track (monitored: depth 1), or no track, and waits for the graph that follows.</summary>
        public async Task MonitorAsync(bool on)
        {
            Controller.SetThruTrack(on ? Synth : null);
            Assert.Equal(on, Controller.Live.MonitoredInstrument is not null);
            await WaitForANewGraphAsync();
        }

        /// <summary>Describes the graph again, and waits until the engine plays it (or one described after it).</summary>
        public async Task WaitForANewGraphAsync()
        {
            Audio.Rebuild();
            var described = Audio.DescribedGraph;
            await WaitUntilAsync(() => Audio.PlayingGraph >= described, Patience, $"the engine to play graph {described}");
        }

        /// <summary>Renders with a key down on the synth track just before the callback of <see cref="KeyFrame"/>, the <see cref="Lead"/> before it is heard; the left channel.</summary>
        public Task<float[]> PlayAKeyAsync() =>
            RenderAsync([new TestNote(Synth.Value, KeyFrame, Lead.Ticks, 0x90, 69, 127), new TestNote(Synth.Value, KeyFrame + (8 * Block), Lead.Ticks, 0x80, 69, 64)]);

        /// <summary>Renders <paramref name="blocks"/> blocks from the song's start, playing <paramref name="notes"/> on the way; the left channel.</summary>
        public async Task<float[]> RenderAsync(TestNote[] notes, int blocks = 16)
        {
            var samples = await Audio.RenderForTestAsync(Path.Combine(_files, $"render-{Interlocked.Increment(ref _renders)}.f32"), blocks * Block, Block, Ct, [.. notes]);
            return [.. samples.Where((_, i) => i % 2 == 0)];
        }

        public async ValueTask DisposeAsync()
        {
            Audio.Dispose();
            await Controller.DisposeAsync();
            await Supervisor.DisposeAsync();
            _playback.Dispose();
            await Devices.DisposeAsync();
            _endpoints.Dispose();
            _provider.Dispose();
        }
    }
}
