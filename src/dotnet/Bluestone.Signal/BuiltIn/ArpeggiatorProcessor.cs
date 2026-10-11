using System.Globalization;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Signal.BuiltIn;

/// <summary>
/// Runs <see cref="BuiltInDevices.Arpeggiator"/>: plays the notes held at each step one at a time, in a pattern, across one or more octaves. Three notes
/// held for three beats at sixteenths become twelve notes.
/// </summary>
/// <remarks>
/// A phrase starts at the first note played while nothing is held, and steps follow every
/// <see cref="StepLength"/> ticks while any note is held, so every phrase sounds at least once. A note
/// played after a gap, however short, starts a new phrase at its own tick. At each
/// step the held notes (each pitch once, lowest first) are expanded over the octaves and the pattern
/// picks one. A generated note takes the channel and velocities of the held note it comes from, and its
/// place in the order. Its ID is derived from that note's ID and its own tick, so the same input always
/// produces the same events, whatever the block size. A note that starts and ends between two steps of a running phrase is not heard,
/// as on most arpeggiators. Unpaired releases and per-note pressure pass on unchanged.
/// </remarks>
public sealed class ArpeggiatorProcessor : ISignalProcessor, IReportingProcessor
{
    /// <summary>Steps per quarter note for each rate position: quarters, eighths, sixteenths, thirty-seconds.</summary>
    private static readonly int[] StepsPerQuarter = [1, 2, 4, 8];

    private static readonly ParameterDescriptor Rate = BuiltInDevices.Arpeggiator.Parameters[0];
    private static readonly ParameterDescriptor Pattern = BuiltInDevices.Arpeggiator.Parameters[1];
    private static readonly ParameterDescriptor Octaves = BuiltInDevices.Arpeggiator.Parameters[2];
    private static readonly ParameterDescriptor Gate = BuiltInDevices.Arpeggiator.Parameters[3];

    private readonly Ppqn _ppqn;
    private readonly List<SignalEvent> _held = new(32);
    private SignalEvent[] _active = new SignalEvent[32];
    private int _rate = 2;
    private ArpeggiatorPattern _pattern;
    private int _octaves = 1;
    private double _gate = 0.5;
    private bool _inPhrase;
    private long _previous;
    private long _next;
    private int _step;
    private int _outOfRange;

    public ArpeggiatorProcessor(Ppqn ppqn)
    {
        if (!ppqn.IsValid)
        {
            throw new ArgumentException("The arpeggiator needs a valid resolution.", nameof(ppqn));
        }

        _ppqn = ppqn;
    }

    /// <summary>The length of one step at the current rate, in ticks.</summary>
    public long StepLength => Math.Max(1, _ppqn.TicksPerQuarterNote / StepsPerQuarter[_rate]);

    public void SetParameter(ParameterId parameter, ControlValue value)
    {
        if (parameter == BuiltInDevices.ArpeggiatorRate)
        {
            _rate = (int)Math.Round(Rate.ToPlain(value));
        }
        else if (parameter == BuiltInDevices.ArpeggiatorPatternParameter)
        {
            _pattern = (ArpeggiatorPattern)(int)Math.Round(Pattern.ToPlain(value));
        }
        else if (parameter == BuiltInDevices.ArpeggiatorOctaves)
        {
            _octaves = (int)Math.Round(Octaves.ToPlain(value));
        }
        else if (parameter == BuiltInDevices.ArpeggiatorGate)
        {
            _gate = Gate.ToPlain(value);
        }
    }

    public void Process(in SignalBlock block, ReadOnlySpan<SignalEvent> input, SignalBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var e in input)
        {
            if (e.Event is NoteEvent)
            {
                _held.Add(e);
            }
            else
            {
                output.Add(e);
            }
        }

        while (true)
        {
            long t;
            if (_inPhrase && RestartAfterGap() is { } restart)
            {
                t = restart;
                _step = 0;
            }
            else if (_inPhrase)
            {
                t = _next;
            }
            else if (EarliestStart() is { } start)
            {
                t = start;
                _step = 0;
            }
            else
            {
                break;
            }

            if (t >= block.End.Value)
            {
                break;
            }

            Expire(t);
            var count = CollectActive(t);
            if (count == 0)
            {
                _inPhrase = false;
                continue;
            }

            Emit(t, count, output);
            _inPhrase = true;
            _previous = t;
            _next = t + StepLength;
            _step++;
        }
    }

    public void Reset()
    {
        _held.Clear();
        _inPhrase = false;
        _step = 0;
    }

    public IReadOnlyList<string> TakeReport()
    {
        if (_outOfRange == 0)
        {
            return [];
        }

        var message = string.Create(CultureInfo.InvariantCulture, $"{_outOfRange} arpeggiated notes fell outside the MIDI range and were left out.");
        _outOfRange = 0;
        return [message];
    }

    private void Emit(long position, int count, SignalBuffer output)
    {
        var length = count * _octaves;
        int index;
        switch (_pattern)
        {
            case ArpeggiatorPattern.Down:
                index = length - 1 - (_step % length);
                break;
            case ArpeggiatorPattern.UpDown when length > 1:
                var period = (2 * length) - 2;
                var k = _step % period;
                index = k < length ? k : period - k;
                break;
            default:
                index = _step % length;
                break;
        }

        var source = _active[index % count];
        var note = (NoteEvent)source.Event;
        if (!note.Note.TryTranspose(12 * (index / count), out var pitch))
        {
            _outOfRange++;
            return;
        }

        var duration = Math.Max(1, (long)Math.Round(StepLength * _gate));
        var generated = new NoteEvent(DerivedId(note.Id, position), new Tick(position), new TickSpan(duration), note.Channel, pitch, note.Velocity, note.ReleaseVelocity);
        output.Add(source.With(generated));
    }

    // A stable ID for the note generated from `source` at `position`: the source's ID with the position mixed into its last eight bytes.
    private static EventId DerivedId(EventId source, long position)
    {
        Span<byte> bytes = stackalloc byte[16];
        source.Value.TryWriteBytes(bytes);
        var mixed = BitConverter.ToUInt64(bytes[8..]) ^ ((ulong)position * 0x9E3779B97F4A7C15UL) ^ 0xA5A5_A5A5_A5A5_A5A5UL;
        BitConverter.TryWriteBytes(bytes[8..], mixed);
        return new EventId(new Guid(bytes));
    }

    // The first note between the last step and the next one that starts after nothing was held: the
    // start of a new phrase. Nothing is held at tick s - 1 when no held note covers it.
    private long? RestartAfterGap()
    {
        long? restart = null;
        foreach (var candidate in _held)
        {
            var start = candidate.Event.Position.Value;
            if (start <= _previous || start >= _next || (restart is { } found && start >= found))
            {
                continue;
            }

            var covered = false;
            foreach (var other in _held)
            {
                if (other.Event.Position.Value <= start - 1 && other.Event.EndPosition.Value > start - 1)
                {
                    covered = true;
                    break;
                }
            }

            if (!covered)
            {
                restart = start;
            }
        }

        return restart;
    }

    private long? EarliestStart()
    {
        long? earliest = null;
        foreach (var held in _held)
        {
            var position = held.Event.Position.Value;
            if (earliest is null || position < earliest)
            {
                earliest = position;
            }
        }

        return earliest;
    }

    private void Expire(long position)
    {
        var kept = 0;
        for (var i = 0; i < _held.Count; i++)
        {
            if (_held[i].Event.EndPosition.Value > position)
            {
                _held[kept++] = _held[i];
            }
        }

        _held.RemoveRange(kept, _held.Count - kept);
    }

    // The held notes sounding at the position, lowest first, each pitch once (the first held wins).
    private int CollectActive(long position)
    {
        if (_active.Length < _held.Count)
        {
            Array.Resize(ref _active, Math.Max(_held.Count, _active.Length * 2));
        }

        var count = 0;
        foreach (var held in _held)
        {
            if (held.Event.Position.Value > position)
            {
                continue;
            }

            var pitch = ((NoteEvent)held.Event).Note;
            var at = count;
            var duplicate = false;
            while (at > 0)
            {
                var lower = ((NoteEvent)_active[at - 1].Event).Note;
                if (lower == pitch)
                {
                    duplicate = true;
                    break;
                }

                if (lower < pitch)
                {
                    break;
                }

                at--;
            }

            if (duplicate)
            {
                continue;
            }

            Array.Copy(_active, at, _active, at + 1, count - at);
            _active[at] = held;
            count++;
        }

        Array.Clear(_active, count, _active.Length - count);
        return count;
    }
}
