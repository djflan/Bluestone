using Bluestone.Application.Audio;
using Bluestone.Application.Sessions;
using Bluestone.Audio.Devices;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Profiles;

namespace Bluestone.Tests.Unit.Application;

/// <summary>
/// Losing and getting back audio devices, with a fake backend at the C# boundary (a real device cannot be unplugged in a
/// test): the engine falls back to the default output, waits when nothing can be opened, returns to the chosen device,
/// asks plugins for each device's rate, and closes every stream it opened.
/// </summary>
public sealed class AudioPlaybackRecoveryTests : IAsyncDisposable
{
    private readonly EndpointDirectory _endpoints = new([]);
    private readonly PlaybackController _playback;
    private readonly FakeBackend _backend = new();
    private readonly List<int> _prepared = [];
    private readonly AudioPlayback _audio;

    public AudioPlaybackRecoveryTests()
    {
        var session = new ProjectSession();
        _playback = new PlaybackController(session, _endpoints, ProfileCatalog.Empty, new VirtualClock(), startThread: false);
        _audio = new AudioPlayback(_playback, session, _ => null, backend: _backend, prepareSampleRate: Prepare, checkInterval: Timeout.InfiniteTimeSpan);
    }

    public async ValueTask DisposeAsync()
    {
        _audio.Dispose();
        await _playback.DisposeAsync();
        _endpoints.Dispose();
        Assert.All(_backend.Opened, o => Assert.Equal(1, o.Disposals));
    }

    private Task Prepare(int rate)
    {
        _prepared.Add(rate);
        return Task.CompletedTask;
    }

    private static AudioDeviceInfo Device(string id, string name, int rate, bool isDefault = false) =>
        new(id, name, "Test", isDefault, rate, 2, [rate], [2], 64, 4096);

    [Fact]
    public void ALostDevice_FallsBackToTheDefault_AndAudioReturnsToItWhenItIsBack()
    {
        var usb = Device("usb", "USB interface", 44_100);
        _backend.Devices.AddRange([Device("built-in", "Speakers", 48_000, isDefault: true), usb]);
        Assert.True(_audio.Start(new AudioSettings(true, "usb")));
        Assert.Equal((44_100, false), (_audio.Engine!.SampleRate, _audio.IsOnFallback));

        _backend.Devices.Remove(usb);
        _backend.Opened[0].Failed = true;
        _audio.CheckDevice();

        Assert.Equal(1, _backend.Opened[0].Disposals);
        Assert.Equal("built-in", _backend.Opened[1].Device);
        Assert.True(_audio.IsRunning);
        Assert.True(_audio.IsOnFallback);
        Assert.Equal(48_000, _audio.Engine!.SampleRate);
        Assert.StartsWith("USB interface is not available", _audio.Problem, StringComparison.Ordinal);

        // Still gone: nothing changes.
        _audio.CheckDevice();
        Assert.Equal(2, _backend.Opened.Count);

        _backend.Devices.Add(usb);
        _audio.CheckDevice();

        Assert.Equal(1, _backend.Opened[1].Disposals);
        Assert.Equal("usb", _backend.Opened[2].Device);
        Assert.False(_audio.IsOnFallback);
        Assert.Null(_audio.Problem);
        Assert.Equal(44_100, _audio.Engine!.SampleRate);
        Assert.Equal([44_100, 48_000, 44_100], _prepared);
    }

    [Fact]
    public void WithNothingToOpen_AudioWaits_AndStartsWhenADeviceAppears()
    {
        Assert.False(_audio.Start(new AudioSettings(true)));
        Assert.True(_audio.IsWaitingForDevice);
        Assert.Contains("starts by itself", _audio.Problem, StringComparison.Ordinal);
        var attempts = _backend.Attempts;

        _audio.CheckDevice();
        Assert.Equal(attempts, _backend.Attempts);

        _backend.Devices.Add(Device("built-in", "Speakers", 48_000, isDefault: true));
        _audio.CheckDevice();

        Assert.True(_audio.IsRunning);
        Assert.False(_audio.IsWaitingForDevice);
        Assert.Null(_audio.Problem);
    }

    [Fact]
    public void LosingTheOnlyDevice_StopsCleanly_AndWaits()
    {
        var speakers = Device("built-in", "Speakers", 48_000, isDefault: true);
        _backend.Devices.Add(speakers);
        _audio.Start(new AudioSettings(true));

        _backend.Devices.Clear();
        _backend.Opened[0].Failed = true;
        _audio.CheckDevice();

        Assert.False(_audio.IsRunning);
        Assert.Null(_audio.Engine);
        Assert.True(_audio.IsWaitingForDevice);
        Assert.StartsWith("The system default output stopped", _audio.Problem, StringComparison.Ordinal);

        _backend.Devices.Add(speakers);
        _audio.CheckDevice();
        Assert.True(_audio.IsRunning);
    }

    [Fact]
    public void Stop_WhileWaiting_StopsWaiting()
    {
        _audio.Start(new AudioSettings(true));

        _audio.Stop();
        _backend.Devices.Add(Device("built-in", "Speakers", 48_000, isDefault: true));
        _audio.CheckDevice();

        Assert.False(_audio.IsRunning);
        Assert.False(_audio.IsWaitingForDevice);
        Assert.Empty(_backend.Opened);
    }

    [Fact]
    public void ChoosingAnotherDevice_WhileOnTheFallback_OpensIt()
    {
        _backend.Devices.AddRange([Device("built-in", "Speakers", 48_000, isDefault: true), Device("headset", "Headset", 32_000)]);
        _audio.Start(new AudioSettings(true, "gone"));
        Assert.True(_audio.IsOnFallback);

        Assert.True(_audio.Start(new AudioSettings(true, "headset")));

        Assert.False(_audio.IsOnFallback);
        Assert.Equal("headset", _backend.Opened[^1].Device);
        Assert.Equal(32_000, _audio.Engine!.SampleRate);
    }

    private sealed class FakeBackend : IAudioBackend
    {
        public List<AudioDeviceInfo> Devices { get; } = [];

        public List<FakeOutput> Opened { get; } = [];

        public int Attempts { get; private set; }

        public IReadOnlyList<AudioDeviceInfo> ListDevices() => [.. Devices];

        public IAudioOutput Open(IAudioRenderer renderer, AudioStreamRequest request)
        {
            Attempts++;
            var device = request.DeviceId is null ? Devices.FirstOrDefault(d => d.IsDefault) : Devices.FirstOrDefault(d => d.Id == request.DeviceId);
            if (device is null)
            {
                throw new AudioDeviceException("The device is not connected.");
            }

            var output = new FakeOutput(device.Id, device.DefaultSampleRate);
            Opened.Add(output);
            return output;
        }
    }

    private sealed class FakeOutput(string device, int rate) : IAudioOutput
    {
        public string Device => device;

        public bool Failed { get; set; }

        public int Disposals { get; private set; }

        public int SampleRate => rate;

        public int Channels => 2;

        public int QueuedFrames => 0;

        public string? RenderThreadPolicy => null;

        public AudioStreamStatus Status => Disposals > 0
            ? throw new ObjectDisposedException(nameof(FakeOutput))
            : new AudioStreamStatus(Failed ? AudioStreamState.Failed : AudioStreamState.Running, rate, 2, 256, 256, 256, 256, 0, 0, 0, 0, 0, TimeSpan.Zero, Failed ? AudioStreamError.DeviceNotAvailable : AudioStreamError.None, 0, TimeSpan.Zero, TimeSpan.Zero, 0, 0, 0, 0);

        public void Start()
        {
        }

        public void Dispose() => Disposals++;
    }
}
