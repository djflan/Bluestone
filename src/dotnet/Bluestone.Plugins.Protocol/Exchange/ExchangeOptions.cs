namespace Bluestone.Plugins.Protocol.Exchange;

/// <summary>Size of one plugin instance's block exchange. Fixed when the instance is created.</summary>
public sealed record ExchangeOptions
{
    public const int DefaultSlotCount = 2;
    public const int DefaultEventCapacity = 256;
    public const int DefaultParameterChangeCapacity = 128;

    public ExchangeOptions(
        int maxFrames,
        int inputChannels,
        int outputChannels,
        int slotCount = DefaultSlotCount,
        int eventCapacity = DefaultEventCapacity,
        int parameterChangeCapacity = DefaultParameterChangeCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxFrames, ProtocolLimits.MaxBlockFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(inputChannels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(inputChannels, ProtocolLimits.MaxChannels);
        ArgumentOutOfRangeException.ThrowIfNegative(outputChannels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(outputChannels, ProtocolLimits.MaxChannels);
        ArgumentOutOfRangeException.ThrowIfLessThan(slotCount, ProtocolLimits.MinSlotCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slotCount, ProtocolLimits.MaxSlotCount);
        ArgumentOutOfRangeException.ThrowIfNegative(eventCapacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(eventCapacity, ProtocolLimits.MaxEventCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(parameterChangeCapacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parameterChangeCapacity, ProtocolLimits.MaxParameterChangeCapacity);
        MaxFrames = maxFrames;
        InputChannels = inputChannels;
        OutputChannels = outputChannels;
        SlotCount = slotCount;
        EventCapacity = eventCapacity;
        ParameterChangeCapacity = parameterChangeCapacity;
    }

    /// <summary>Largest block, in sample frames.</summary>
    public int MaxFrames { get; }

    public int InputChannels { get; }

    public int OutputChannels { get; }

    /// <summary>Blocks that can be in flight at once; at least 2, so the host can submit one while the worker processes another.</summary>
    public int SlotCount { get; }

    /// <summary>Events per block in each direction. Events beyond this are dropped and counted.</summary>
    public int EventCapacity { get; }

    /// <summary>Timestamped parameter changes per block. Changes beyond this are dropped and counted.</summary>
    public int ParameterChangeCapacity { get; }
}

/// <summary>Ownership of a slot. Host: Free to Submitted, Submitted to Free (withdraw), Done to Free. Worker: Submitted to Processing to Done.</summary>
public enum SlotState
{
    Free = 0,
    Submitted = 1,
    Processing = 2,
    Done = 3,
}

/// <summary>Per-block result written by the worker.</summary>
public enum BlockStatus
{
    None = 0,
    Ok = 1,
    PluginError = 2,

    /// <summary>The worker finished the block later than the block's own duration. The audio is valid but was late.</summary>
    DeadlineMissed = 3,
}

/// <summary>Outcome of a host-side exchange call. None of them throws.</summary>
public enum ExchangeStatus
{
    Ok = 0,

    /// <summary>The block was submitted but was not Done within the host's bounded wait. It is abandoned; a late result is discarded.</summary>
    NotReady = 1,

    /// <summary>The slot for this block is still owned by the worker (an earlier block is late), so nothing was submitted.</summary>
    Busy = 2,

    /// <summary>Nothing for this block index is in the exchange (never submitted, or withdrawn).</summary>
    NotSubmitted = 3,

    /// <summary>The worker died or the exchange was faulted or disposed; mapped memory was not touched.</summary>
    Faulted = 4,

    /// <summary>The file's generation is not the one this handle was opened for: a newer worker recreated it.</summary>
    Stale = 5,

    /// <summary>The arguments do not fit the exchange (too many frames, wrong buffer sizes).</summary>
    Rejected = 6,
}

[Flags]
public enum ExchangeState
{
    None = 0,

    /// <summary>The host marked the worker as dead. Nobody may hand out blocks any more.</summary>
    Faulted = 1,

    /// <summary>The instance was destroyed normally; the worker's processing loop stops.</summary>
    Closed = 2,
}

/// <summary>Timestamped normalized parameter change inside a block. <paramref name="SampleOffset"/> is relative to the block start.</summary>
public readonly record struct ParameterChange(uint ParameterId, int SampleOffset, double Value);

/// <summary>Transport at the start of a block.</summary>
public readonly record struct TransportState(long SamplePosition, double Tempo, bool IsPlaying);

/// <summary>What the worker reported for a collected block.</summary>
public readonly record struct BlockResult(long BlockIndex, int Frames, BlockStatus Status, int OutputEventCount, int OutputEventsDropped);
