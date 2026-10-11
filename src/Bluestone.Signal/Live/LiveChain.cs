using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Signal.Live;

/// <summary>
/// One step of the live clock: ticks [<see cref="StartTick"/>, <see cref="EndTick"/>), which last
/// <see cref="Frames"/> sample frames when the step is an audio block (0 when it is not). Ticks advance at the
/// transport's tempo; the live clock never jumps back (a loop wrap or a seek does not move it).
/// </summary>
public readonly record struct LiveBlock(long StartTick, long EndTick, int Frames = 0)
{
    public long Ticks => EndTick - StartTick;

    /// <summary>The frame inside the block where <paramref name="tick"/> falls (0 when the block has no frames).</summary>
    public int OffsetOf(long tick) =>
        Frames <= 0 || Ticks <= 0 ? 0 : (int)Math.Clamp((tick - StartTick) * Frames / Ticks, 0, Frames - 1);

    /// <summary>The tick of a frame inside the block.</summary>
    public long TickOf(int offset) =>
        Frames <= 0 ? StartTick : StartTick + Math.Clamp((long)offset * Ticks / Frames, 0, Math.Max(0, Ticks - 1));
}

/// <summary>
/// The real-time form of a device (ADR 0035): it turns live events into live events, block by block, as they are
/// played. Built-in devices have one alongside their plan-time <see cref="ISignalProcessor"/>, and given the same
/// input they put out the same notes (tested). <see cref="Process"/> and <see cref="Release"/> run on a real-time
/// thread: no allocation, locking, I/O, or throwing.
/// </summary>
public interface ILiveProcessor
{
    /// <summary>Sets a parameter from its stored (normalized) value. Control thread, before the processor runs.</summary>
    void SetParameter(ParameterId parameter, ControlValue value);

    /// <summary>
    /// Processes one block: <paramref name="input"/> (only the classes the device handles, in tick order, inside the
    /// block) becomes what it appends to <paramref name="output"/>, inside the block. Notes it generates later (an
    /// arpeggiator's releases, say) come out in the block they fall in.
    /// </summary>
    void Process(in LiveBlock block, ReadOnlySpan<LiveEvent> input, LiveEventBuffer output);

    /// <summary>
    /// Ends everything at the block's start: a note-off for every note it put out and has not ended, and it forgets
    /// the notes it holds. Used when the route changes, the device is bypassed or removed, or input stops.
    /// </summary>
    void Release(in LiveBlock block, LiveEventBuffer output);
}

/// <summary>Creates the live processor for one device instance, at the sequence's resolution.</summary>
public delegate ILiveProcessor LiveProcessorFactory(Ppqn ppqn);

/// <summary>
/// Runs live events through a list of processors in order, each given only the event classes it handles; everything
/// else goes around it and is merged back by tick (passthrough by class, ADR 0022). Owns fixed buffers: running
/// allocates nothing. Not thread-safe: one real-time thread at a time.
/// </summary>
public sealed class LiveChain
{
    private readonly (ILiveProcessor Processor, EventClass Handles)[] _stages;
    private LiveEventBuffer _current;
    private LiveEventBuffer _next;
    private readonly LiveEventBuffer _handled;
    private readonly LiveEventBuffer _produced;
    private readonly LiveEventBuffer _around;

    public LiveChain(IEnumerable<(ILiveProcessor Processor, EventClass Handles)> stages, int capacity = LiveEventBuffer.DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(stages);
        _stages = [.. stages];
        _current = new LiveEventBuffer(capacity);
        _next = new LiveEventBuffer(capacity);
        _handled = new LiveEventBuffer(capacity);
        _produced = new LiveEventBuffer(capacity);
        _around = new LiveEventBuffer(capacity);
    }

    public int Count => _stages.Length;

    public ILiveProcessor this[int index] => _stages[index].Processor;

    /// <summary>Events lost because a block held more than the buffers' capacity.</summary>
    public long Dropped => _current.Dropped + _next.Dropped + _handled.Dropped + _produced.Dropped + _around.Dropped;

    /// <summary>Runs one block; <paramref name="input"/> is in tick order inside the block.</summary>
    public void Process(in LiveBlock block, ReadOnlySpan<LiveEvent> input, LiveEventBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _current.Clear();
        _current.AddRange(input);
        for (var i = 0; i < _stages.Length; i++)
        {
            Run(i, block, release: false);
        }

        output.AddRange(_current.Events);
    }

    /// <summary>
    /// Releases every stage in order, the releases of one stage passing through the stages after it (so each sees
    /// the note-offs for what it was given), and appends the final note-offs to <paramref name="output"/>.
    /// </summary>
    public void Release(in LiveBlock block, LiveEventBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _current.Clear();
        for (var i = 0; i < _stages.Length; i++)
        {
            Run(i, block, release: true);
        }

        output.AddRange(_current.Events);
    }

    private void Run(int index, in LiveBlock block, bool release)
    {
        var (processor, handles) = _stages[index];
        _handled.Clear();
        _around.Clear();
        foreach (var e in _current.Events)
        {
            if ((e.Class & handles) != EventClass.None)
            {
                _handled.Add(e);
            }
            else
            {
                _around.Add(e);
            }
        }

        _produced.Clear();
        processor.Process(block, _handled.Events, _produced);
        if (release)
        {
            processor.Release(block, _produced);
        }

        _produced.SortByTick();
        _next.Clear();
        Merge(_produced.Events, _around.Events, _next);
        (_current, _next) = (_next, _current);
    }

    // By tick; on equal ticks what the device produced goes first.
    private static void Merge(ReadOnlySpan<LiveEvent> first, ReadOnlySpan<LiveEvent> second, LiveEventBuffer output)
    {
        int i = 0, j = 0;
        while (i < first.Length || j < second.Length)
        {
            if (j == second.Length || (i < first.Length && first[i].Tick <= second[j].Tick))
            {
                output.Add(first[i++]);
            }
            else
            {
                output.Add(second[j++]);
            }
        }
    }
}
