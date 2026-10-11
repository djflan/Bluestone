namespace Bluestone.Audio.Devices;

/// <summary>An open output stream: <see cref="AudioOutput"/> on a real device, or a test's.</summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>The rate the stream runs at.</summary>
    int SampleRate { get; }

    int Channels { get; }

    /// <summary>Frames rendered ahead (buffered mode), or 0 in direct mode.</summary>
    int QueuedFrames { get; }

    /// <summary>
    /// Changes how far ahead a buffered stream renders, while it runs (ADR 0035): a smaller queue drains, a larger one
    /// fills, and the audio stays continuous because the device plays every frame either way. At least one frame, at
    /// most what the stream was opened with; ignored in direct mode.
    /// </summary>
    void SetQueuedFrames(int frames);

    /// <summary>How the render-ahead thread is scheduled, in words; null in direct mode or before it runs.</summary>
    string? RenderThreadPolicy { get; }

    /// <exception cref="ObjectDisposedException">The stream is closed.</exception>
    AudioStreamStatus Status { get; }

    /// <exception cref="AudioDeviceException">The device refused to start.</exception>
    void Start();
}

/// <summary>Where output streams come from: the operating system's devices (<see cref="SystemAudioBackend"/>) or a test's.</summary>
public interface IAudioBackend
{
    /// <summary>The output devices present now.</summary>
    IReadOnlyList<AudioDeviceInfo> ListDevices();

    /// <exception cref="AudioDeviceException">The device is missing or cannot be opened as asked.</exception>
    IAudioOutput Open(IAudioRenderer renderer, AudioStreamRequest request);
}

/// <summary>The operating system's audio devices, through the native layer (ADR 0032).</summary>
public sealed class SystemAudioBackend : IAudioBackend
{
    public static SystemAudioBackend Instance { get; } = new();

    public IReadOnlyList<AudioDeviceInfo> ListDevices() => AudioDevices.List();

    public IAudioOutput Open(IAudioRenderer renderer, AudioStreamRequest request) => AudioOutput.Open(renderer, request);
}
