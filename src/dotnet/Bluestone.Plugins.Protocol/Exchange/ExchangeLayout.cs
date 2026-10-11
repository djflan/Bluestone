namespace Bluestone.Plugins.Protocol.Exchange;

/// <summary>
/// Byte layout of a block-exchange file. All values are native-endian, which is little-endian on every supported
/// platform (checked at create and open). Offsets of 32-bit fields are 4-aligned and of 64-bit fields 8-aligned.
/// <code>
/// Header (128 bytes)
///   0 magic u64 | 8 layout version | 12 generation | 16 slot count | 20 max frames | 24 input channels
///  28 output channels | 32 event capacity | 36 parameter-change capacity | 40 event record bytes | 44 slot stride
///  48 worker heartbeat i64 | 56 state flags (ExchangeState)
/// Slot (stride bytes, 64-aligned), repeated slot-count times from offset 128
///   0 state (SlotState) | 4 status (BlockStatus) | 8 block index i64 | 16 frames | 20 input events
///  24 parameter changes | 28 output events | 32 transport sample position i64 | 40 tempo f64 | 48 transport flags
///  52 input events dropped | 56 output events dropped
///  64 input audio (planar float32, channel c at c * max frames) | input events | parameter changes (16 bytes each)
///     | output audio | output events
/// </code>
/// </summary>
public sealed class ExchangeLayout
{
    public const ulong Magic = 0x3148435843444E43; // "CDNCXCH1"
    public const int LayoutVersion = 1;
    public const int HeaderBytes = 128;
    public const int ParameterChangeBytes = 16;

    internal const int MagicOffset = 0;
    internal const int LayoutVersionOffset = 8;
    internal const int GenerationOffset = 12;
    internal const int SlotCountOffset = 16;
    internal const int MaxFramesOffset = 20;
    internal const int InputChannelsOffset = 24;
    internal const int OutputChannelsOffset = 28;
    internal const int EventCapacityOffset = 32;
    internal const int ParameterCapacityOffset = 36;
    internal const int EventRecordBytesOffset = 40;
    internal const int SlotStrideOffset = 44;
    internal const int HeartbeatOffset = 48;
    internal const int FlagsOffset = 56;

    internal const int SlotStateOffset = 0;
    internal const int SlotStatusOffset = 4;
    internal const int SlotBlockIndexOffset = 8;
    internal const int SlotFramesOffset = 16;
    internal const int SlotInputEventCountOffset = 20;
    internal const int SlotParameterCountOffset = 24;
    internal const int SlotOutputEventCountOffset = 28;
    internal const int SlotTransportPositionOffset = 32;
    internal const int SlotTempoOffset = 40;
    internal const int SlotTransportFlagsOffset = 48;
    internal const int SlotInputDroppedOffset = 52;
    internal const int SlotOutputDroppedOffset = 56;
    internal const int SlotHeaderBytes = 64;

    public ExchangeLayout(ExchangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("The plugin block exchange requires a little-endian platform.");
        }

        Options = options;
        InputAudioOffset = SlotHeaderBytes;
        var inputAudioBytes = Align((long)options.InputChannels * options.MaxFrames * sizeof(float), 8);
        InputEventsOffset = InputAudioOffset + inputAudioBytes;
        ParameterChangesOffset = InputEventsOffset + ((long)options.EventCapacity * PluginEvent.RecordBytes);
        OutputAudioOffset = ParameterChangesOffset + ((long)options.ParameterChangeCapacity * ParameterChangeBytes);
        var outputAudioBytes = Align((long)options.OutputChannels * options.MaxFrames * sizeof(float), 8);
        OutputEventsOffset = OutputAudioOffset + outputAudioBytes;
        SlotStride = Align(OutputEventsOffset + ((long)options.EventCapacity * PluginEvent.RecordBytes), 64);
        TotalBytes = HeaderBytes + (SlotStride * options.SlotCount);
    }

    public ExchangeOptions Options { get; }

    public long SlotStride { get; }

    public long TotalBytes { get; }

    internal long InputAudioOffset { get; }

    internal long InputEventsOffset { get; }

    internal long ParameterChangesOffset { get; }

    internal long OutputAudioOffset { get; }

    internal long OutputEventsOffset { get; }

    internal long SlotOffset(int slot) => HeaderBytes + (slot * SlotStride);

    private static long Align(long value, int alignment) => (value + alignment - 1) / alignment * alignment;
}
