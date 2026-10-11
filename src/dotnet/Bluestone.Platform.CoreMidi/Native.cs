using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bluestone.Platform.CoreMidi;

/// <summary>Minimal CoreMIDI, CoreFoundation, and mach bindings. Types follow the macOS SDK headers.</summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class Native
{
    private const string CoreMidi = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibSystem = "/usr/lib/libSystem.dylib";

    public const int NoError = 0;
    public const int MsgSetupChanged = 1;

    /// <summary>Offsets within MIDIPacketList / MIDIPacket, which are declared with 4-byte packing.</summary>
    public const int PacketListHeader = 4;
    public const int PacketHeader = 10;

    // CoreMIDI
    [LibraryImport(CoreMidi)]
    public static partial int MIDIClientCreate(IntPtr name, delegate* unmanaged<IntPtr, IntPtr, void> notifyProc, IntPtr notifyRefCon, out uint client);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIClientDispose(uint client);

    [LibraryImport(CoreMidi)]
    public static partial nuint MIDIGetNumberOfDestinations();

    [LibraryImport(CoreMidi)]
    public static partial uint MIDIGetDestination(nuint index);

    [LibraryImport(CoreMidi)]
    public static partial nuint MIDIGetNumberOfSources();

    [LibraryImport(CoreMidi)]
    public static partial uint MIDIGetSource(nuint index);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIObjectGetStringProperty(uint obj, IntPtr propertyId, out IntPtr value);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIObjectGetIntegerProperty(uint obj, IntPtr propertyId, out int value);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIObjectSetIntegerProperty(uint obj, IntPtr propertyId, int value);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIObjectFindByUniqueID(int uniqueId, out uint obj, out int objectType);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIEndpointGetEntity(uint endpoint, out uint entity);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIEntityGetDevice(uint entity, out uint device);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIOutputPortCreate(uint client, IntPtr portName, out uint port);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIInputPortCreate(uint client, IntPtr portName, delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> readProc, IntPtr refCon, out uint port);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIPortConnectSource(uint port, uint source, IntPtr connectionRefCon);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIPortDispose(uint port);

    [LibraryImport(CoreMidi)]
    public static partial int MIDISend(uint port, uint destination, byte* packetList);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIReceived(uint source, byte* packetList);

    [LibraryImport(CoreMidi)]
    public static partial int MIDISourceCreate(uint client, IntPtr name, out uint source);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIEndpointDispose(uint endpoint);

    // CoreFoundation
    [StructLayout(LayoutKind.Sequential)]
    public struct CFRange
    {
        public nint Location;
        public nint Length;
    }

    [LibraryImport(CoreFoundation)]
    public static partial IntPtr CFStringCreateWithCharacters(IntPtr allocator, char* chars, nint length);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFStringGetLength(IntPtr str);

    [LibraryImport(CoreFoundation)]
    public static partial void CFStringGetCharacters(IntPtr str, CFRange range, char* buffer);

    [LibraryImport(CoreFoundation)]
    public static partial void CFRelease(IntPtr obj);

    [LibraryImport(CoreFoundation)]
    public static partial IntPtr CFRunLoopGetCurrent();

    [LibraryImport(CoreFoundation)]
    public static partial int CFRunLoopRunInMode(IntPtr mode, double seconds, [MarshalAs(UnmanagedType.U1)] bool returnAfterSourceHandled);

    // mach
    [StructLayout(LayoutKind.Sequential)]
    public struct MachTimebase
    {
        public uint Numer;
        public uint Denom;
    }

    [LibraryImport(LibSystem)]
    public static partial ulong mach_absolute_time();

    [LibraryImport(LibSystem)]
    public static partial int mach_timebase_info(out MachTimebase info);

    // Exported CFString constants
    private static readonly IntPtr CoreMidiHandle = NativeLibrary.Load(CoreMidi);
    private static readonly IntPtr CoreFoundationHandle = NativeLibrary.Load(CoreFoundation);

    public static readonly IntPtr PropertyName = Constant(CoreMidiHandle, "kMIDIPropertyName");
    public static readonly IntPtr PropertyDisplayName = Constant(CoreMidiHandle, "kMIDIPropertyDisplayName");
    public static readonly IntPtr PropertyUniqueId = Constant(CoreMidiHandle, "kMIDIPropertyUniqueID");
    public static readonly IntPtr PropertyManufacturer = Constant(CoreMidiHandle, "kMIDIPropertyManufacturer");
    public static readonly IntPtr PropertyModel = Constant(CoreMidiHandle, "kMIDIPropertyModel");
    public static readonly IntPtr PropertyOffline = Constant(CoreMidiHandle, "kMIDIPropertyOffline");
    public static readonly IntPtr PropertyDriverOwner = Constant(CoreMidiHandle, "kMIDIPropertyDriverOwner");
    public static readonly IntPtr RunLoopDefaultMode = Constant(CoreFoundationHandle, "kCFRunLoopDefaultMode");

    private static IntPtr Constant(IntPtr library, string name) => *(IntPtr*)NativeLibrary.GetExport(library, name);

    /// <summary>Creates a CFString the caller must release.</summary>
    public static IntPtr CreateString(string value)
    {
        fixed (char* chars = value)
        {
            return CFStringCreateWithCharacters(IntPtr.Zero, chars, value.Length);
        }
    }

    /// <summary>Reads and releases a CFString returned by a Get/Copy function.</summary>
    public static string? TakeString(IntPtr str)
    {
        if (str == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var length = CFStringGetLength(str);
            var result = new string('\0', (int)length);
            fixed (char* buffer = result)
            {
                CFStringGetCharacters(str, new CFRange { Location = 0, Length = length }, buffer);
            }

            return result;
        }
        finally
        {
            CFRelease(str);
        }
    }

    public static string? GetString(uint obj, IntPtr property) =>
        MIDIObjectGetStringProperty(obj, property, out var value) == NoError ? TakeString(value) : null;

    public static int? GetInteger(uint obj, IntPtr property) =>
        MIDIObjectGetIntegerProperty(obj, property, out var value) == NoError ? value : null;
}
