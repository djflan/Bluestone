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
/// <param name="DeviceId">An <see cref="AudioDeviceInfo.Id"/>, or null for the selected host's default output.</param>
/// <param name="SampleRate">In Hz.</param>
/// <param name="BufferFrames">Frames per callback; the device may clamp it to its range, and ASIO uses the driver default.</param>
/// <param name="Channels">Output channels; the engine writes left and right to the first two.</param>
/// <param name="Host">The audio host, or null for the system default.</param>
/// <param name="QueuedFrames">
/// 0: the device callback calls the renderer directly (lowest latency; a .NET garbage collection that happens while
/// a callback is due delays it). Above 0: buffered: a .NET render thread keeps this many frames queued ahead of the
/// device, and the callback (native code) only copies them, so a collection shorter than the queue is not heard. The
/// queue adds this many frames of latency. See ADR 0032.
/// </param>
public sealed record AudioStreamRequest(string? DeviceId = null, int SampleRate = 0, int BufferFrames = 0, int Channels = 0, int QueuedFrames = 0, string? Host = null);

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

/// <summary>
/// A stream's counters, read lock-free from the native layer. Render times are measured in the native callback around
/// the call into .NET, so they include entering the runtime and any wait for a garbage collection to finish;
/// <see cref="LateRenders"/> counts calls longer than their block lasts.
/// </summary>
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
    int Errors,
    TimeSpan RenderMax,
    TimeSpan RenderTotal,
    long LateRenders,
    long QueueCapacity,
    long QueuedFrames,
    long QueueUnderrunFrames)
{
    /// <summary>The device callback's actual buffer size, or 0 until the device reports it.</summary>
    public int DeviceBufferFrames { get; init; }
}

public sealed class AudioDeviceException(string message) : Exception(message);

/// <summary>
/// An open output stream on a device. In direct mode the renderer is called on the device's own audio thread
/// (cpal's); in buffered mode (<see cref="AudioStreamRequest.QueuedFrames"/>) on a dedicated .NET thread that keeps
/// the queue filled. Either way it is called from <see cref="Start"/> until <see cref="Stop"/> or <see cref="Dispose"/>,
/// and never after Dispose. Control methods are for one control thread at a time.
/// </summary>
public sealed unsafe class AudioOutput : IAudioOutput
{
    private const int ErrorCapacity = 512;
    private readonly IAudioRenderer _renderer;
    private readonly ManualResetEventSlim _queuePrefilled = new(false);
    private bool _asio;
    private GCHandle _self;
    private nint _handle;
    private long _renderFaults;
    private int _queued;
    private int _openedQueue;
    private int _renderBlock;
    private Thread? _renderThread;
    private volatile bool _rendering;
    private volatile string? _renderThreadPolicy;

    private AudioOutput(IAudioRenderer renderer) => _renderer = renderer;

    /// <summary>Frames the render thread keeps queued (buffered mode), or 0 in direct mode.</summary>
    public int QueuedFrames => Volatile.Read(ref _queued);

    public void SetQueuedFrames(int frames)
    {
        if (_openedQueue > 0)
        {
            Volatile.Write(ref _queued, Math.Clamp(frames, 1, _openedQueue));
        }
    }

    /// <summary>
    /// How the render-ahead thread is scheduled (see <see cref="RenderThreadScheduling"/>), once it runs; null in direct
    /// mode, where the device's own audio thread renders.
    /// </summary>
    public string? RenderThreadPolicy => _renderThreadPolicy;

    /// <summary>The rate the stream runs at.</summary>
    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    /// <summary>Exceptions that escaped the renderer (the block was output as silence).</summary>
    public long RenderFaults => Interlocked.Read(ref _renderFaults);

    /// <exception cref="AudioDeviceException">The library is missing, or the device cannot be opened as asked.</exception>
    public static AudioOutput Open(IAudioRenderer renderer, AudioStreamRequest request) =>
        request.Host?.Equals("ASIO", StringComparison.OrdinalIgnoreCase) == true
            ? AsioThread.Invoke(() => OpenCore(renderer, request))
            : OpenCore(renderer, request);

    private static AudioOutput OpenCore(IAudioRenderer renderer, AudioStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(request);
        if (!NativeMethods.TryCheck(out var problem))
        {
            throw new AudioDeviceException(problem!);
        }

        var stream = new AudioOutput(renderer) { _asio = request.Host?.Equals("ASIO", StringComparison.OrdinalIgnoreCase) == true };
        stream._self = GCHandle.Alloc(stream);
        var native = new NativeMethods.StreamRequest
        {
            StructSize = (uint)sizeof(NativeMethods.StreamRequest),
            SampleRate = (uint)Math.Max(0, request.SampleRate),
            BufferFrames = (uint)Math.Max(0, request.Host?.Equals("ASIO", StringComparison.OrdinalIgnoreCase) == true ? 0 : request.BufferFrames),
            Channels = (uint)Math.Max(0, request.Channels),
        };
        var id = request.DeviceId is null ? null : System.Text.Encoding.UTF8.GetBytes(request.DeviceId + "\0");
        var host = request.Host is null ? null : System.Text.Encoding.UTF8.GetBytes(request.Host + "\0");
        var error = stackalloc byte[ErrorCapacity];
        error[0] = 0;
        stream._queued = Math.Max(0, request.QueuedFrames);
        stream._openedQueue = stream._queued;
        fixed (byte* hostPointer = host)
        fixed (byte* idPointer = id)
        {
            stream._handle = stream._queued == 0
            ? NativeMethods.OpenOutput(hostPointer, idPointer, &native, &RenderCallback, GCHandle.ToIntPtr(stream._self), error, ErrorCapacity)
            : NativeMethods.OpenOutputBuffered(hostPointer, idPointer, &native, (uint)(2 * stream._queued) + 8192, error, ErrorCapacity);
        }

        if (stream._handle == 0)
        {
            stream._self.Free();
            stream._queuePrefilled.Dispose();
            throw new AudioDeviceException(NativeMethods.ErrorText(new ReadOnlySpan<byte>(error, ErrorCapacity)));
        }

        var status = stream.Status;
        stream.SampleRate = status.SampleRate;
        stream.Channels = status.Channels;
        stream._renderBlock = Math.Min(stream._queued, request.BufferFrames > 0 ? request.BufferFrames : 256);
        return stream;
    }

    public void Start()
    {
        if (_asio)
        {
            AsioThread.Invoke(StartCore);
            return;
        }

        StartCore();
    }

    private void StartCore()
    {
        if (_queued > 0 && _renderThread is null)
        {
            _queuePrefilled.Reset();
            _rendering = true;
            _renderThread = new Thread(RenderAhead) { IsBackground = true, Name = "Bluestone audio render", Priority = ThreadPriority.Highest };
            _renderThread.Start();
            if (!_queuePrefilled.Wait(TimeSpan.FromMilliseconds(250)))
            {
                StopRenderThread();
                throw new AudioDeviceException("The render-ahead queue could not be filled before starting the audio device.");
            }
        }

        try
        {
            Control(started: true);
        }
        catch
        {
            StopRenderThread();
            throw;
        }
    }

    public void Stop()
    {
        if (_asio)
        {
            AsioThread.Invoke(StopCore);
            return;
        }

        StopCore();
    }

    private void StopCore()
    {
        Control(started: false);
        StopRenderThread();
    }

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
                (int)data.Errors,
                TimeSpan.FromTicks(data.RenderMaxNanoseconds / 100),
                TimeSpan.FromTicks((long)(data.RenderTotalNanoseconds / 100)),
                (long)data.LateRenders,
                (long)data.RingCapacity,
                (long)data.RingFill,
                (long)data.RingUnderrunFrames)
            {
                DeviceBufferFrames = (int)Math.Min(data.DeviceBufferFrames, (uint)int.MaxValue),
            };
        }
    }

    /// <summary>
    /// How many render calls fell in each 10 µs bucket of duration (as measured natively, see
    /// <see cref="AudioStreamStatus"/>); the last bucket counts everything longer than 20 ms.
    /// </summary>
    public long[] RenderHistogram()
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        var buckets = new ulong[NativeMethods.HistogramBuckets];
        fixed (ulong* pointer = buckets)
        {
            NativeMethods.StreamHistogram(_handle, pointer, (nuint)buckets.Length);
        }

        return [.. buckets.Select(b => (long)b)];
    }

    /// <summary>The render duration below which <paramref name="percent"/> of calls finished, from <see cref="RenderHistogram"/>.</summary>
    public static TimeSpan Percentile(long[] histogram, double percent)
    {
        ArgumentNullException.ThrowIfNull(histogram);
        var total = histogram.Sum();
        if (total == 0)
        {
            return TimeSpan.Zero;
        }

        var target = (long)Math.Ceiling(total * percent / 100);
        long seen = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            seen += histogram[i];
            if (seen >= target)
            {
                return TimeSpan.FromMicroseconds((i + 1) * NativeMethods.HistogramBucketMicroseconds);
            }
        }

        return TimeSpan.FromMicroseconds(histogram.Length * NativeMethods.HistogramBucketMicroseconds);
    }

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
        if (_handle == 0)
        {
            return;
        }

        StopRenderThread();

        // Closing joins the audio thread, so the renderer and this object's handle are unused afterwards.
        NativeMethods.Close(_handle);
        _handle = 0;
        _self.Free();
        _queuePrefilled.Dispose();
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

    private TimeSpan ReadOutputLatency() => ReadDeviceTiming().Latency;

    // The device's output latency, and the frames it takes per callback (0 until the first callback).
    private (TimeSpan Latency, int BufferFrames) ReadDeviceTiming()
    {
        var data = new NativeMethods.StreamStatusData { StructSize = (uint)sizeof(NativeMethods.StreamStatusData) };
        return NativeMethods.StreamStatus(_handle, &data) == 0
            ? (TimeSpan.FromTicks(data.OutputLatencyNanoseconds / 100), (int)data.LastBufferFrames)
            : (TimeSpan.Zero, 0);
    }

    private void StopRenderThread()
    {
        _rendering = false;
        _renderThread?.Join();
        _renderThread = null;
    }

    // Buffered mode: keeps the queue at _queued frames, rendering blocks of _renderBlock. The wait happens in native
    // code, so this thread is not where a garbage collection holds it; a collection only delays the next block.
    private void RenderAhead()
    {
        _renderThreadPolicy = RenderThreadScheduling.PromoteCurrentThread(TimeSpan.FromSeconds((double)_queued / SampleRate));
        var channels = Channels;
        var block = new float[_renderBlock * channels];
        var (latency, deviceFrames) = ReadDeviceTiming();
        long written = 0;
        var checks = 0;
        while (_rendering)
        {
            // The target is read once per wake-up, so a change takes effect at the next one.
            var target = Volatile.Read(ref _queued);
            if (!_queuePrefilled.IsSet)
            {
                target += _renderBlock;
            }
            var queued = NativeMethods.RingWait(_handle, (uint)target, 2000);
            if (++checks % 256 == 0 || deviceFrames == 0)
            {
                (latency, deviceFrames) = ReadDeviceTiming();
            }

            while (_rendering && queued < target)
            {
                var frames = (int)Math.Min(_renderBlock, target - queued);
                var now = Stopwatch.GetTimestamp();
                // This thread wakes when a callback has just taken a block, so what is queued is taken from the next
                // callback on: a frame written now is taken one device buffer after the queue ahead of it, then heard
                // after the output latency. Measured with a loopback (ADR 0043): without the device buffer, frames
                // were heard one buffer later than this said.
                var heardIn = latency + TimeSpan.FromSeconds((double)(queued + deviceFrames) / SampleRate);
                Array.Clear(block);
                var samples = block.AsSpan(0, frames * channels);
                try
                {
                    _renderer.Render(samples, frames, channels, new AudioDeviceBlock(written, now, heardIn, SampleRate, false));
                }
#pragma warning disable CA1031 // The render thread must keep the device fed; a failed block becomes silence.
                catch (Exception)
#pragma warning restore CA1031
                {
                    samples.Clear();
                    Interlocked.Increment(ref _renderFaults);
                }

                fixed (float* pointer = block)
                {
                    var taken = NativeMethods.RingWrite(_handle, pointer, (uint)frames);
                    written += taken;
                    queued += taken;
                    if (taken < frames)
                    {
                        break;
                    }
                }
            }

            if (queued >= target)
            {
                _queuePrefilled.Set();
            }
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
