using Bluestone.Application.Audio;
using Bluestone.Application.Engine;
using Bluestone.Application.Sessions;
using Bluestone.Application.Status;
using Bluestone.Domain.Mixing;
using Bluestone.Engine.Protocol;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Profiles;

namespace Bluestone.Tests.Unit.Engine;

/// <summary>
/// Bluestone's side of audio in the engine (ADR 0044, migration step 6): the settings go to every (re)connected engine,
/// the device's state, meters, and figures it reports are mirrored, graphs are described for the stream it opened, and
/// their files are deleted once the engine has moved past them.
/// </summary>
public sealed class EngineAudioTests : IAsyncDisposable
{
    private readonly string _graphs = Path.Combine(AppContext.BaseDirectory, "engine-graphs", Guid.NewGuid().ToString("N"));
    private readonly EngineSupervisor _supervisor = new(new EngineSupervisorOptions(), new ActivityLog());
    private readonly LoopbackMidiProvider _provider = new(new VirtualClock(TimeSpan.FromSeconds(1)));
    private readonly EndpointDirectory _endpoints;
    private readonly PlaybackController _playback;
    private readonly ProjectSession _session = new();
    private readonly EngineAudio _audio;

    public EngineAudioTests()
    {
        _endpoints = new EndpointDirectory([_provider]);
        _playback = new PlaybackController(_session, _endpoints, ProfileCatalog.Empty, new VirtualClock(TimeSpan.FromSeconds(1)), startThread: false);
        _audio = new EngineAudio(_supervisor, _playback, _session, _ => null, options: new EngineAudioOptions { GraphDirectory = _graphs });
    }

    private IEngineClient Client => _audio;

    public async ValueTask DisposeAsync()
    {
        _audio.Dispose();
        await _playback.DisposeAsync();
        await _supervisor.DisposeAsync();
        _endpoints.Dispose();
        _provider.Dispose();
        if (Directory.Exists(_graphs))
        {
            Directory.Delete(_graphs, recursive: true);
        }
    }

    [Fact]
    public void Start_SendsTheSettings_WithTheEnginesOwnRenderAheadQueue_AndMirrorsTheDeviceTheEngineOpened()
    {
        var link = new RecordingLink();
        Client.Connected(link, restarted: false);

        Assert.False(_audio.Start(new AudioSettings(true, "studio", 48_000, 128, QueuedFrames: 1024) { EngineQueuedFrames = 0 }));
        var sent = Assert.IsType<SetAudio>(Assert.Single(link.Sent));
        Assert.Equal(new SetAudio(sent.Request, true, "studio", 48_000, 128, 0, 256, false), sent);
        Assert.True(_audio.IsStarting);
        Assert.False(_audio.IsWaitingForDevice);
        Assert.True(_audio.RendersInEngine);

        Client.Received(link, new AudioDeviceStatus(sent.Request, AudioDeviceState.Running, null, 48_000, 2, 128, 0, null));

        Assert.False(_audio.IsStarting);
        Assert.True(_audio.IsRunning);
        Assert.False(_audio.IsWaitingForDevice);
        Assert.Equal(new AudioStreamInfo(48_000, 2, 128, 0, null, 0), _audio.Stream);
        Assert.Null(_audio.Problem);

        _audio.TestTone = true;
        Assert.True(Assert.IsType<SetAudio>(link.Sent[^1]).TestTone);

        _audio.Stop();
        Assert.False(Assert.IsType<SetAudio>(link.Sent[^1]).Enabled);
        Assert.False(_audio.IsWaitingForDevice);
    }

    [Fact]
    public void Start_ReturnsAtOnce_WhenTheEngineNeverAnswers_AndReportsStartingUntilItDoes()
    {
        var link = new RecordingLink();
        Client.Connected(link, restarted: false);
        var changes = 0;
        _audio.Changed += (_, _) => changes++;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var started = _audio.Start(new AudioSettings(true));
        watch.Stop();

        Assert.False(started);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(250), $"Start took {watch.Elapsed.TotalMilliseconds:0} ms.");
        Assert.True(_audio.IsStarting);
        Assert.False(_audio.IsRunning);
        Assert.False(_audio.IsWaitingForDevice);
        Assert.Equal(1, changes);
        Assert.Empty(link.Sent.OfType<LoadAudioGraph>());

        // An answer to an earlier request does not end the start.
        Client.Received(link, new AudioDeviceStatus(0, AudioDeviceState.Off, null, 0, 0, 0, 0, null));
        Assert.True(_audio.IsStarting);

        Client.Received(link, new AudioDeviceStatus(link.Sent.OfType<SetAudio>().Last().Request, AudioDeviceState.Waiting, "No device.", 0, 0, 0, 0, null));
        Assert.False(_audio.IsStarting);
        Assert.True(_audio.IsWaitingForDevice);
        Assert.True(changes >= 3);
    }

    [Fact]
    public void AFallbackOrAWait_IsReportedAsTheEngineSaysIt()
    {
        var link = new RecordingLink();
        Client.Connected(link, restarted: false);
        _audio.Start(new AudioSettings(true, "studio"));

        Client.Received(link, new AudioDeviceStatus(1, AudioDeviceState.Fallback, "Studio is not available.", 44_100, 2, 256, 0, null));
        Assert.True(_audio.IsRunning);
        Assert.True(_audio.IsOnFallback);
        Assert.Equal("Studio is not available.", _audio.Problem);

        Client.Received(link, new AudioDeviceStatus(1, AudioDeviceState.Waiting, "No device.", 0, 0, 0, 0, null));
        Assert.False(_audio.IsRunning);
        Assert.True(_audio.IsWaitingForDevice);
        Assert.Null(_audio.Stream);
    }

    [Fact]
    public void Meters_AndFigures_AreReadFromTheEnginesLastReport()
    {
        var link = new RecordingLink();
        var channel = MixerChannelId.New();
        Client.Connected(link, restarted: false);
        _audio.Start(new AudioSettings(true));
        Assert.Null(_audio.MeterOf(null));

        Client.Received(link, new AudioDeviceStatus(1, AudioDeviceState.Running, null, 48_000, 2, 256, 0, null));
        Client.Received(link, new AudioStatus(new MeterSpec(0.5f, 0.25f, 0.1f, 0.05f), [new StripMeter(channel.Value, new MeterSpec(1, 0, 0.5f, 0))], new AudioCounters(100, 2, 0, 1, 0, 0, 5000, 70_000, 3, 512)));

        Assert.Equal(0.5f, _audio.MeterOf(null)!.Value.PeakLeft);
        Assert.Equal(1f, _audio.MeterOf(channel)!.Value.PeakLeft);
        Assert.Null(_audio.MeterOf(MixerChannelId.New()));
        var telemetry = _audio.Telemetry!.Value;
        Assert.Equal((100L, 2L, 3L), (telemetry.Callbacks, telemetry.Xruns, telemetry.PluginFailedBlocks));
        Assert.Equal(TimeSpan.FromTicks(70_000), telemetry.OutputLatency);

        // Reports from an engine that has gone are ignored, and meters go with it.
        Client.Disconnected(link);
        Client.Received(link, new AudioStatus(new MeterSpec(0.9f, 0, 0, 0), [], default));
        Assert.Null(_audio.MeterOf(null));
        Assert.False(_audio.IsRunning);
    }

    [Fact]
    public async Task AGraph_IsDescribedForTheOpenStream_AndItsFileIsDeletedOnceANewerOneIsLoaded()
    {
        var link = new RecordingLink();
        Client.Connected(link, restarted: false);
        _audio.Start(new AudioSettings(true));

        // Nothing is described before the engine says which stream is open.
        _audio.Rebuild();
        Assert.Empty(link.Sent.OfType<LoadAudioGraph>());

        Client.Received(link, new AudioDeviceStatus(1, AudioDeviceState.Running, null, 44_100, 2, 128, 0, null));
        await WaitUntilAsync(() => link.Sent.OfType<LoadAudioGraph>().Any());
        var first = link.Sent.OfType<LoadAudioGraph>().Single();
        Assert.Equal((44_100, 128), (first.SampleRate, first.BlockFrames));
        Assert.True(File.Exists(first.Path));

        _audio.Rebuild();
        var second = link.Sent.OfType<LoadAudioGraph>().Last();
        Assert.True(second.GraphId > first.GraphId);
        Client.Received(link, new AudioGraphLoaded(second.GraphId, true, 256, ["Vocal: tone.wav could not be opened."]));

        Assert.False(File.Exists(first.Path));
        Assert.True(File.Exists(second.Path));
        Assert.Equal(256, _audio.Stream!.LatencyFrames);
        Assert.Contains("Vocal: tone.wav could not be opened.", _audio.GraphProblems);
    }

    [Fact]
    public async Task ARestartedEngine_IsSentTheSettingsAgain_AndTheGraphFollowsTheStreamItOpens()
    {
        var first = new RecordingLink();
        Client.Connected(first, restarted: false);
        _audio.Start(new AudioSettings(true, "studio"));
        Client.Received(first, new AudioDeviceStatus(1, AudioDeviceState.Running, null, 48_000, 2, 256, 0, null));
        await WaitUntilAsync(() => first.Sent.OfType<LoadAudioGraph>().Any());

        Client.Disconnected(first);
        Assert.False(_audio.IsRunning);
        Assert.True(_audio.IsWaitingForDevice);
        Assert.NotNull(_audio.Problem);

        var second = new RecordingLink();
        Client.Connected(second, restarted: true);
        var resent = Assert.IsType<SetAudio>(Assert.Single(second.Sent));
        Assert.Equal("studio", resent.DeviceId);
        Client.Received(second, new AudioDeviceStatus(resent.Request, AudioDeviceState.Running, null, 48_000, 2, 256, 0, null));
        await WaitUntilAsync(() => second.Sent.OfType<LoadAudioGraph>().Any());
        Assert.True(_audio.IsRunning);
    }

    [Fact]
    public async Task ItIsReady_WhenAudioIsOff_OrTheDeviceIsOpenAndTheEngineAnsweredTheGraph_OrNoDeviceCanOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        var shortWait = TimeSpan.FromMilliseconds(30);
        Assert.True(await _audio.WhenReadyAsync(shortWait, ct));

        var first = new RecordingLink();
        Client.Connected(first, restarted: false);
        _audio.Start(new AudioSettings(true));
        Assert.False(await _audio.WhenReadyAsync(shortWait, ct));

        // The device is open, but the graph for it has not been answered: playback would start ahead of the audio.
        Client.Received(first, new AudioDeviceStatus(1, AudioDeviceState.Running, null, 48_000, 2, 128, 0, null));
        await WaitUntilAsync(() => first.Sent.OfType<LoadAudioGraph>().Any());
        Assert.False(await _audio.WhenReadyAsync(shortWait, ct));
        Client.Received(first, new AudioGraphLoaded(first.Sent.OfType<LoadAudioGraph>().Last().GraphId, true, 0, []));
        Assert.True(await _audio.WhenReadyAsync(shortWait, ct));

        // A restarted engine: not ready until it has opened the device and answered a graph of its own.
        Client.Disconnected(first);
        var second = new RecordingLink();
        Client.Connected(second, restarted: true);
        Assert.False(await _audio.WhenReadyAsync(shortWait, ct));
        var resent = second.Sent.OfType<SetAudio>().Single();
        Client.Received(second, new AudioDeviceStatus(resent.Request, AudioDeviceState.Running, null, 48_000, 2, 128, 0, null));
        Assert.False(await _audio.WhenReadyAsync(shortWait, ct));
        await WaitUntilAsync(() => second.Sent.OfType<LoadAudioGraph>().Any());
        Client.Received(second, new AudioGraphLoaded(second.Sent.OfType<LoadAudioGraph>().Last().GraphId, false, 0, ["Not applied."]));
        Assert.True(await _audio.WhenReadyAsync(shortWait, ct));

        // No device can open: MIDI plays without audio rather than waiting for it.
        _audio.Start(new AudioSettings(true, "gone"));
        var again = second.Sent.OfType<SetAudio>().Last();
        Client.Received(second, new AudioDeviceStatus(again.Request, AudioDeviceState.Waiting, "No device.", 0, 0, 0, 0, null));
        Assert.True(await _audio.WhenReadyAsync(shortWait, ct));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Timed out.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class RecordingLink : IEngineLink
    {
        private readonly Lock _gate = new();
        private readonly List<EngineMessage> _sent = [];

        public List<EngineMessage> Sent
        {
            get
            {
                lock (_gate)
                {
                    return [.. _sent];
                }
            }
        }

        public bool Send(EngineMessage message)
        {
            lock (_gate)
            {
                _sent.Add(message);
            }

            return true;
        }
    }
}
