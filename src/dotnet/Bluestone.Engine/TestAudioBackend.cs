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

    private static readonly AudioDeviceInfo Device = new(DeviceId, "Test output", "Test", true, 48_000, 2, [44_100, 48_000, 96_000], [2], 32, 8192);

    public IReadOnlyList<AudioDeviceInfo> ListDevices() => [Device];

    public IAudioOutput Open(IAudioRenderer renderer, AudioStreamRequest request) =>
        request.DeviceId is null or DeviceId
            ? new Output(request.SampleRate > 0 ? request.SampleRate : Device.DefaultSampleRate, request.BufferFrames > 0 ? request.BufferFrames : 256, request.QueuedFrames)
            : throw new AudioDeviceException($"There is no audio device \"{request.DeviceId}\" here.");

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
            : new AudioStreamStatus(AudioStreamState.Running, rate, 2, buffer, buffer, buffer, buffer, 0, 0, 0, 0, 0, TimeSpan.Zero, AudioStreamError.None, 0, TimeSpan.Zero, TimeSpan.Zero, 0, _opened, 0, 0);

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
