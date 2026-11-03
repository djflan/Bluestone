using Bluestone.Application.Audio;
using Bluestone.Application.Editing;
using Bluestone.Application.Engine;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Application.Status;
using Bluestone.Audio;
using Bluestone.Audio.Files;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Plugins;
using Bluestone.Profiles;
using Bluestone.Signal;
using Bluestone.Tests.Integration.Plugins;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Engine;

/// <summary>
/// Audio through a real engine process (ADR 0044, migration step 6): Bluestone describes the graph, the engine reads and
/// realizes it, streams the clip from disk, and drives the plugin's worker through its block exchange; what it renders
/// through its device callback's path must be exactly what Bluestone renders in one process. The engine's audio device is
/// the test stand-in, so no audio hardware is needed. The project has a clip in its first half second and a note on the
/// reference sine in the next, so the plugin's part can be told apart.
/// </summary>
[Collection(EngineProcessTests.Name)]
public sealed class EngineAudioTests
{
    private const int Rate = 48_000;
    private const int Block = 256;
    private const int Frames = Rate * 3 / 2;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AClipAndAPlugin_RenderInTheEngine_ExactlyAsInBluestone_AndAPluginThatStops_IsSilentUntilItRestarts()
    {
        await using var host = new PluginTestHost();
        await using var rig = await Rig.StartAsync(host);
        var expected = await rig.RenderInBluestoneAsync();
        var plugin = Rig.SynthPart(expected);
        Assert.Contains(Rig.ClipPart(expected), s => Math.Abs(s) > 0.1f);
        Assert.Contains(plugin, s => Math.Abs(s) > 0.1f);

        var rendered = await rig.RenderInEngineAsync();
        Assert.Equal(expected, rendered);
        await WaitUntilAsync(() => rig.Audio.MeterOf(rig.Channel) is { PeakLeft: > 0 }, Patience, "the channel's meter from the engine");

        // The worker dies: Bluestone marks its exchange, and the engine plays the failure policy (silence) for it.
        var instance = rig.Devices.InstanceOf(rig.Synth)!;
        KillProcess(instance.WorkerProcessId!.Value);
        await WaitForStateAsync(instance, PluginInstanceState.Unavailable);
        await rig.WaitForANewGraphAsync();
        var crashed = await rig.RenderInEngineAsync();
        Assert.Equal(Rig.ClipPart(expected), Rig.ClipPart(crashed));
        Assert.All(Rig.SynthPart(crashed), s => Assert.Equal(0f, s));

        // Restarted, it has a new exchange; Bluestone describes the graph again and the engine attaches to it.
        Assert.True(await rig.Devices.RestartAsync(rig.Synth, Ct));
        host.Track(instance);
        await WaitForStateAsync(instance, PluginInstanceState.Running);
        await rig.WaitForANewGraphAsync();
        Assert.Equal(expected, await rig.RenderInEngineAsync());
    }

    [Fact]
    public async Task AKilledEngine_IsRestarted_AndTheSameAudioComesBackWithoutAsking()
    {
        await using var host = new PluginTestHost();
        await using var rig = await Rig.StartAsync(host);
        var expected = await rig.RenderInBluestoneAsync();
        Assert.Equal(expected, await rig.RenderInEngineAsync());
        var first = rig.Supervisor.ProcessId!.Value;

        KillProcess(first);
        await WaitUntilAsync(() => rig.Supervisor.State == EngineState.Ready && rig.Supervisor.ProcessId is { } next && next != first, Patience, "a new engine");
        await WaitUntilAsync(() => rig.Audio.IsRunning, Patience, "the new engine to open the audio device");
        await rig.WaitForANewGraphAsync();

        // The plugin's worker and state survived; the new engine attached to the same exchange.
        Assert.Equal(PluginInstanceState.Running, rig.Devices.InstanceOf(rig.Synth)!.Status.State);
        var rendered = await rig.RenderInEngineAsync();
        var same = expected.AsSpan().CommonPrefixLength(rendered);
        Assert.True(same == expected.Length && rendered.Length == expected.Length,
            $"The restarted engine's render differs from frame {same / 2} (block {same / 2 / Block}): expected {expected.ElementAtOrDefault(same)}, got {rendered.ElementAtOrDefault(same)}; telemetry {rig.Audio.Telemetry}.");
    }

    [Fact]
    public async Task AMissingDevice_IsReplacedByTheDefault_AndReportedAsAnEngineProblem()
    {
        await using var host = new PluginTestHost();
        await using var rig = await Rig.StartAsync(host);

        rig.Audio.Start(new AudioSettings(true, "no-such-device", Rate, Block));
        await WaitUntilAsync(() => rig.Audio.IsOnFallback, Patience, "the engine to play through the default output");
        await WaitUntilAsync(() => { lock (rig.Warnings) { return rig.Warnings.Any(w => w.Contains("is not available", StringComparison.Ordinal)); } }, Patience, "the device problem in the activity log");

        Assert.True(rig.Audio.IsRunning);
        Assert.Contains("is not available", rig.Audio.Problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// A project, the engine with its MIDI and audio clients, and two plugin bridges: one whose instance the engine drives,
    /// and one whose instance renders the same project in this process for comparison.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _files;
        private readonly LoopbackMidiProvider _provider = new(SystemMonotonicClock.Instance);
        private readonly EndpointDirectory _endpoints;
        private readonly EnginePlayback _playback;
        private readonly PlaybackController _controller;
        private readonly PluginDeviceHost _local;
        private long _renders;

        private Rig(PluginTestHost host)
        {
            _files = Path.Combine(host.Directory, "engine");
            Directory.CreateDirectory(_files);
            var clip = Path.Combine(_files, "tone.wav");
            using (var writer = new WaveFileWriter(clip, Rate, 2))
            {
                var samples = new float[Rate / 2 * 2];
                for (var i = 0; i < Rate / 2; i++)
                {
                    samples[2 * i] = (float)(0.4 * Math.Sin(2 * Math.PI * 330 * i / Rate));
                    samples[(2 * i) + 1] = (float)(0.3 * Math.Sin(2 * Math.PI * 550 * i / Rate));
                }

                writer.Write(samples);
            }

            // 480 PPQN at 120 BPM: 960 ticks a second. The clip plays from 0 to 0.5 s; the note from 0.5 s to 1 s.
            var audioTrack = Track.Create("Clip", TrackRole.Audio).WithClip(new AudioClip(ClipId.New(), Tick.Zero, new TickSpan(480), TickSpan.Zero, new AudioSource(clip, Rate / 2, Rate, 2)));
            var note = new NoteEvent(new Tick(480), new TickSpan(480), MidiChannel.FromIndex(0), new NoteNumber(69), new Velocity(127));
            var synthTrack = Track.FromEvents(TrackId.New(), "Lead", [note]);
            var device = DeviceInstance.Create(PluginDeviceHost.DefinitionOf(Sine).ToReference());
            var channel = MixerChannel.Create("Lead") with { GainDecibels = -3, Pan = 0.25 };
            Synth = device.Id;
            Channel = channel.Id;
            var project = Session.Project with
            {
                Sequence = new Sequence(TempoMap.Constant(new Ppqn(480), Tempo.Default), MeterMap.Constant(new Ppqn(480), TimeSignature.CommonTime), [audioTrack, synthTrack], []),
                Mixer = Mixer.Empty.With(channel) with { MasterGainDecibels = -1 },
                Connections =
                [
                    SignalConnection.Create(SignalKind.Audio, SignalNode.Track(audioTrack.Id), SignalNode.Master),
                    SignalConnection.Create(SignalKind.Audio, SignalNode.Track(synthTrack.Id), SignalNode.Mixer(channel.Id)),
                ],
            };
            project = project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(synthTrack.Id)) with { Devices = [device] });
            Session.Execute(new ProjectCommand("Set up", _ => project));

            _endpoints = new EndpointDirectory([_provider]);
            Activities.Ended += (_, ended) =>
            {
                lock (Warnings)
                {
                    if (ended.Outcome == ActivityOutcome.Warning)
                    {
                        Warnings.Add(ended.Text);
                    }
                }
            };
            Supervisor = new EngineSupervisor(new EngineSupervisorOptions { AllowTestCommands = true, TestAudio = true }, Activities);
            _playback = new EnginePlayback(Supervisor, new EnginePlaybackOptions { PlanDirectory = Path.Combine(_files, "plans") });
            _controller = new PlaybackController(Session, _endpoints, ProfileCatalog.Empty, _playback);
            Devices = new PluginDeviceHost(host.Manager, Session, [Sine], Rate, Block);
            _local = new PluginDeviceHost(host.Manager, Session, [Sine], Rate, Block);
            _controller.UseDevices(Devices.Extend(DeviceCatalog.BuiltIn));
            Audio = new EngineAudio(Supervisor, _controller, Session, Devices.InstanceOf, options: new EngineAudioOptions { GraphDirectory = Path.Combine(_files, "graphs"), MaxBlockFrames = Block });
        }

        public ProjectSession Session { get; } = new();

        public ActivityLog Activities { get; } = new();

        /// <summary>Warnings recorded in the activity log (the status line and Messages).</summary>
        public List<string> Warnings { get; } = [];

        public EngineSupervisor Supervisor { get; }

        public EngineAudio Audio { get; }

        public PluginDeviceHost Devices { get; }

        public DeviceId Synth { get; }

        public MixerChannelId Channel { get; }

        public static async Task<Rig> StartAsync(PluginTestHost host)
        {
            var rig = new Rig(host);
            await rig.Devices.SyncAsync(rig.Session.Project, Ct);
            await rig._local.SyncAsync(rig.Session.Project, Ct);
            host.Track(rig.Devices.InstanceOf(rig.Synth)!);
            host.Track(rig._local.InstanceOf(rig.Synth)!);
            await rig._controller.RefreshAsync(Ct);
            await rig.Supervisor.StartAsync();
            Assert.Equal(EngineState.Ready, rig.Supervisor.State);
            rig.Audio.Start(new AudioSettings(true, null, Rate, Block));
            await WaitUntilAsync(() => rig.Audio.IsRunning, Patience, "the engine to open the audio device");
            Assert.Equal(new AudioStreamInfo(Rate, 2, Block, 0, null, rig.Audio.Stream!.LatencyFrames), rig.Audio.Stream);
            await rig.WaitForANewGraphAsync();
            Assert.Empty(rig.Audio.GraphProblems);
            return rig;
        }

        /// <summary>Describes the graph again, and waits until the engine plays it (or one described after it).</summary>
        public async Task WaitForANewGraphAsync()
        {
            Audio.Rebuild();
            var described = Audio.DescribedGraph;
            await WaitUntilAsync(() => Audio.PlayingGraph >= described, Patience, $"the engine to play graph {described}");
        }

        public Task<float[]> RenderInEngineAsync() =>
            Audio.RenderForTestAsync(Path.Combine(_files, $"render-{Interlocked.Increment(ref _renders)}.f32"), Frames, Block, Ct);

        /// <summary>The same project rendered in this process, by the in-process path, with the other instance.</summary>
        public Task<float[]> RenderInBluestoneAsync()
        {
            var devices = _local.Extend(DeviceCatalog.BuiltIn);
            var project = Session.Project;
            using var graphs = new ProjectAudioGraph(Rate, Block, _local.InstanceOf);
            var result = graphs.Build(project, SignalGraph.Evaluate(project, devices), devices, null);
            Assert.Empty(result.Problems);
            using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
            engine.Publish(result.Graph);
            engine.Transport.Play(0);
            return Task.FromResult(new OfflineAudioDriver(engine).Render(Frames, Block));
        }

        // Both parts are heard one block late: the plugin's pipeline, and the clip's compensation for it.

        /// <summary>The interleaved samples while only the clip sounds.</summary>
        public static float[] ClipPart(float[] samples) => samples[..(((Rate / 2) + Block) * 2)];

        /// <summary>The interleaved samples from the note on.</summary>
        public static float[] SynthPart(float[] samples) => samples[(((Rate / 2) + Block) * 2)..];

        public async ValueTask DisposeAsync()
        {
            Audio.Dispose();
            await _controller.DisposeAsync();
            await Supervisor.DisposeAsync();
            _playback.Dispose();
            await Devices.DisposeAsync();
            await _local.DisposeAsync();
            _endpoints.Dispose();
            _provider.Dispose();
        }
    }
}
