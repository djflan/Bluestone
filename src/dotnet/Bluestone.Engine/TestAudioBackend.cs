using Bluestone.Audio.Devices;

namespace Bluestone.Engine;

/// <summary>
/// The audio device of an engine started for tests (<c>--test-audio</c>): one output that opens at the rate and buffer
/// asked for and never calls back, so nothing renders except <see cref="Protocol.TestRender"/>, through the same render
/// path a device callback takes. Integration tests use it to compare what the engine renders with what Bluestone renders
/// in one process, without an audio device.
/// </summary>
internal sealed class TestAudioBackend : IAudioBackend
{
    public const string DeviceId = "test";

    private static readonly AudioDeviceInfo WasapiDevice = new(DeviceId, "Test WASAPI output", "WASAPI", true, 48_000, 2, [48_000], [2], 32, 8192);
    private static readonly AudioDeviceInfo AsioDevice = new("asio-test", "Test ASIO output", "ASIO", false, 44_100, 2, [44_100], [2], 1024, 1024);

    public IReadOnlyList<AudioDeviceInfo> ListDevices() => [WasapiDevice, AsioDevice];

    public IAudioOutput Open(IAudioRenderer renderer, AudioStreamRequest request)
    {
        var hostDevice = request.Host == "ASIO" ? AsioDevice : WasapiDevice;
        if (request.DeviceId is not null && request.DeviceId != hostDevice.Id)
        {
            throw new AudioDeviceException($"There is no audio device \"{request.DeviceId}\" here.");
        }

        if (request.SampleRate > 0 && !hostDevice.SampleRates.Contains(request.SampleRate))
        {
            throw new AudioDeviceException($"The {hostDevice.Host} host does not support {request.SampleRate} Hz.");
        }

        return new Output(request.SampleRate > 0 ? request.SampleRate : hostDevice.DefaultSampleRate, request.BufferFrames > 0 ? request.BufferFrames : 256, request.QueuedFrames);
    }

    private sealed class Output(int rate, int buffer, int opened) : IAudioOutput
    {
        private bool _disposed;

        public int SampleRate => rate;

        public int Channels => 2;

        private readonly int _opened = opened;
        private int _queued = opened;

        public int QueuedFrames => _queued;

        public string? RenderThreadPolicy => null;

        public AudioStreamStatus Status => _disposed
            ? throw new ObjectDisposedException(nameof(Output))
            : new AudioStreamStatus(AudioStreamState.Running, rate, 2, buffer, buffer, buffer, buffer, 0, 0, 0, 0, 0, TimeSpan.Zero, AudioStreamError.None, 0, TimeSpan.Zero, TimeSpan.Zero, 0, _opened, 0, 0)
            {
                DeviceBufferFrames = buffer,
            };

        public void SetQueuedFrames(int frames)
        {
            if (_opened > 0)
            {
                _queued = Math.Clamp(frames, 1, _opened);
            }
        }

        public void Start()
        {
        }

        public void Dispose() => _disposed = true;
    }
}
