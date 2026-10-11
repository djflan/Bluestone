using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bluestone.Platform.Windows;

/// <summary>WinMM MIDI output functions (mmeapi.h).</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WinMm
{
    public const uint NoError = 0;
    public const uint InvalidHandle = 5;
    public const uint NoDriver = 6;
    public const uint StillPlaying = 65;
    public const uint NotReady = 67;
    public const uint NoDevice = 68;
    public const uint CallbackNull = 0;

    /// <summary>MOD_MIDIPORT: a hardware MIDI port.</summary>
    public const ushort TechnologyMidiPort = 1;

    /// <summary>MOD_SWSYNTH: a software synthesizer such as the Microsoft GS Wavetable Synth.</summary>
    public const ushort TechnologySoftwareSynth = 7;

    /// <summary>MIDIOUTCAPSW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MidiOutCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;
        public fixed char Name[32];
        public ushort Technology;
        public ushort Voices;
        public ushort Notes;
        public ushort ChannelMask;
        public uint Support;
    }

    /// <summary>MHDR_DONE: the driver has finished with a long-message buffer.</summary>
    public const uint HeaderDone = 1;

    /// <summary>
    /// MIDIHDR. Pointer-sized fields keep the layout right on 32- and 64-bit Windows; the reserved
    /// array is declared at its 64-bit size, which only over-allocates on 32-bit.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MidiHeader
    {
        public byte* Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nuint User;
        public uint Flags;
        public MidiHeader* Next;
        public nuint Reserved;
        public uint Offset;
        public fixed ulong ReservedArray[8];
    }

    [LibraryImport("winmm.dll")]
    public static partial uint midiOutGetNumDevs();

    [LibraryImport("winmm.dll")]
    public static partial uint midiOutGetDevCapsW(nuint deviceId, MidiOutCaps* caps, uint size);

    [LibraryImport("winmm.dll")]
    public static partial uint midiOutOpen(out IntPtr handle, uint deviceId, nuint callback, nuint instance, uint flags);

    [LibraryImport("winmm.dll")]
    public static partial uint midiOutShortMsg(IntPtr handle, uint message);

    [LibraryImport("winmm.dll")]
    public static partial uint midiOutPrepareHeader(IntPtr handle, MidiHeader* header, uint size);

    [LibraryImport("winmm.dll")]
    public static partial uint midiOutUnprepareHeader(IntPtr handle, MidiHeader* header, uint size);

    [LibraryImport("winmm.dll")]
    public static partial uint midiOutLongMsg(IntPtr handle, MidiHeader* header, uint size);

    [LibraryImport("winmm.dll")]
    public static partial uint midiOutReset(IntPtr handle);

    [LibraryImport("winmm.dll")]
    public static partial uint midiOutClose(IntPtr handle);
}
