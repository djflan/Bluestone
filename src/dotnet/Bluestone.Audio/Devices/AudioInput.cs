using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bluestone.Audio.Devices;

/// <summary>One captured block, as the device delivered it.</summary>
/// <param name="FramePosition">Frames captured before this block since the stream started.</param>
/// <param name="CallbackTimestamp">When the callback began (<see cref="Stopwatch.GetTimestamp"/>).</param>
/// <param name="InputLatency">From when the block's first frame was captured until the callback (zero when the device does not say).</param>
/// <param name="SampleRate">The stream's rate.</param>
public readonly record struct AudioCaptureBlock(long FramePosition, long CallbackTimestamp, TimeSpan InputLatency, int SampleRate)
{
    /// <summary>When the block's first frame was captured, on the <see cref="Stopwatch"/> clock.</summary>
    public long CapturedTimestamp => CallbackTimestamp - (long)(InputLatency.TotalSeconds * Stopwatch.Frequency);
}

/// <summary>Receives captured audio on the device's audio thread; must not block or allocate.</summary>
/// <param name="interleaved">The block, <paramref name="frames"/> × <paramref name="channels"/> samples, valid only during the call.</param>
/// <param name="frames">Frames in the block.</param>
/// <param name="channels">Samples per frame.</param>
/// <param name="block">Where and when the block was captured.</param>
public delegate void AudioCaptureHandler(ReadOnlySpan<float> interleaved, int frames, int channels, in AudioCaptureBlock block);

/// <summary>A snapshot of an input stream: rate, channels, counts, and the input latency the device reports.</summary>
public readonly record struct AudioInputStatus(int SampleRate, int Channels, long Callbacks, long Frames, long Overruns, TimeSpan InputLatency)
{
    /// <summary>Captured blocks that were too large for the conversion buffer and were not delivered.</summary>
    public long OversizedBlocks { get; init; }
}

/// <summary>
/// An audio input stream on a device (through the <c>bluestone_audio</c> Rust crate): each captured block is handed to
/// a handler on the device's audio thread, with when it was captured. Opened stopped.
/// </summary>
public sealed unsafe class AudioInput : IDisposable
{
    private const int ErrorCapacity = 512;
    private readonly AudioCaptureHandler _handler;
    private GCHandle _self;
    private nint _handle;
    private long _faults;
    private bool _asio;

    private AudioInput(AudioCaptureHandler handler) => _handler = handler;

    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    /// <summary>Exceptions that escaped the handler (that block was dropped).</summary>
    public long HandlerFaults => Interlocked.Read(ref _faults);

    /// <exception cref="AudioDeviceException">The library is missing, or the device cannot be opened as asked.</exception>
    public static AudioInput Open(AudioCaptureHandler handler, string? deviceId, int sampleRate = 0, int bufferFrames = 0, int channels = 0, string? host = null) =>
        host?.Equals("ASIO", StringComparison.OrdinalIgnoreCase) == true
            ? AsioThread.Invoke(() => OpenCore(handler, deviceId, sampleRate, bufferFrames, channels, host))
            : OpenCore(handler, deviceId, sampleRate, bufferFrames, channels, host);

    private static AudioInput OpenCore(AudioCaptureHandler handler, string? deviceId, int sampleRate, int bufferFrames, int channels, string? host)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!NativeMethods.TryCheck(out var problem))
        {
            throw new AudioDeviceException(problem!);
        }

        var stream = new AudioInput(handler) { _asio = host?.Equals("ASIO", StringComparison.OrdinalIgnoreCase) == true };
        stream._self = GCHandle.Alloc(stream);
        var native = new NativeMethods.StreamRequest
        {
            StructSize = (uint)sizeof(NativeMethods.StreamRequest),
            SampleRate = (uint)Math.Max(0, sampleRate),
            BufferFrames = (uint)Math.Max(0, host?.Equals("ASIO", StringComparison.OrdinalIgnoreCase) == true ? 0 : bufferFrames),
            Channels = (uint)Math.Max(0, channels),
        };
        var id = deviceId is null ? null : System.Text.Encoding.UTF8.GetBytes(deviceId + "\0");
        var hostId = host is null ? null : System.Text.Encoding.UTF8.GetBytes(host + "\0");
        var error = stackalloc byte[ErrorCapacity];
        error[0] = 0;
        fixed (byte* hostPointer = hostId)
        fixed (byte* idPointer = id)
        {
            stream._handle = NativeMethods.OpenInput(hostPointer, idPointer, &native, &CaptureCallback, GCHandle.ToIntPtr(stream._self), error, ErrorCapacity);
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

    public AudioInputStatus Status
    {
        get
        {
            var data = new NativeMethods.InputStatusData { StructSize = (uint)sizeof(NativeMethods.InputStatusData) };
            if (_handle == 0 || NativeMethods.InputStatus(_handle, &data) != 0)
            {
                return default;
            }

            return new AudioInputStatus((int)data.SampleRate, (int)data.Channels, (long)data.Callbacks, (long)data.Frames, (long)data.Overruns, TimeSpan.FromTicks(data.InputLatencyNanoseconds / 100))
            {
                OversizedBlocks = (long)data.OversizedBlocks,
            };
        }
    }

    /// <exception cref="AudioDeviceException">The device refused.</exception>
    public void Start()
    {
        if (_asio)
        {
            AsioThread.Invoke(StartCore);
            return;
        }

        StartCore();
    }

    private void StartCore() => Control(&NativeMethods.InputStart);

    /// <exception cref="AudioDeviceException">The device refused.</exception>
    public void Stop()
    {
        if (_asio)
        {
            AsioThread.Invoke(StopCore);
            return;
        }

        StopCore();
    }

    private void StopCore() => Control(&NativeMethods.InputStop);

    /// <summary>Closes the stream; the handler is never called after this returns.</summary>
    public void Dispose()
    {
        if (_asio)
        {
            AsioThread.Invoke(DisposeCore);
            return;
        }

        DisposeCore();
    }

    private void DisposeCore()
    {
        if (_handle != 0)
        {
            NativeMethods.InputClose(_handle);
            _handle = 0;
            _self.Free();
        }
    }

    private void Control(delegate*<nint, byte*, nuint, int> control)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        var error = stackalloc byte[ErrorCapacity];
        error[0] = 0;
        if (control(_handle, error, ErrorCapacity) != 0)
        {
            throw new AudioDeviceException(NativeMethods.ErrorText(new ReadOnlySpan<byte>(error, ErrorCapacity)));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CaptureCallback(nint user, float* buffer, uint frames, uint channels, NativeMethods.CaptureInfo* info)
    {
        var entered = Stopwatch.GetTimestamp();
        if (GCHandle.FromIntPtr(user).Target is not AudioInput stream)
        {
            return;
        }

        var block = new AudioCaptureBlock((long)info->FramePosition, entered, TimeSpan.FromTicks(info->InputLatencyNanoseconds / 100), (int)info->SampleRate);
        try
        {
            stream._handler(new ReadOnlySpan<float>(buffer, checked((int)(frames * channels))), (int)frames, (int)channels, block);
        }
#pragma warning disable CA1031 // Nothing may unwind into the native audio thread; the block is dropped.
        catch (Exception)
#pragma warning restore CA1031
        {
            Interlocked.Increment(ref stream._faults);
        }
    }
}
