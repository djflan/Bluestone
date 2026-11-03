using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Plugins;

/// <summary>
/// The host's half of one instance's data plane: block numbering, the pipeline (block k is submitted and block
/// k - (depth - 1) collected), the parameter changes queued for the next block, and the failure policy's output. Used by
/// <see cref="PluginInstance"/> in the process that supervises the worker, and by <see cref="AttachedPluginInstance"/> in
/// the engine process, which drives the same exchange file (ADR 0044). One audio thread; it never locks, allocates, does
/// I/O, or throws for a plugin failure.
/// </summary>
public sealed class PluginBlockPipeline
{
    private readonly ParameterChange[] _pendingChanges;
    private readonly float[] _inputHistory;
    private readonly long[] _historyBlock;
    private readonly int[] _historyFrames;
    private readonly bool _bypass;
    private readonly int _inputChannels;
    private readonly int _maxBlockFrames;
    private long _nextBlock;
    private int _pendingCount;
    private long _parameterChangesDropped;

    /// <param name="maxBlockFrames">The largest block.</param>
    /// <param name="inputChannels">Planar input channels.</param>
    /// <param name="parameterChangeCapacity">Parameter changes queued per block.</param>
    /// <param name="pipelineDepth">Blocks in flight, at least 1.</param>
    /// <param name="collectWait">How long a collect may spin for its block.</param>
    /// <param name="bypassOnFailure">Pass the delayed dry input through when the plugin cannot deliver (<see cref="FailurePolicy.BypassInput"/>).</param>
    public PluginBlockPipeline(int maxBlockFrames, int inputChannels, int parameterChangeCapacity, int pipelineDepth, TimeSpan collectWait, bool bypassOnFailure)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBlockFrames, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(inputChannels);
        ArgumentOutOfRangeException.ThrowIfNegative(parameterChangeCapacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(pipelineDepth, 1);
        _maxBlockFrames = maxBlockFrames;
        _inputChannels = inputChannels;
        PipelineDepth = pipelineDepth;
        CollectWait = collectWait;
        _bypass = bypassOnFailure;
        _pendingChanges = new ParameterChange[parameterChangeCapacity];
        var historyBlocks = bypassOnFailure ? pipelineDepth : 0;
        _inputHistory = new float[historyBlocks * inputChannels * maxBlockFrames];
        _historyBlock = new long[historyBlocks];
        _historyFrames = new int[historyBlocks];
        Array.Fill(_historyBlock, -1);
    }

    public int PipelineDepth { get; }

    public TimeSpan CollectWait { get; }

    /// <summary>Parameter changes dropped because the per-block queue was full.</summary>
    public long ParameterChangesDropped => Interlocked.Read(ref _parameterChangesDropped);

    /// <summary>
    /// Submits this block to <paramref name="exchange"/> and collects the one the pipeline delivers now. With no exchange
    /// (the worker is not running) the output follows the failure policy. Arguments must already be checked.
    /// </summary>
    public BlockOutcome Process(
        HostBlockExchange? exchange,
        int frames,
        ReadOnlySpan<float> input,
        Span<float> audio,
        ReadOnlySpan<PluginEvent> inputEvents,
        Span<PluginEvent> outputEvents,
        in TransportState transport)
    {
        var block = _nextBlock++;
        if (_bypass)
        {
            RememberInput(block, frames, input);
        }

        var collectIndex = block - (PipelineDepth - 1);
        if (exchange is null)
        {
            _pendingCount = 0;
            return Fail(ProcessOutcome.Unavailable, audio, collectIndex, frames);
        }

        var submitted = exchange.TrySubmit(block, frames, transport, input, inputEvents, _pendingChanges.AsSpan(0, _pendingCount));
        if (submitted != ExchangeStatus.Busy)
        {
            // Busy keeps the changes for the next block; anything else consumed or invalidated them.
            _pendingCount = 0;
        }

        if (collectIndex < 0)
        {
            audio.Clear();
            return new BlockOutcome(ProcessOutcome.Priming, 0, false);
        }

        var collected = exchange.TryCollect(collectIndex, CollectWait, audio, outputEvents, out var result);
        if (collected == ExchangeStatus.Ok && result.Frames == frames && result.Status is BlockStatus.Ok or BlockStatus.DeadlineMissed)
        {
            return new BlockOutcome(ProcessOutcome.Processed, result.OutputEventCount, false);
        }

        var outcome = collected switch
        {
            ExchangeStatus.Ok when result.Status == BlockStatus.PluginError => ProcessOutcome.PluginError,
            ExchangeStatus.Faulted or ExchangeStatus.Stale => ProcessOutcome.Unavailable,
            _ => ProcessOutcome.Late,
        };
        return Fail(outcome, audio, collectIndex, frames);
    }

    /// <summary>Queues a checked parameter change for the next block; false (and counted) when the queue is full.</summary>
    public bool Queue(uint parameterId, double normalizedValue, int sampleOffset)
    {
        if (_pendingCount == _pendingChanges.Length)
        {
            Interlocked.Increment(ref _parameterChangesDropped);
            return false;
        }

        _pendingChanges[_pendingCount++] = new ParameterChange(parameterId, sampleOffset, normalizedValue);
        return true;
    }

    private BlockOutcome Fail(ProcessOutcome outcome, Span<float> audio, long collectIndex, int frames)
    {
        if (_bypass && collectIndex >= 0)
        {
            var slot = (int)(collectIndex % PipelineDepth);
            if (_historyBlock[slot] == collectIndex && _historyFrames[slot] == frames)
            {
                _inputHistory.AsSpan(slot * _inputChannels * _maxBlockFrames, audio.Length).CopyTo(audio);
                return new BlockOutcome(outcome, 0, true);
            }
        }

        audio.Clear();
        return new BlockOutcome(outcome, 0, false);
    }

    private void RememberInput(long block, int frames, ReadOnlySpan<float> input)
    {
        var slot = (int)(block % PipelineDepth);
        input.CopyTo(_inputHistory.AsSpan(slot * _inputChannels * _maxBlockFrames, input.Length));
        _historyBlock[slot] = block;
        _historyFrames[slot] = frames;
    }
}
