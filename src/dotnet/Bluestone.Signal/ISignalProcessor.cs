using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Time;

namespace Bluestone.Signal;

/// <summary>A half-open window of the timeline that a processor works through in one call.</summary>
public readonly record struct SignalBlock
{
    public SignalBlock(Tick start, Tick end)
    {
        if (end < start)
        {
            throw new ArgumentException("A block cannot end before it starts.", nameof(end));
        }

        Start = start;
        End = end;
    }

    public Tick Start { get; }

    public Tick End { get; }

    /// <summary>The whole timeline in one block, as plan compilation processes it.</summary>
    public static SignalBlock Everything { get; } = new(Tick.Zero, new Tick(long.MaxValue));

    public bool Contains(Tick position) => position >= Start && position < End;
}

/// <summary>
/// The running form of one device: it turns the events it handles into the events it puts out (ADR 0022).
/// One instance belongs to one device instance and keeps whatever state that device needs between blocks.
/// </summary>
/// <remarks>
/// <para>
/// The runner hands a processor only the event classes its definition <c>Handles</c>, in canonical order
/// and all inside <c>block</c>; everything else passes around it. A processor appends what it puts out to
/// <c>output</c>, giving each event the key of the input it came from (<see cref="SignalEvent.With"/>).
/// Output should be in canonical order; the runner restores the order if it is not.
/// </para>
/// <para>
/// <see cref="Process"/> must not allocate for events it passes on unchanged, block, or do I/O: the same
/// contract later serves a real-time runner. Creating a new event (a transposed or generated note) is
/// the only allocation it may make.
/// </para>
/// </remarks>
public interface ISignalProcessor
{
    /// <summary>Sets a parameter from its stored (normalized) value. It applies to events processed after this call.</summary>
    void SetParameter(ParameterId parameter, ControlValue value);

    void Process(in SignalBlock block, ReadOnlySpan<SignalEvent> input, SignalBuffer output);

    /// <summary>Forgets state carried between blocks (held notes, counters), for a jump in the timeline.</summary>
    void Reset();
}

/// <summary>
/// A processor that has something to tell the musician after processing, such as notes it had to drop.
/// Read outside the processing path; reading clears the report.
/// </summary>
public interface IReportingProcessor
{
    IReadOnlyList<string> TakeReport();
}
