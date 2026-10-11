using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Bluestone.Plugins.Protocol.Exchange;

/// <summary>
/// The worker's side of one instance's block exchange: it creates the file and runs blocks the host submitted.
/// Used by one processing thread; the owner must stop that thread before <see cref="Dispose"/>.
/// </summary>
public sealed class WorkerBlockExchange : IDisposable
{
    private readonly MappedRegion _region;
    private readonly ExchangeLayout _layout;
    private int _disposed;

    private WorkerBlockExchange(string path, MappedRegion region, ExchangeLayout layout, uint generation)
    {
        Path = path;
        _region = region;
        _layout = layout;
        Generation = generation;
    }

    public string Path { get; }

    public uint Generation { get; }

    public ExchangeOptions Options => _layout.Options;

    /// <summary>True once the host marked the exchange faulted or closed. The processing loop should stop.</summary>
    public bool IsStopped =>
        (Volatile.Read(ref _region.Int32At(ExchangeLayout.FlagsOffset)) & (int)(ExchangeState.Faulted | ExchangeState.Closed)) != 0;

    /// <summary>
    /// Creates and initializes an exchange file. With <paramref name="allowExisting"/>, an existing file of exactly the
    /// same size is re-initialized in place under the new generation, which a host still holding the old generation
    /// detects as stale; a file of another size is never touched.
    /// </summary>
    public static WorkerBlockExchange Create(string path, ExchangeOptions options, uint generation, bool allowExisting = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfZero(generation);
        var layout = new ExchangeLayout(options);
        var region = MappedRegion.Create(path, layout.TotalBytes, allowExisting);
        try
        {
            Volatile.Write(ref region.Int64At(ExchangeLayout.MagicOffset), 0);
            for (var s = 0; s < options.SlotCount; s++)
            {
                region.Bytes(layout.SlotOffset(s), ExchangeLayout.SlotHeaderBytes).Clear();
            }

            var header = region.Bytes(0, ExchangeLayout.HeaderBytes);
            header[8..].Clear();
            BinaryPrimitives.WriteInt32LittleEndian(header[ExchangeLayout.LayoutVersionOffset..], ExchangeLayout.LayoutVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(header[ExchangeLayout.GenerationOffset..], generation);
            BinaryPrimitives.WriteInt32LittleEndian(header[ExchangeLayout.SlotCountOffset..], options.SlotCount);
            BinaryPrimitives.WriteInt32LittleEndian(header[ExchangeLayout.MaxFramesOffset..], options.MaxFrames);
            BinaryPrimitives.WriteInt32LittleEndian(header[ExchangeLayout.InputChannelsOffset..], options.InputChannels);
            BinaryPrimitives.WriteInt32LittleEndian(header[ExchangeLayout.OutputChannelsOffset..], options.OutputChannels);
            BinaryPrimitives.WriteInt32LittleEndian(header[ExchangeLayout.EventCapacityOffset..], options.EventCapacity);
            BinaryPrimitives.WriteInt32LittleEndian(header[ExchangeLayout.ParameterCapacityOffset..], options.ParameterChangeCapacity);
            BinaryPrimitives.WriteInt32LittleEndian(header[ExchangeLayout.EventRecordBytesOffset..], PluginEvent.RecordBytes);
            BinaryPrimitives.WriteInt32LittleEndian(header[ExchangeLayout.SlotStrideOffset..], (int)layout.SlotStride);

            // The magic goes in last, so a reader never sees a half-written header as valid.
            Volatile.Write(ref region.Int64At(ExchangeLayout.MagicOffset), unchecked((long)ExchangeLayout.Magic));
            return new WorkerBlockExchange(path, region, layout, generation);
        }
        catch
        {
            region.Dispose();
            throw;
        }
    }

    /// <summary>Advances the liveness counter the host can read.</summary>
    public void Heartbeat() => Interlocked.Increment(ref _region.Int64At(ExchangeLayout.HeartbeatOffset));

    /// <summary>Marks the exchange closed from the worker side, which stops the processing loop.</summary>
    public void MarkClosed() => Interlocked.Or(ref _region.Int32At(ExchangeLayout.FlagsOffset), (int)ExchangeState.Closed);

    /// <summary>
    /// Claims the oldest submitted block (Submitted to Processing). False when none is waiting. A block whose header
    /// is out of contract is completed at once with <see cref="BlockStatus.PluginError"/> and skipped.
    /// </summary>
    public bool TryBeginBlock(out WorkerBlock block)
    {
        block = default;
        var options = _layout.Options;
        while (true)
        {
            var best = -1;
            var bestIndex = long.MaxValue;
            for (var s = 0; s < options.SlotCount; s++)
            {
                var offset = _layout.SlotOffset(s);
                if (Volatile.Read(ref _region.Int32At(offset + ExchangeLayout.SlotStateOffset)) == (int)SlotState.Submitted)
                {
                    var index = Volatile.Read(ref _region.Int64At(offset + ExchangeLayout.SlotBlockIndexOffset));
                    if (index < bestIndex)
                    {
                        bestIndex = index;
                        best = s;
                    }
                }
            }

            if (best < 0)
            {
                return false;
            }

            var slotOffset = _layout.SlotOffset(best);
            ref var state = ref _region.Int32At(slotOffset + ExchangeLayout.SlotStateOffset);
            if (Interlocked.CompareExchange(ref state, (int)SlotState.Processing, (int)SlotState.Submitted) != (int)SlotState.Submitted)
            {
                // The host withdrew it in the meantime; look again.
                continue;
            }

            var slot = _region.Bytes(slotOffset, (int)_layout.SlotStride);
            var frames = BinaryPrimitives.ReadInt32LittleEndian(slot[ExchangeLayout.SlotFramesOffset..]);
            var inputEvents = BinaryPrimitives.ReadInt32LittleEndian(slot[ExchangeLayout.SlotInputEventCountOffset..]);
            var changes = BinaryPrimitives.ReadInt32LittleEndian(slot[ExchangeLayout.SlotParameterCountOffset..]);
            if (frames < 1 || frames > options.MaxFrames || inputEvents < 0 || inputEvents > options.EventCapacity || changes < 0 || changes > options.ParameterChangeCapacity)
            {
                BinaryPrimitives.WriteInt32LittleEndian(slot[ExchangeLayout.SlotStatusOffset..], (int)BlockStatus.PluginError);
                BinaryPrimitives.WriteInt32LittleEndian(slot[ExchangeLayout.SlotOutputEventCountOffset..], 0);
                Volatile.Write(ref state, (int)SlotState.Done);
                continue;
            }

            var transport = new TransportState(
                BinaryPrimitives.ReadInt64LittleEndian(slot[ExchangeLayout.SlotTransportPositionOffset..]),
                BinaryPrimitives.ReadDoubleLittleEndian(slot[ExchangeLayout.SlotTempoOffset..]),
                BinaryPrimitives.ReadInt32LittleEndian(slot[ExchangeLayout.SlotTransportFlagsOffset..]) != 0);
            block = new WorkerBlock(slot, _layout, best, Volatile.Read(ref _region.Int64At(slotOffset + ExchangeLayout.SlotBlockIndexOffset)), frames, transport, inputEvents, changes);
            return true;
        }
    }

    /// <summary>Publishes the block's outputs and status (Processing to Done).</summary>
    public void CompleteBlock(ref WorkerBlock block, BlockStatus status)
    {
        var slot = block.Slot;
        BinaryPrimitives.WriteInt32LittleEndian(slot[ExchangeLayout.SlotStatusOffset..], (int)status);
        BinaryPrimitives.WriteInt32LittleEndian(slot[ExchangeLayout.SlotOutputEventCountOffset..], block.OutputEventCount);
        BinaryPrimitives.WriteInt32LittleEndian(slot[ExchangeLayout.SlotOutputDroppedOffset..], block.OutputEventsDropped);
        Volatile.Write(ref _region.Int32At(_layout.SlotOffset(block.SlotIndex) + ExchangeLayout.SlotStateOffset), (int)SlotState.Done);
        block = default;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _region.Dispose();
        }
    }
}

/// <summary>A block the worker has claimed. Views straight into the mapped slot; valid until <see cref="WorkerBlockExchange.CompleteBlock"/>.</summary>
public ref struct WorkerBlock
{
    private readonly ExchangeLayout _layout;

    internal WorkerBlock(Span<byte> slot, ExchangeLayout layout, int slotIndex, long blockIndex, int frames, TransportState transport, int inputEventCount, int parameterChangeCount)
    {
        Slot = slot;
        _layout = layout;
        SlotIndex = slotIndex;
        BlockIndex = blockIndex;
        Frames = frames;
        Transport = transport;
        InputEventCount = inputEventCount;
        ParameterChangeCount = parameterChangeCount;
        OutputEventCount = 0;
        OutputEventsDropped = 0;
    }

    public long BlockIndex { get; }

    public int Frames { get; }

    public TransportState Transport { get; }

    public int InputEventCount { get; }

    public int ParameterChangeCount { get; }

    public int OutputEventCount { readonly get; private set; }

    public int OutputEventsDropped { readonly get; private set; }

    internal Span<byte> Slot { get; }

    internal int SlotIndex { get; }

    public readonly ReadOnlySpan<float> Input(int channel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, _layout.Options.InputChannels);
        return Floats(_layout.InputAudioOffset, channel);
    }

    public readonly Span<float> Output(int channel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, _layout.Options.OutputChannels);
        return Floats(_layout.OutputAudioOffset, channel);
    }

    /// <summary>Reads input event <paramref name="index"/>; false if the host wrote something out of contract.</summary>
    public readonly bool TryReadInputEvent(int index, ref PluginEvent target)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, InputEventCount);
        return PluginEvent.TryReadFrom(Slot.Slice((int)(_layout.InputEventsOffset + ((long)index * PluginEvent.RecordBytes)), PluginEvent.RecordBytes), Frames, ref target);
    }

    /// <summary>Reads parameter change <paramref name="index"/>; false if it is out of contract.</summary>
    public readonly bool TryReadParameterChange(int index, out ParameterChange change)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ParameterChangeCount);
        var record = Slot.Slice((int)(_layout.ParameterChangesOffset + ((long)index * ExchangeLayout.ParameterChangeBytes)), ExchangeLayout.ParameterChangeBytes);
        change = new ParameterChange(
            BinaryPrimitives.ReadUInt32LittleEndian(record),
            BinaryPrimitives.ReadInt32LittleEndian(record[4..]),
            BinaryPrimitives.ReadDoubleLittleEndian(record[8..]));
        return change.SampleOffset >= 0 && change.SampleOffset < Frames && NormalizedValue.IsValid(change.Value);
    }

    /// <summary>Appends an output event; false (and counted as dropped) when the block's event capacity is full or it lies outside the block.</summary>
    public bool TryWriteOutputEvent(in PluginEvent e)
    {
        if (OutputEventCount >= _layout.Options.EventCapacity || e.SampleOffset >= Frames)
        {
            OutputEventsDropped++;
            return false;
        }

        e.WriteTo(Slot.Slice((int)(_layout.OutputEventsOffset + ((long)OutputEventCount * PluginEvent.RecordBytes)), PluginEvent.RecordBytes));
        OutputEventCount++;
        return true;
    }

    /// <summary>Counts an output event the plugin produced but could not be represented (for example oversized system exclusive).</summary>
    public void CountDroppedOutputEvent() => OutputEventsDropped++;

    private readonly Span<float> Floats(long areaOffset, int channel) =>
        MemoryMarshal.Cast<byte, float>(Slot.Slice((int)(areaOffset + ((long)channel * _layout.Options.MaxFrames * sizeof(float))), Frames * sizeof(float)));
}
