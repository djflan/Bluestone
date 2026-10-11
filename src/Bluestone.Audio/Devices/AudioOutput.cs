using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bluestone.Audio.Devices;

/// <summary>What the device says about one callback.</summary>
/// <param name="FramePosition">Frames handed to the device before this block since the stream started.</param>
/// <param name="CallbackTimestamp">When the callback started, in <see cref="Stopwatch"/> ticks.</param>
/// <param name="OutputLatency">From the callback until the block's first frame is heard (zero when the device does not say).</param>
/// <param name="SampleRate">The stream's rate in Hz.</param>
/// <param name="Discontinuity">The callback came later than the previous block lasted: audio was probably dropped.</param>
public readonly record struct AudioDeviceBlock(long FramePosition, long CallbackTimestamp, TimeSpan OutputLatency, int SampleRate, bool Discontinuity)
{
    /// <summary>When the block's first frame is heard, in <see cref="Stopwatch"/> ticks.</summary>
    public long HeardTimestamp => CallbackTimestamp + (long)(OutputLatency.TotalSeconds * Stopwatch.Frequency);
}

/// <summary>
/// Fills device buffers. Called on the device's audio thread: implementations must not block, allocate, do I/O, log,
/// or throw. The buffer is interleaved, frames × channels samples, and holds silence when called.
/// </summary>
public interface IAudioRenderer
{
    void Render(Span<float> interleaved, int frames, int channels, in AudioDeviceBlock block);
}

/// <summary>What to open. Zero means the device's default.</summary>
/// <param name="DeviceId">An <see cref="AudioDeviceInfo.Id"/>, or null for the default output.</param>
/// <param name="SampleRate">In Hz.</param>
/// <param name="BufferFrames">Frames per callback; the device may clamp it to its range.</param>
/// <param name="Channels">Output channels; the engine writes left and right to the first two.</param>
public sealed record AudioStreamRequest(string? DeviceId = null, int SampleRate = 0, int BufferFrames = 0, int Channels = 0);

public enum AudioStreamState
{
    Stopped,
    Running,

    /// <summary>The device went away or the stream became invalid; it must be closed and opened again.</summary>
    Failed,
}

public enum AudioStreamError
{
    None,
    DeviceNotAvailable,
    StreamInvalidated,
    DeviceChanged,
    Backend,
    Other,
}

/// <summary>A stream's counters, read lock-free from the native layer.</summary>
public sealed record AudioStreamStatus(
    AudioStreamState State,
    int SampleRate,
    int Channels,
    int RequestedBufferFrames,
    int LastBufferFrames,
    int MinBufferFrames,
    int MaxBufferFrames,
    long Callbacks,
    long Frames,
    long Xruns,
    long Discontinuities,
    long OversizedBlocks,
    TimeSpan OutputLatency,
    AudioStreamError LastError,
    int Errors);

public sealed class AudioDeviceException(string message) : Exception(message);

/// <summary>
/// An open output stream on a device. The renderer is called on the device's own audio thread (cpal's) from the
/// moment <see cref="Start"/> is called until <see cref="Stop"/> or <see cref="Dispose"/>; after Dispose it is never
/// called again. Control methods are for one control thread at a time.
/// </summary>
public sealed unsafe class AudioOutput : IDisposable
{
    private const int ErrorCapacity = 512;
    private readonly IAudioRenderer _renderer;
    private GCHandle _self;
    private nint _handle;
    private long _renderFaults;

    private AudioOutput(IAudioRenderer renderer) => _renderer = renderer;

    /// <summary>The rate the stream runs at.</summary>
    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    /// <summary>Exceptions that escaped the renderer (the block was output as silence).</summary>
    public long RenderFaults => Interlocked.Read(ref _renderFaults);

    /// <exception cref="AudioDeviceException">The library is missing, or the device cannot be opened as asked.</exception>
    public static AudioOutput Open(IAudioRenderer renderer, AudioStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(request);
        if (!NativeMethods.TryCheck(out var problem))
        {
            throw new AudioDeviceException(problem!);
        }

        var stream = new AudioOutput(renderer);
        stream._self = GCHandle.Alloc(stream);
        var native = new NativeMethods.StreamRequest
        {
            StructSize = (uint)sizeof(NativeMethods.StreamRequest),
            SampleRate = (uint)Math.Max(0, request.SampleRate),
            BufferFrames = (uint)Math.Max(0, request.BufferFrames),
            Channels = (uint)Math.Max(0, request.Channels),
        };
        var id = request.DeviceId is null ? null : System.Text.Encoding.UTF8.GetBytes(request.DeviceId + "\0");
        var error = stackalloc byte[ErrorCapacity];
        error[0] = 0;
        fixed (byte* idPointer = id)
        {
            stream._handle = NativeMethods.OpenOutput(idPointer, &native, &RenderCallback, GCHandle.ToIntPtr(stream._self), error, ErrorCapacity);
        }

        if (stream._handle == 0)
        {
            stream._self.Free();
            throw new AudioDeviceException(NativeMethods.ErrorText(new ReadOnlySpan<byte>(error, ErrorCapacity)));
        }

        var status = stream.Status;
        stream.SampleRate = status.SampleRate;
        stream.Channels = status.Channels;
        return stream;
    }

    public void Start() => Control(started: true);

    public void Stop() => Control(started: false);

    public AudioStreamStatus Status
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            var data = new NativeMethods.StreamStatusData { StructSize = (uint)sizeof(NativeMethods.StreamStatusData) };
            if (NativeMethods.StreamStatus(_handle, &data) != 0)
            {
                throw new AudioDeviceException("The audio stream's status could not be read.");
            }

            return new AudioStreamStatus(
                (AudioStreamState)data.State,
                (int)data.SampleRate,
                (int)data.Channels,
                (int)data.RequestedBufferFrames,
                (int)data.LastBufferFrames,
                (int)data.MinBufferFrames,
                (int)data.MaxBufferFrames,
                (long)data.Callbacks,
                (long)data.Frames,
                (long)data.Xruns,
                (long)data.Discontinuities,
                (long)data.OversizedBlocks,
                TimeSpan.FromTicks(data.OutputLatencyNanoseconds / 100),
                (AudioStreamError)Math.Min(data.LastError, (uint)AudioStreamError.Other),
                (int)data.Errors);
        }
    }

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }

        // Closing joins the audio thread, so the renderer and this object's handle are unused afterwards.
        NativeMethods.Close(_handle);
        _handle = 0;
        _self.Free();
    }

    private void Control(bool started)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        var error = stackalloc byte[ErrorCapacity];
        error[0] = 0;
        var result = started ? NativeMethods.Start(_handle, error, ErrorCapacity) : NativeMethods.Stop(_handle, error, ErrorCapacity);
        if (result != 0)
        {
            throw new AudioDeviceException(NativeMethods.ErrorText(new ReadOnlySpan<byte>(error, ErrorCapacity)));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RenderCallback(nint user, float* buffer, uint frames, uint channels, NativeMethods.BlockInfo* info)
    {
        var entered = Stopwatch.GetTimestamp();
        var samples = new Span<float>(buffer, checked((int)(frames * channels)));
        if (GCHandle.FromIntPtr(user).Target is not AudioOutput stream)
        {
            samples.Clear();
            return;
        }

        var block = new AudioDeviceBlock(
            (long)info->FramePosition,
            entered,
            TimeSpan.FromTicks(info->OutputLatencyNanoseconds / 100),
            (int)info->SampleRate,
            (info->Flags & NativeMethods.FlagDiscontinuity) != 0);
        try
        {
            stream._renderer.Render(samples, (int)frames, (int)channels, block);
        }
#pragma warning disable CA1031 // Nothing may unwind into the native audio thread; the block becomes silence.
        catch (Exception)
#pragma warning restore CA1031
        {
            samples.Clear();
            Interlocked.Increment(ref stream._renderFaults);
        }
    }
}
