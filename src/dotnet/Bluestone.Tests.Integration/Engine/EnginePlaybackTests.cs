using System.Diagnostics;
using Bluestone.Application.Editing;
using Bluestone.Application.Engine;
using Bluestone.Application.Sessions;
using Bluestone.Application.Status;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Playback;
using Bluestone.Profiles;
using Bluestone.Signal;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Engine;

/// <summary>
/// MIDI playback, thru, and recording through a real engine process (ADR 0044, migration step 5). Bluestone's loopback
/// endpoints are ones only Bluestone can reach, so the engine forwards what it plays to them and Bluestone forwards their
/// input: no MIDI hardware or operating-system MIDI service is needed. New projects are 960 PPQN at 120 BPM: a beat is
/// 500 ms.
/// </summary>
[Collection(EngineProcessTests.Name)]
public sealed class EnginePlaybackTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APlan_PlaysInTheEngine_ToAnOutputOnlyBluestoneReaches_InOrder_AndTheTransportIsMirrored()
    {
        await using var rig = await Rig.StartAsync();
        rig.AddTrack(Note(0, 60), Note(240, 62), Note(480, 64));

        await rig.Controller.PlayAsync(Tick.Zero, Ct);
        await WaitUntilAsync(() => rig.NoteMessages().Count >= 6, Patience, "the plan's six note messages");
        await WaitUntilAsync(() => rig.Controller.Transport.State == TransportState.Playing, Patience, "the transport to be reported playing");

        Assert.Equal([(0x90, 60), (0x80, 60), (0x90, 62), (0x80, 62), (0x90, 64), (0x80, 64)], rig.NoteMessages().Take(6));
        Assert.True(rig.Controller.Transport.TryGetTickAt(SystemMonotonicClock.Instance.Now, out var tick));
        Assert.InRange(tick.Value, 480, 960 * 20);
        await WaitUntilAsync(() => rig.Controller.Transport.SnapshotStatistics().Dispatched >= 6, Patience, "timing figures from the engine");

        rig.Controller.Stop();
        await WaitUntilAsync(() => rig.Controller.Transport.State == TransportState.Stopped, Patience, "the transport to stop");
        Assert.False(rig.Controller.Transport.TryGetTickAt(SystemMonotonicClock.Instance.Now, out _));
    }

    [Fact]
    public async Task Loop_Seek_AndStop_WorkInTheEngine()
    {
        await using var rig = await Rig.StartAsync();
        rig.AddTrack(Note(0, 60), Note(480, 62), Note(1920, 70));
        rig.Session.Execute(LoopCommands.SetRange(new TickRange(Tick.Zero, new Tick(960)), enable: true));

        await rig.Controller.PlayAsync(Tick.Zero, Ct);
        await WaitUntilAsync(() => rig.Controller.Transport.LoopWraps >= 2, Patience, "two loop wraps");
        rig.Controller.Stop();
        await WaitUntilAsync(() => rig.Controller.Transport.State == TransportState.Stopped, Patience, "the transport to stop");

        var ons = rig.NoteMessages().Where(m => m.Status == 0x90).Select(m => m.Note).ToList();
        Assert.True(ons.Count >= 4);
        Assert.DoesNotContain(70, ons);
        Assert.Equal([60, 62, 60, 62], ons.Take(4));

        // Seeking while stopped, then playing from the playhead, starts there.
        rig.Session.Execute(LoopCommands.SetRange(new TickRange(Tick.Zero, new Tick(960)), enable: false));
        await rig.Controller.RefreshAsync(Ct);
        rig.Synth.ClearSent();
        rig.Controller.Seek(new Tick(1900));
        await WaitUntilAsync(() => rig.Controller.Transport.Position.Value == 1900, Patience, "the engine to report the new playhead");
        await rig.Controller.PlayAsync(cancellationToken: Ct);
        await WaitUntilAsync(() => rig.NoteMessages().Any(m => m.Status == 0x90), Patience, "a note from the new playhead");

        Assert.Equal(70, rig.NoteMessages().First(m => m.Status == 0x90).Note);
        rig.Controller.Stop();
    }

    [Fact]
    public async Task InputOnlyBluestoneReaches_IsRecorded_AndPlayedThruTheRouteTheEngineRuns()
    {
        await using var rig = await Rig.StartAsync();
        var track = rig.AddTrack();
        rig.Session.Execute(DeviceCommands.InsertDevice(track, BuiltInDevices.CreateTranspose(12), DeviceCatalog.BuiltIn.Lookup));
        rig.Controller.SelectInputs([rig.Keys.InputId]);
        rig.Controller.SetThruTrack(track);

        await rig.Controller.RecordAsync(track, new RecordOptions(CountInBars: 0), Ct);
        await WaitUntilAsync(() => rig.Controller.Transport.TryGetTickAt(SystemMonotonicClock.Instance.Now, out _), Patience, "the engine to report playing");
        Assert.True(rig.Controller.Live.RunnerOf(track)!.IsRemote);

        rig.Keys.Inject([0x90, 60, 100]);
        await WaitUntilAsync(() => rig.Synth.Sent.Any(m => m.Bytes is [0x90, 72, 100]), Patience, "the transposed note thru the engine");
        await Task.Delay(200, Ct);
        rig.Keys.Inject([0x80, 60, 64]);
        await WaitUntilAsync(() => rig.Synth.Sent.Any(m => m.Bytes is [0x80, 72, _]), Patience, "its release");
        var take = rig.Controller.Stop();

        Assert.NotNull(take);
        var note = Assert.IsType<NoteEvent>(Assert.Single(rig.Session.Project.Sequence.FindTrack(track)!.ArrangedEvents));
        Assert.Equal(60, note.Note.Value);
        Assert.InRange(note.Duration.Value, 200, 2000);
    }

    [Fact]
    public async Task AKilledEngine_IsRestarted_SendsAllNotesOff_StaysStopped_AndPlaysAgainAfterResync()
    {
        await using var rig = await Rig.StartAsync();
        rig.AddTrack(new NoteEvent(Tick.Zero, new TickSpan(960 * 40), MidiChannel.FromIndex(0), new NoteNumber(60), Velocity.Max));
        await rig.Controller.PlayAsync(Tick.Zero, Ct);
        await WaitUntilAsync(() => rig.NoteMessages().Any(m => m.Status == 0x90), Patience, "the held note");
        await WaitUntilAsync(() => rig.Controller.Transport.State == TransportState.Playing, Patience, "the transport to be reported playing");
        var first = rig.Supervisor.ProcessId!.Value;
        rig.Synth.ClearSent();

        KillProcess(first);
        await WaitUntilAsync(() => rig.Supervisor.State == EngineState.Ready && rig.Supervisor.ProcessId is { } next && next != first, Patience, "a new engine");
        await WaitUntilAsync(() => rig.Synth.Sent.Count >= 48, Patience, "All Notes Off from the new engine");

        // Sustain off, All Notes Off, and All Sound Off on all 16 channels, before anything else.
        var sweep = rig.Synth.Sent.Take(48).Select(m => m.Bytes).ToList();
        Assert.All(sweep, bytes => Assert.Equal(0xB0, bytes[0] & 0xF0));
        foreach (var controller in new byte[] { 64, 123, 120 })
        {
            Assert.Equal(Enumerable.Range(0, 16), sweep.Where(b => b[1] == controller).Select(b => b[0] & 0x0F).Order());
        }

        Assert.Equal(TransportState.Stopped, rig.Controller.Transport.State);
        await Task.Delay(300, Ct);
        Assert.Equal(TransportState.Stopped, rig.Controller.Transport.State);
        Assert.DoesNotContain(rig.Synth.Sent, m => m.Bytes[0] == 0x90);

        await rig.Controller.PlayAsync(Tick.Zero, Ct);
        await WaitUntilAsync(() => rig.Synth.Sent.Any(m => m.Bytes is [0x90, 60, _]), Patience, "the note again after the restart");
        rig.Controller.Stop();
    }

    [Fact]
    public async Task TheEnginesInputAndThruPath_AndItsAudioRender_AllocateNothing()
    {
        var engine = EngineLauncher.ResolveDefault();
        var info = EngineLauncher.CreateStartInfo(engine, "unused", "unused", restarted: false, allowTestCommands: false);
        info.ArgumentList.Clear();
        if (engine.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add(engine);
        }

        info.ArgumentList.Add("--check-allocations");
        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);

        Assert.True(process.ExitCode == 0, output);
        Assert.Contains("MIDI: allocated 0 bytes", output, StringComparison.Ordinal);
        Assert.Contains("audio: allocated 0 bytes", output, StringComparison.Ordinal);
    }

    private static NoteEvent Note(long at, int note) => new(new Tick(at), new TickSpan(120), MidiChannel.FromIndex(0), new NoteNumber(note), Velocity.Max);

    /// <summary>An engine, a controller playing through it, and loopback ports for an instrument and a keyboard.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _plans = Path.Combine(AppContext.BaseDirectory, "engine-plans", Guid.NewGuid().ToString("N"));

        private Rig()
        {
            Provider = new LoopbackMidiProvider(SystemMonotonicClock.Instance);
            Synth = Provider.CreatePort("Synth", "synth");
            Keys = Provider.CreatePort("Keys", "keys");
            Directory = new EndpointDirectory([Provider]);
            Supervisor = new EngineSupervisor(new EngineSupervisorOptions { AllowTestCommands = true }, new ActivityLog());
            Engine = new EnginePlayback(Supervisor, new EnginePlaybackOptions { PlanDirectory = _plans });
            Controller = new PlaybackController(Session, Directory, ProfileCatalog.Empty, Engine);
        }

        public LoopbackMidiProvider Provider { get; }

        public LoopbackPort Synth { get; }

        public LoopbackPort Keys { get; }

        public EndpointDirectory Directory { get; }

        public EngineSupervisor Supervisor { get; }

        public EnginePlayback Engine { get; }

        public ProjectSession Session { get; } = new();

        public PlaybackController Controller { get; }

        public static async Task<Rig> StartAsync()
        {
            var rig = new Rig();
            await rig.Supervisor.StartAsync();
            Assert.Equal(EngineState.Ready, rig.Supervisor.State);
            return rig;
        }

        public TrackId AddTrack(params TrackEvent[] events)
        {
            var track = Track.FromEvents(TrackId.New(), "t", events);
            Session.Execute(ProjectCommands.AddTrack(track));
            Session.Execute(ProjectCommands.SetTrackOutput(track.Id, new TrackOutput { Endpoint = new EndpointReference(LoopbackMidiProvider.ProviderId, Synth.OutputId.Value, Synth.Name) }));
            return track.Id;
        }

        /// <summary>Note-ons and note-offs sent to the instrument, in order.</summary>
        public List<(int Status, int Note)> NoteMessages() =>
            [.. Synth.Sent.Where(m => (m.Bytes[0] & 0xF0) is 0x80 or 0x90).Select(m => ((m.Bytes[0] & 0xF0) == 0x90 && m.Bytes[2] == 0 ? 0x80 : m.Bytes[0] & 0xF0, (int)m.Bytes[1]))];

        public async ValueTask DisposeAsync()
        {
            await Controller.DisposeAsync();
            await Supervisor.DisposeAsync();
            Engine.Dispose();
            Directory.Dispose();
            Provider.Dispose();
            if (System.IO.Directory.Exists(_plans))
            {
                System.IO.Directory.Delete(_plans, recursive: true);
            }
        }
    }
}
