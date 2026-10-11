using System.Runtime.InteropServices;

namespace Bluestone.Audio.Devices;

/// <summary>
/// The C ABI of the <c>bluestone_audio</c> Rust crate (src/native/crates/bluestone_audio). Layouts here must match its
/// <c>#[repr(C)]</c> structs; <see cref="AbiVersion"/> is checked before the first call.
/// </summary>
internal static unsafe partial class NativeMethods
{
    public const uint AbiVersion = 1;
    public const uint FlagDiscontinuity = 1;
    public const int HistogramBuckets = 2001;
    public const int HistogramBucketMicroseconds = 10;
    private const string Library = "bluestone_audio";

    [LibraryImport(Library, EntryPoint = "bluestone_audio_abi_version")]
    public static partial uint GetAbiVersion();

    [LibraryImport(Library, EntryPoint = "bluestone_audio_devices_json")]
    public static partial long DevicesJson(byte* buffer, nuint capacity);

    [LibraryImport(Library, EntryPoint = "bluestone_audio_open_output")]
    public static partial nint OpenOutput(
        byte* deviceId,
        StreamRequest* request,
        delegate* unmanaged[Cdecl]<nint, float*, uint, uint, BlockInfo*, void> render,
        nint userData,
        byte* error,
        nuint errorCapacity);

    [LibraryImport(Library, EntryPoint = "bluestone_audio_open_output_buffered")]
    public static partial nint OpenOutputBuffered(byte* deviceId, StreamRequest* request, uint ringFrames, byte* error, nuint errorCapacity);

    [LibraryImport(Library, EntryPoint = "bluestone_audio_ring_write")]
    public static partial long RingWrite(nint stream, float* samples, uint frames);

    /// <summary>Blocks in native code (so a .NET garbage collection does not hold the thread there).</summary>
    [LibraryImport(Library, EntryPoint = "bluestone_audio_ring_wait")]
    public static partial long RingWait(nint stream, uint belowFrames, uint timeoutMicroseconds);

    [LibraryImport(Library, EntryPoint = "bluestone_audio_start")]
    public static partial int Start(nint stream, byte* error, nuint errorCapacity);

    [LibraryImport(Library, EntryPoint = "bluestone_audio_stop")]
    public static partial int Stop(nint stream, byte* error, nuint errorCapacity);

    [LibraryImport(Library, EntryPoint = "bluestone_audio_stream_status")]
    public static partial int StreamStatus(nint stream, StreamStatusData* status);

    [LibraryImport(Library, EntryPoint = "bluestone_audio_stream_histogram")]
    public static partial long StreamHistogram(nint stream, ulong* buckets, nuint count);

    [LibraryImport(Library, EntryPoint = "bluestone_audio_close")]
    public static partial void Close(nint stream);

    [StructLayout(LayoutKind.Sequential)]
    public struct BlockInfo
    {
        public ulong FramePosition;
        public long OutputLatencyNanoseconds;
        public ulong CallbackIndex;
        public uint SampleRate;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StreamRequest
    {
        public uint StructSize;
        public uint SampleRate;
        public uint BufferFrames;
        public uint Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StreamStatusData
    {
        public uint StructSize;
        public uint State;
        public uint SampleRate;
        public uint Channels;
        public uint RequestedBufferFrames;
        public uint LastBufferFrames;
        public uint MinBufferFrames;
        public uint MaxBufferFrames;
        public ulong Callbacks;
        public ulong Frames;
        public ulong Xruns;
        public ulong Discontinuities;
        public ulong OversizedBlocks;
        public long OutputLatencyNanoseconds;
        public uint LastError;
        public uint Errors;
        public uint SampleFormat;
        public uint Reserved;
        public long RenderMaxNanoseconds;
        public ulong RenderTotalNanoseconds;
        public ulong LateRenders;
        public ulong RingCapacity;
        public ulong RingFill;
        public ulong RingUnderrunFrames;
    }

    /// <summary>Whether the library can be loaded and speaks this ABI version; the reason when it cannot.</summary>
    public static bool TryCheck(out string? problem)
    {
        try
        {
            var version = GetAbiVersion();
            problem = version == AbiVersion ? null : $"The audio library speaks ABI version {version}; Bluestone needs {AbiVersion}.";
        }
        catch (DllNotFoundException ex)
        {
            problem = $"The audio library could not be loaded: {ex.Message}";
        }
        catch (EntryPointNotFoundException ex)
        {
            problem = $"The audio library is incomplete: {ex.Message}";
        }

        return problem is null;
    }

    public static string ErrorText(ReadOnlySpan<byte> error)
    {
        var end = error.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(end < 0 ? error : error[..end]);
    }
}
