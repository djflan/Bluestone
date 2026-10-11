using Bluestone.Application.Audio;
using Bluestone.Application.Sessions;
using Bluestone.Audio;
using Bluestone.Audio.Devices;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Profiles;

namespace Bluestone.Tests.Integration.Audio;

/// <summary>
/// Opens the real default output device. Hosted CI runners have no audio device; these tests skip there with the
/// reason. They play silence, so running them on a workstation makes no sound.
/// </summary>
[Collection(AudioDeviceGroup.Name)]
public sealed class AudioDeviceTests
{
    [Fact]
    public void DeviceList_HasTheDefaultOutput_WithRatesAndChannels()
    {
        var devices = RequireDevices();

        var chosen = devices.FirstOrDefault(d => d.IsDefault) ?? devices[0];
        Assert.False(string.IsNullOrWhiteSpace(chosen.Id));
        Assert.False(string.IsNullOrWhiteSpace(chosen.Name));
        Assert.NotEmpty(chosen.SampleRates);
        Assert.NotEmpty(chosen.ChannelCounts);
        Assert.True(chosen.DefaultSampleRate > 0);
    }

    [Fact]
    public void DefaultDevice_RunsTheEngineCallback_AtTheRequestedRateAndBufferSize()
    {
        var device = RequireDevices().FirstOrDefault(d => d.IsDefault) ?? RequireDevices()[0];
        var rate = device.SampleRates.Contains(48_000) ? 48_000 : device.DefaultSampleRate;
        using var engine = new AudioEngine(rate, 256);
        using var output = Open(engine, new AudioStreamRequest(device.Id, rate, 256));

        output.Start();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (engine.Statistics.Callbacks < 20 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        output.Stop();
        var status = output.Status;

        Assert.True(engine.Statistics.Callbacks >= 20, $"Only {engine.Statistics.Callbacks} callbacks arrived.");
        Assert.Equal(rate, status.SampleRate);
        Assert.Equal(rate, output.SampleRate);
        Assert.True(status.LastBufferFrames > 0);
        Assert.True(status.Frames >= status.Callbacks);
        Assert.Equal(0, engine.Statistics.Faults);
        Assert.Equal(0, output.RenderFaults);
    }

    [Fact]
    public void OpeningAnUnknownDevice_FailsWithAReason()
    {
        RequireDevices();
        using var engine = new AudioEngine(48_000);

        var error = Assert.Throws<AudioDeviceException>(() => AudioOutput.Open(engine, new AudioStreamRequest("coreaudio:no-such-device")));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void DisposingARunningOutput_StopsTheCallbacks()
    {
        RequireDevices();
        using var engine = new AudioEngine(48_000, 256);
        var output = Open(engine, new AudioStreamRequest(SampleRate: 0));
        output.Start();
        Thread.Sleep(100);

        output.Dispose();
        var after = engine.Statistics.Callbacks;
        Thread.Sleep(100);

        Assert.Equal(after, engine.Statistics.Callbacks);
        Assert.Throws<ObjectDisposedException>(() => output.Status);
    }

    [Fact]
    public void BufferedOutput_KeepsItsQueueFilled_WithoutUnderruns()
    {
        RequireDevices();
        using var engine = new AudioEngine(48_000, 128);
        using var output = Open(engine, new AudioStreamRequest(SampleRate: 48_000, BufferFrames: 128, QueuedFrames: 1024));

        output.Start();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (output.Status.Callbacks < 100 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        var status = output.Status;
        output.Stop();

        Assert.True(status.Callbacks >= 100, $"Only {status.Callbacks} callbacks arrived.");
        Assert.Equal(1024, output.QueuedFrames);
        Assert.True(status.QueueCapacity >= 1024);
        Assert.InRange(status.QueuedFrames, 0, status.QueueCapacity);
        Assert.Equal(0, status.QueueUnderrunFrames);
        Assert.True(engine.Statistics.Callbacks > 0);
    }

    [Fact]
    public async Task AudioPlayback_OpensTheDevice_FollowsTheMidiTransport_AndCloses()
    {
        RequireDevices();
        using var endpoints = new EndpointDirectory([]);
        var session = new ProjectSession();
        await using var playback = new PlaybackController(session, endpoints, ProfileCatalog.Empty, SystemMonotonicClock.Instance);
        using var audio = new AudioPlayback(playback, session, _ => null);

        if (!audio.Start(new AudioSettings(Enabled: true, SampleRate: 48_000, BufferFrames: 256)))
        {
            Assert.Skip($"The audio device could not be opened: {audio.Problem}");
        }

        await playback.PlayAsync(Tick.Zero, TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!audio.Engine!.Transport.IsPlaying && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.True(audio.Engine.Transport.IsPlaying);
        Assert.True(audio.IsRunning);
        playback.Stop();
        audio.Stop();
        Assert.False(audio.IsRunning);
        Assert.Null(audio.Engine);
    }

    private static AudioOutput Open(AudioEngine engine, AudioStreamRequest request)
    {
        try
        {
            return AudioOutput.Open(engine, request);
        }
        catch (AudioDeviceException ex)
        {
            Assert.Skip($"The audio device could not be opened: {ex.Message}");
            throw;
        }
    }

    private static System.Collections.Immutable.ImmutableArray<AudioDeviceInfo> RequireDevices()
    {
        if (!AudioDevices.IsAvailable(out var problem))
        {
            Assert.Fail($"The audio library must load on every platform: {problem}");
        }

        var devices = AudioDevices.List();
        if (devices.IsEmpty)
        {
            Assert.Skip("There is no audio output device (hosted CI runners have none).");
        }

        return devices;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AudioDeviceGroup
{
    public const string Name = "Audio device";
}
