using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bluestone.Plugins.Protocol.Exchange;

/// <summary>
/// The host's handle on one instance's block exchange. Called from one audio thread (submit and collect) and from
/// control threads (<see cref="MarkFaulted"/>, <see cref="Dispose"/>). <see cref="TrySubmit"/> and
/// <see cref="TryCollect"/> never block on a lock, allocate, do I/O, log, or throw; they return a status. The only
/// waiting is the bounded spin in <see cref="TryCollect"/>.
/// </summary>
public sealed class HostBlockExchange : IDisposable
{
    private readonly MappedRegion _region;
    private readonly ExchangeLayout _layout;
    private int _activeCalls;
    private int _disposed;
    private volatile bool _faulted;
    private long _lateResultsDiscarded;
    private long _withdrawn;
    private long _inputEventsDropped;
    private long _parameterChangesDropped;

    private HostBlockExchange(string path, MappedRegion region, ExchangeLayout layout, uint generation)
    {
        Path = path;
        _region = region;
        _layout = layout;
        Generation = generation;
    }

    public string Path { get; }

    public uint Generation { get; }

    public ExchangeOptions Options => _layout.Options;

    public bool IsFaulted => _faulted;

    /// <summary>Results that arrived after the host had given up on their block, and were discarded rather than delivered.</summary>
    public long LateResultsDiscarded => Interlocked.Read(ref _lateResultsDiscarded);

    /// <summary>Blocks taken back before the worker started them because the host's wait expired.</summary>
    public long Withdrawn => Interlocked.Read(ref _withdrawn);

    public long InputEventsDropped => Interlocked.Read(ref _inputEventsDropped);

    public long ParameterChangesDropped => Interlocked.Read(ref _parameterChangesDropped);

    /// <summary>The worker's processing-loop counter, or -1 when the exchange is faulted. It advances while the worker is alive.</summary>
    public long WorkerHeartbeat
    {
        get
        {
            if (_faulted || !TryEnter())
            {
                return -1;
            }

            try
            {
                return Volatile.Read(ref _region.Int64At(ExchangeLayout.HeartbeatOffset));
            }
            finally
            {
                Exit();
            }
        }
    }

    /// <summary>
    /// Maps an exchange file that a worker created. The header must match <paramref name="expectedGeneration"/> and
    /// <paramref name="expectedOptions"/> exactly; anything else throws <see cref="ProtocolException"/>.
    /// </summary>
    public static HostBlockExchange Open(string path, uint expectedGeneration, ExchangeOptions expectedOptions)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var layout = new ExchangeLayout(expectedOptions);
        var region = MappedRegion.Open(path, ExchangeLayout.HeaderBytes);
        try
        {
            var header = region.Bytes(0, ExchangeLayout.HeaderBytes);
            Require(BinaryPrimitives.ReadUInt64LittleEndian(header) == ExchangeLayout.Magic, "bad magic");
            Require(Int(header, ExchangeLayout.LayoutVersionOffset) == ExchangeLayout.LayoutVersion, "unsupported layout version");
            var generation = BinaryPrimitives.ReadUInt32LittleEndian(header[ExchangeLayout.GenerationOffset..]);
            Require(generation == expectedGeneration, $"generation {generation}, expected {expectedGeneration}");
            Require(Int(header, ExchangeLayout.SlotCountOffset) == expectedOptions.SlotCount, "slot count differs");
            Require(Int(header, ExchangeLayout.MaxFramesOffset) == expectedOptions.MaxFrames, "max frames differ");
            Require(Int(header, ExchangeLayout.InputChannelsOffset) == expectedOptions.InputChannels, "input channels differ");
            Require(Int(header, ExchangeLayout.OutputChannelsOffset) == expectedOptions.OutputChannels, "output channels differ");
            Require(Int(header, ExchangeLayout.EventCapacityOffset) == expectedOptions.EventCapacity, "event capacity differs");
            Require(Int(header, ExchangeLayout.ParameterCapacityOffset) == expectedOptions.ParameterChangeCapacity, "parameter capacity differs");
            Require(Int(header, ExchangeLayout.EventRecordBytesOffset) == PluginEvent.RecordBytes, "event record size differs");
            Require(Int(header, ExchangeLayout.SlotStrideOffset) == layout.SlotStride, "slot stride differs");
            Require(region.Length >= layout.TotalBytes, "file is shorter than its layout");
            return new HostBlockExchange(path, region, layout, generation);
        }
        catch
        {
            region.Dispose();
            throw;
        }

        static void Require(bool condition, string what)
        {
            if (!condition)
            {
                throw new ProtocolException($"Invalid exchange file header: {what}.");
            }
        }

        static int Int(ReadOnlySpan<byte> header, int offset) => BinaryPrimitives.ReadInt32LittleEndian(header[offset..]);
    }

    /// <summary>
    /// Writes block <paramref name="blockIndex"/> into its slot (<c>blockIndex % SlotCount</c>) and hands it to the
    /// worker. <paramref name="input"/> is planar: channel c occupies <c>[c * frames, (c + 1) * frames)</c>. Events and
    /// parameter changes that do not fit or are out of range are dropped and counted, never thrown.
    /// </summary>
    public ExchangeStatus TrySubmit(
        long blockIndex,
        int frames,
        in TransportState transport,
        ReadOnlySpan<float> input,
        ReadOnlySpan<PluginEvent> events,
        ReadOnlySpan<ParameterChange> changes)
    {
        var options = _layout.Options;
        if (blockIndex < 0 || frames < 1 || frames > options.MaxFrames || input.Length != options.InputChannels * frames)
        {
            return ExchangeStatus.Rejected;
        }

        if (!TryEnter())
        {
            return ExchangeStatus.Faulted;
        }

        try
        {
            if (_faulted)
            {
                return ExchangeStatus.Faulted;
            }

            if (!GenerationMatches())
            {
                _faulted = true;
                return ExchangeStatus.Stale;
            }

            var slotOffset = _layout.SlotOffset(SlotOf(blockIndex));
            ref var state = ref _region.Int32At(slotOffset + ExchangeLayout.SlotStateOffset);
            var current = Volatile.Read(ref state);
            if (current == (int)SlotState.Done)
            {
                // A result for a block the host already gave up on: it is never delivered.
                Interlocked.Increment(ref _lateResultsDiscarded);
                Volatile.Write(ref state, (int)SlotState.Free);
                current = (int)SlotState.Free;
            }

            if (current != (int)SlotState.Free)
            {
                return ExchangeStatus.Busy;
            }

            var slot = _region.Bytes(slotOffset, (int)_layout.SlotStride);
            for (var c = 0; c < options.InputChannels; c++)
            {
                input.Slice(c * frames, frames).CopyTo(Floats(slot, _layout.InputAudioOffset + ((long)c * options.MaxFrames * sizeof(float)), frames));
            }

            var eventCount = 0;
            var droppedEvents = 0;
            foreach (ref readonly var e in events)
            {
                if (eventCount < options.EventCapacity && e.SampleOffset < frames)
                {
                    e.WriteTo(slot.Slice((int)(_layout.InputEventsOffset + ((long)eventCount * PluginEvent.RecordBytes)), PluginEvent.RecordBytes));
                    eventCount++;
                }
                else
                {
                    droppedEvents++;
                }
            }

            var changeCount = 0;
            var droppedChanges = 0;
            foreach (var change in changes)
            {
                if (changeCount < options.ParameterChangeCapacity && change.SampleOffset >= 0 && change.SampleOffset < frames && NormalizedValue.IsValid(change.Value))
                {
                    var record = slot.Slice((int)(_layout.ParameterChangesOffset + ((long)changeCount * ExchangeLayout.ParameterChangeBytes)), ExchangeLayout.ParameterChangeBytes);
                    BinaryPrimitives.WriteUInt32LittleEndian(record, change.ParameterId);
                    BinaryPrimitives.WriteInt32LittleEndian(record[4..], change.SampleOffset);
                    BinaryPrimitives.WriteDoubleLittleEndian(record[8..], change.Value);
                    changeCount++;
                }
                else
                {
                    droppedChanges++;
                }
            }

            BinaryPrimitives.WriteInt64LittleEndian(slot[ExchangeLayout.SlotBlockIndexOffset..], blockIndex);
            BinaryPrimitives.WriteInt32LittleEndian(slot[ExchangeLayout.SlotStatusOffset..], (int)BlockStatus.None);
            BinaryPrimitives.WriteInt32LittleEndian(slot[ExchangeLayout.SlotFramesOffset..], frames);
            BinaryPrimitives.WriteInt32LittleEndian(slot[ExchangeLayout.SlotInputEventCountOffset..], eventCount);
            BinaryPrimitives.WriteInt32LittleEndian(slot[ExchangeLayout.SlotParameterCountOffset..], changeCount);
            BinaryPrimitives.WriteInt32LittleEndian(slot[ExchangeLayout.SlotOutputEventCountOffset..], 0);
            BinaryPrimitives.WriteInt64LittleEndian(slot[ExchangeLayout.SlotTransportPositionOffset..], transport.SamplePosition);
            BinaryPrimitives.WriteDoubleLittleEndian(slot[ExchangeLayout.SlotTempoOffset..], transport.Tempo);
            BinaryPrimitives.WriteInt32LittleEndian(slot[ExchangeLayout.SlotTransportFlagsOffset..], transport.IsPlaying ? 1 : 0);
            BinaryPrimitives.WriteInt32LittleEndian(slot[ExchangeLayout.SlotInputDroppedOffset..], droppedEvents);
            BinaryPrimitives.WriteInt32LittleEndian(slot[ExchangeLayout.SlotOutputDroppedOffset..], 0);
            if (droppedEvents > 0)
            {
                Interlocked.Add(ref _inputEventsDropped, droppedEvents);
            }

            if (droppedChanges > 0)
            {
                Interlocked.Add(ref _parameterChangesDropped, droppedChanges);
            }

            // Release: everything above is visible to the worker before it can see Submitted.
            Volatile.Write(ref state, (int)SlotState.Submitted);
            return ExchangeStatus.Ok;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Takes the result of block <paramref name="blockIndex"/>, spinning at most <paramref name="maxWait"/> for it.
    /// <paramref name="output"/> must hold <c>OutputChannels * frames</c> samples, planar like the input; output
    /// events beyond <paramref name="outputEvents"/>' length are dropped and counted. A block that is not Done in time
    /// is abandoned (taken back if the worker has not started it) and returns <see cref="ExchangeStatus.NotReady"/>;
    /// its late result is discarded by block-index check and never delivered for another block.
    /// </summary>
    public ExchangeStatus TryCollect(long blockIndex, TimeSpan maxWait, Span<float> output, Span<PluginEvent> outputEvents, out BlockResult result)
    {
        result = default;
        if (blockIndex < 0)
        {
            return ExchangeStatus.Rejected;
        }

        if (!TryEnter())
        {
            return ExchangeStatus.Faulted;
        }

        try
        {
            if (_faulted)
            {
                return ExchangeStatus.Faulted;
            }

            if (!GenerationMatches())
            {
                _faulted = true;
                return ExchangeStatus.Stale;
            }

            var slotOffset = _layout.SlotOffset(SlotOf(blockIndex));
            ref var state = ref _region.Int32At(slotOffset + ExchangeLayout.SlotStateOffset);
            ref var indexField = ref _region.Int64At(slotOffset + ExchangeLayout.SlotBlockIndexOffset);
            var deadline = 0L;
            var spinner = default(SpinWait);
            while (true)
            {
                var current = Volatile.Read(ref state);
                if (current == (int)SlotState.Free)
                {
                    return ExchangeStatus.NotSubmitted;
                }

                if (Volatile.Read(ref indexField) != blockIndex)
                {
                    if (current == (int)SlotState.Done)
                    {
                        Interlocked.Increment(ref _lateResultsDiscarded);
                        Volatile.Write(ref state, (int)SlotState.Free);
                    }

                    return ExchangeStatus.NotSubmitted;
                }

                if (current == (int)SlotState.Done)
                {
                    break;
                }

                if (_faulted)
                {
                    return ExchangeStatus.Faulted;
                }

                var now = Stopwatch.GetTimestamp();
                if (deadline == 0)
                {
                    deadline = now + (long)(Math.Clamp(maxWait.TotalSeconds, 0, 3600) * Stopwatch.Frequency);
                }

                if (now >= deadline)
                {
                    if (Interlocked.CompareExchange(ref state, (int)SlotState.Free, (int)SlotState.Submitted) == (int)SlotState.Submitted)
                    {
                        Interlocked.Increment(ref _withdrawn);
                    }

                    return ExchangeStatus.NotReady;
                }

                spinner.SpinOnce(sleep1Threshold: -1);
            }

            var slot = _region.Bytes(slotOffset, (int)_layout.SlotStride);
            var options = _layout.Options;
            var frames = BinaryPrimitives.ReadInt32LittleEndian(slot[ExchangeLayout.SlotFramesOffset..]);
            var status = (BlockStatus)BinaryPrimitives.ReadInt32LittleEndian(slot[ExchangeLayout.SlotStatusOffset..]);
            if (frames < 1 || frames > options.MaxFrames || output.Length < options.OutputChannels * frames || status is < BlockStatus.None or > BlockStatus.DeadlineMissed)
            {
                Volatile.Write(ref state, (int)SlotState.Free);
                return ExchangeStatus.Rejected;
            }

            for (var c = 0; c < options.OutputChannels; c++)
            {
                Floats(slot, _layout.OutputAudioOffset + ((long)c * options.MaxFrames * sizeof(float)), frames).CopyTo(output.Slice(c * frames, frames));
            }

            var declared = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(slot[ExchangeLayout.SlotOutputEventCountOffset..]), 0, options.EventCapacity);
            var dropped = Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(slot[ExchangeLayout.SlotOutputDroppedOffset..]));
            var delivered = 0;
            for (var i = 0; i < declared; i++)
            {
                var record = slot.Slice((int)(_layout.OutputEventsOffset + ((long)i * PluginEvent.RecordBytes)), PluginEvent.RecordBytes);
                if (delivered < outputEvents.Length && PluginEvent.TryReadFrom(record, frames, ref outputEvents[delivered]))
                {
                    delivered++;
                }
                else
                {
                    dropped++;
                }
            }

            result = new BlockResult(blockIndex, frames, status, delivered, dropped);
            Volatile.Write(ref state, (int)SlotState.Free);
            return ExchangeStatus.Ok;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Called when the worker is known to be dead. Every later call returns <see cref="ExchangeStatus.Faulted"/> without
    /// touching mapped memory; this call itself only sets the header's faulted flag (and not even that if the file
    /// now belongs to another generation). Safe from any thread, any number of times.
    /// </summary>
    public void MarkFaulted()
    {
        _faulted = true;
        SetFlag(ExchangeState.Faulted);
    }

    /// <summary>Tells a live worker that the instance is going away normally, so its processing loop stops.</summary>
    public void MarkClosed() => SetFlag(ExchangeState.Closed);

    /// <summary>Unmaps the file once no call is in flight. Idempotent and safe in any state.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _faulted = true;
        var spinner = default(SpinWait);
        while (Volatile.Read(ref _activeCalls) != 0)
        {
            spinner.SpinOnce();
        }

        _region.Dispose();
    }

    private static Span<float> Floats(Span<byte> slot, long offset, int count) =>
        MemoryMarshal.Cast<byte, float>(slot.Slice((int)offset, count * sizeof(float)));

    private int SlotOf(long blockIndex) => (int)(blockIndex % _layout.Options.SlotCount);

    private bool GenerationMatches() =>
        (uint)Volatile.Read(ref _region.Int32At(ExchangeLayout.GenerationOffset)) == Generation;

    private void SetFlag(ExchangeState flag)
    {
        if (!TryEnter())
        {
            return;
        }

        try
        {
            if (GenerationMatches())
            {
                Interlocked.Or(ref _region.Int32At(ExchangeLayout.FlagsOffset), (int)flag);
            }
        }
        finally
        {
            Exit();
        }
    }

    private bool TryEnter()
    {
        Interlocked.Increment(ref _activeCalls);
        if (Volatile.Read(ref _disposed) != 0)
        {
            Interlocked.Decrement(ref _activeCalls);
            return false;
        }

        return true;
    }

    private void Exit() => Interlocked.Decrement(ref _activeCalls);
}
