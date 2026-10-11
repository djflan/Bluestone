using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bluestone.Platform.Alsa;

/// <summary>ALSA sequencer and MIDI event encoder functions (alsa/seq.h, alsa/seq_midi_event.h).</summary>
[SupportedOSPlatform("linux")]
internal static unsafe partial class Alsa
{
    private const string Library = "libasound.so.2";

    public const int OpenOutput = 1;
    public const int NonBlock = 1;

    public const uint CapRead = 1 << 0;
    public const uint CapWrite = 1 << 1;
    public const uint CapSubsRead = 1 << 5;
    public const uint CapSubsWrite = 1 << 6;
    public const uint CapNoExport = 1 << 7;

    public const uint TypeMidiGeneric = 1 << 1;
    public const uint TypeHardware = 1 << 16;
    public const uint TypeSoftware = 1 << 17;
    public const uint TypeSynthesizer = 1 << 18;
    public const uint TypePort = 1 << 19;
    public const uint TypeApplication = 1 << 20;

    /// <summary>SND_SEQ_QUEUE_DIRECT: deliver now, bypassing any queue.</summary>
    public const byte QueueDirect = 253;

    /// <summary>SND_SEQ_ADDRESS_SUBSCRIBERS: every port subscribed to the source port.</summary>
    public const byte AddressSubscribers = 254;

    /// <summary>SND_SEQ_ADDRESS_UNKNOWN.</summary>
    public const byte AddressUnknown = 253;

    public const int ENOENT = 2;
    public const int ENXIO = 6;
    public const int EAGAIN = 11;
    public const int ENODEV = 19;

    /// <summary>
    /// snd_seq_event_t. 28 bytes on every architecture: an 8-byte header, an 8-byte timestamp
    /// union, source and destination addresses, and a 12-byte data union (packed for SysEx).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    public struct SeqEvent
    {
        [FieldOffset(0)]
        public byte Type;

        [FieldOffset(3)]
        public byte Queue;

        [FieldOffset(13)]
        public byte SourcePort;

        [FieldOffset(14)]
        public byte DestClient;

        [FieldOffset(15)]
        public byte DestPort;
    }

    public static string Error(int code) => Marshal.PtrToStringUTF8(snd_strerror(code)) ?? code.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int snd_seq_open(out IntPtr seq, string name, int streams, int mode);

    [LibraryImport(Library)]
    public static partial int snd_seq_close(IntPtr seq);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int snd_seq_set_client_name(IntPtr seq, string name);

    [LibraryImport(Library)]
    public static partial int snd_seq_client_id(IntPtr seq);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int snd_seq_create_simple_port(IntPtr seq, string name, uint caps, uint type);

    [LibraryImport(Library)]
    public static partial int snd_seq_delete_simple_port(IntPtr seq, int port);

    [LibraryImport(Library)]
    public static partial nuint snd_seq_client_info_sizeof();

    [LibraryImport(Library)]
    public static partial void snd_seq_client_info_set_client(IntPtr info, int client);

    [LibraryImport(Library)]
    public static partial int snd_seq_client_info_get_client(IntPtr info);

    [LibraryImport(Library)]
    public static partial IntPtr snd_seq_client_info_get_name(IntPtr info);

    [LibraryImport(Library)]
    public static partial int snd_seq_query_next_client(IntPtr seq, IntPtr info);

    [LibraryImport(Library)]
    public static partial nuint snd_seq_port_info_sizeof();

    [LibraryImport(Library)]
    public static partial void snd_seq_port_info_set_client(IntPtr info, int client);

    [LibraryImport(Library)]
    public static partial void snd_seq_port_info_set_port(IntPtr info, int port);

    [LibraryImport(Library)]
    public static partial int snd_seq_port_info_get_port(IntPtr info);

    [LibraryImport(Library)]
    public static partial IntPtr snd_seq_port_info_get_name(IntPtr info);

    [LibraryImport(Library)]
    public static partial uint snd_seq_port_info_get_capability(IntPtr info);

    [LibraryImport(Library)]
    public static partial uint snd_seq_port_info_get_type(IntPtr info);

    [LibraryImport(Library)]
    public static partial int snd_seq_query_next_port(IntPtr seq, IntPtr info);

    [LibraryImport(Library)]
    public static partial int snd_seq_event_output_direct(IntPtr seq, SeqEvent* ev);

    [LibraryImport(Library)]
    public static partial int snd_midi_event_new(nuint bufferSize, out IntPtr encoder);

    [LibraryImport(Library)]
    public static partial void snd_midi_event_free(IntPtr encoder);

    [LibraryImport(Library)]
    public static partial void snd_midi_event_reset_encode(IntPtr encoder);

    [LibraryImport(Library)]
    public static partial CLong snd_midi_event_encode(IntPtr encoder, byte* buffer, CLong count, SeqEvent* ev);

    [LibraryImport(Library)]
    private static partial IntPtr snd_strerror(int error);
}
