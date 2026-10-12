using System.Runtime.InteropServices;

namespace Bluestone.PluginWorker.Native;

/// <summary>
/// The C ABI of the Rust plugin host (<c>src/native/crates/bluestone_plugin_host</c>, ABI version <see cref="AbiVersion"/>).
/// This is the worker's only unsafe code. Ownership: strings and byte buffers returned by the library are freed with
/// <see cref="StringFree"/> and <see cref="BytesFree"/>; handles are released with their close or destroy function.
/// Threads: process and wake on any thread, everything else on the worker's main thread (see the crate's docs).
/// </summary>
internal static unsafe partial class NativeMethods
{
    public const string Library = "bluestone_plugin_host";
    public const uint AbiVersion = 1;
    public const uint FormatVst3 = 1;
    public const uint FormatClap = 2;

    [LibraryImport(Library, EntryPoint = "bph_abi_version")]
    public static partial uint AbiVersionOf();

    [LibraryImport(Library, EntryPoint = "bph_main_init")]
    public static partial int MainInit(out nint error);

    [LibraryImport(Library, EntryPoint = "bph_main_pump")]
    public static partial void MainPump(uint timeoutMilliseconds);

    [LibraryImport(Library, EntryPoint = "bph_main_wake")]
    public static partial void MainWake();

    [LibraryImport(Library, EntryPoint = "bph_string_free")]
    public static partial void StringFree(nint text);

    [LibraryImport(Library, EntryPoint = "bph_bytes_free")]
    public static partial void BytesFree(nint data, nuint length);

    [LibraryImport(Library, EntryPoint = "bph_module_open", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ModuleOpen(uint format, string path, out nint module, out nint error);

    [LibraryImport(Library, EntryPoint = "bph_module_describe")]
    public static partial nint ModuleDescribe(nint module);

    [LibraryImport(Library, EntryPoint = "bph_module_close")]
    public static partial void ModuleClose(nint module);

    [LibraryImport(Library, EntryPoint = "bph_plugin_create", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int PluginCreate(nint module, string id, out nint plugin, out nint error);

    [LibraryImport(Library, EntryPoint = "bph_plugin_destroy")]
    public static partial void PluginDestroy(nint plugin);

    [LibraryImport(Library, EntryPoint = "bph_plugin_parameters")]
    public static partial nint PluginParameters(nint plugin);

    [LibraryImport(Library, EntryPoint = "bph_plugin_get_parameter")]
    public static partial int PluginGetParameter(nint plugin, uint id, out double value);

    [LibraryImport(Library, EntryPoint = "bph_plugin_set_parameter")]
    public static partial int PluginSetParameter(nint plugin, uint id, double value);

    [LibraryImport(Library, EntryPoint = "bph_plugin_activate")]
    public static partial int PluginActivate(nint plugin, double sampleRate, uint maxFrames, uint inputChannels, uint outputChannels, out nint error);

    [LibraryImport(Library, EntryPoint = "bph_plugin_process")]
    private static partial int PluginProcess(nint plugin, NativeBlock* block);

    [LibraryImport(Library, EntryPoint = "bph_plugin_latency")]
    public static partial uint PluginLatency(nint plugin);

    [LibraryImport(Library, EntryPoint = "bph_plugin_save_state")]
    public static partial int PluginSaveState(nint plugin, out nint data, out nuint length, out nint error);

    [LibraryImport(Library, EntryPoint = "bph_plugin_load_state")]
    private static partial int PluginLoadState(nint plugin, byte* data, nuint length, out nint error);

    [LibraryImport(Library, EntryPoint = "bph_plugin_idle")]
    private static partial uint PluginIdle(nint plugin, NativeNotification* notifications, uint capacity);

    [LibraryImport(Library, EntryPoint = "bph_plugin_has_editor")]
    public static partial int PluginHasEditor(nint plugin);

    [LibraryImport(Library, EntryPoint = "bph_plugin_open_editor", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int PluginOpenEditor(nint plugin, string title, out nint error);

    [LibraryImport(Library, EntryPoint = "bph_plugin_set_editor_visible")]
    public static partial void PluginSetEditorVisible(nint plugin, int visible);

    [LibraryImport(Library, EntryPoint = "bph_plugin_route_editor_keys")]
    public static partial int PluginRouteEditorKeys(nint plugin, int toHost);

    [LibraryImport(Library, EntryPoint = "bph_editor_key_routing_supported")]
    public static partial int EditorKeyRoutingSupported();

    [LibraryImport(Library, EntryPoint = "bph_plugin_close_editor")]
    public static partial void PluginCloseEditor(nint plugin);

    [LibraryImport(Library, EntryPoint = "bph_plugin_is_editor_open")]
    public static partial int PluginIsEditorOpen(nint plugin);

    /// <summary>Reads and frees a string the library returned; null for a null pointer.</summary>
    public static string? TakeString(nint text)
    {
        if (text == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(text);
        }
        finally
        {
            StringFree(text);
        }
    }

    /// <summary>Copies and frees a byte buffer the library returned.</summary>
    public static byte[] TakeBytes(nint data, nuint length)
    {
        try
        {
            return data == 0 ? [] : new ReadOnlySpan<byte>((void*)data, checked((int)length)).ToArray();
        }
        finally
        {
            BytesFree(data, length);
        }
    }

    public static int LoadState(nint plugin, ReadOnlySpan<byte> data, out nint error)
    {
        fixed (byte* pointer = data)
        {
            return PluginLoadState(plugin, pointer, (nuint)data.Length, out error);
        }
    }

    public static int Idle(nint plugin, Span<NativeNotification> notifications)
    {
        fixed (NativeNotification* pointer = notifications)
        {
            return (int)PluginIdle(plugin, pointer, (uint)notifications.Length);
        }
    }

    /// <summary>Processes one block with the buffers pinned; the library reads and writes them during the call only.</summary>
    public static int Process(nint plugin, ref NativeBlock block, ReadOnlySpan<float> input, Span<float> output, ReadOnlySpan<NativeEvent> events, ReadOnlySpan<NativeParameterChange> changes, Span<NativeEvent> outputEvents)
    {
        fixed (float* inputPointer = input)
        fixed (float* outputPointer = output)
        fixed (NativeEvent* eventPointer = events)
        fixed (NativeParameterChange* changePointer = changes)
        fixed (NativeEvent* outputEventPointer = outputEvents)
        fixed (NativeBlock* blockPointer = &block)
        {
            block.Input = (nint)inputPointer;
            block.Output = (nint)outputPointer;
            block.Events = (nint)eventPointer;
            block.Changes = (nint)changePointer;
            block.OutputEvents = (nint)outputEventPointer;
            return PluginProcess(plugin, blockPointer);
        }
    }
}

/// <summary>Mirrors <c>BphEvent</c>: a MIDI 1.0 channel event; <see cref="Kind"/> uses <c>PluginEventKind</c>'s numbers.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeEvent
{
    public int Offset;
    public byte Kind;
    public byte Channel;
    public byte Data1;
    public byte Data2;
}

/// <summary>Mirrors <c>BphParamChange</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeParameterChange
{
    public int Offset;
    public uint Id;
    public double Value;
}

/// <summary>Mirrors <c>BphNotification</c>; <see cref="Kind"/> uses <c>NotificationKind</c>'s numbers.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeNotification
{
    public uint Kind;
    public uint Id;
    public double Value;
}

/// <summary>Mirrors <c>BphProcess</c> (96 bytes on 64-bit platforms). The pointers are set by <see cref="NativeMethods.Process"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeBlock
{
    public uint Frames;
    public uint InputChannels;
    public uint OutputChannels;
    public uint EventCount;
    public nint Input;
    public nint Output;
    public nint Events;
    public nint Changes;
    public uint ChangeCount;
    public uint OutputEventCapacity;
    public nint OutputEvents;
    public uint OutputEventCount;
    public uint OutputEventsDropped;
    public long Position;
    public double Tempo;
    public uint Playing;
    public uint Reserved;
}
