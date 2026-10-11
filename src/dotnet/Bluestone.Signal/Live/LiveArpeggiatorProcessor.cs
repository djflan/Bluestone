using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Time;
using Bluestone.Signal.BuiltIn;

namespace Bluestone.Signal.Live;

/// <summary>
/// The live form of <see cref="BuiltInDevices.Arpeggiator"/>, with the same rules as <see cref="ArpeggiatorProcessor"/>
/// (decision 16 in docs/roadmap.md): a phrase starts at the first note played while nothing is held, steps follow
/// every step length while anything is held, a note after a gap restarts the phrase at its own tick, held pitches
/// are played lowest first, each once. Steps fall on the live clock, which advances at the transport's tempo, so the
/// arpeggio follows tempo whether the transport plays or not. Generated notes end after the gate; a release frees
/// its key at once; <see cref="Release"/> ends every generated note. Generated notes are released at velocity 64
/// (when a step plays, the key's own release velocity is not known yet). Allocation-free.
/// </summary>
public sealed class LiveArpeggiatorProcessor : ILiveProcessor
{
    private const int HeldCapacity = 128;
    private const int PendingCapacity = 128;
    private static readonly int[] StepsPerQuarter = [1, 2, 4, 8];
    private static readonly ParameterDescriptor Rate = BuiltInDevices.Arpeggiator.Parameters[0];
    private static readonly ParameterDescriptor Pattern = BuiltInDevices.Arpeggiator.Parameters[1];
    private static readonly ParameterDescriptor Octaves = BuiltInDevices.Arpeggiator.Parameters[2];
    private static readonly ParameterDescriptor Gate = BuiltInDevices.Arpeggiator.Parameters[3];

    private readonly Ppqn _ppqn;
    private readonly Held[] _held = new Held[HeldCapacity];
    private readonly Held[] _active = new Held[HeldCapacity];
    private readonly Pending[] _pending = new Pending[PendingCapacity];
    private int _heldCount;
    private int _pendingCount;
    private int _rate = 2;
    private ArpeggiatorPattern _pattern;
    private int _octaves = 1;
    private double _gate = 0.5;
    private bool _inPhrase;
    private long _previous;
    private long _next;
    private int _step;

    public LiveArpeggiatorProcessor(Ppqn ppqn)
    {
        if (!ppqn.IsValid)
        {
            throw new ArgumentException("The arpeggiator needs a valid resolution.", nameof(ppqn));
        }

        _ppqn = ppqn;
    }

    /// <summary>The length of one step at the current rate, in ticks.</summary>
    public long StepLength => Math.Max(1, _ppqn.TicksPerQuarterNote / StepsPerQuarter[_rate]);

    /// <summary>Generated notes left out because they fell outside the MIDI range, or keys beyond the held capacity.</summary>
    public long Dropped { get; private set; }

    /// <summary>Generated notes still sounding.</summary>
    public int Sounding => _pendingCount;

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

    public void Process(in LiveBlock block, ReadOnlySpan<LiveEvent> input, LiveEventBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var i = 0;
        while (true)
        {
            // Everything played at or before the next step happens first, a tick at a time.
            if (i < input.Length && (!_inPhrase || input[i].Tick <= _next))
            {
                var tick = input[i].Tick;
                FlushReleases(tick, output);
                var covered = HeldBefore(tick);
                var played = false;
                while (i < input.Length && input[i].Tick == tick)
                {
                    played |= Apply(input[i], output);
                    i++;
                }

                if (played && (!_inPhrase || (tick > _previous && tick < _next && !covered)))
                {
                    _step = 0;
                    Step(tick, output);
                }

                continue;
            }

            if (_inPhrase && _next < block.EndTick)
            {
                FlushReleases(_next, output);
                Step(_next, output);
                continue;
            }

            break;
        }

        FlushReleases(block.EndTick - 1, output);
    }

    public void Release(in LiveBlock block, LiveEventBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        for (var i = 0; i < _pendingCount; i++)
        {
            output.Add(LiveEvent.NoteOff(block.StartTick, _pending[i].Channel, _pending[i].Note));
        }

        _pendingCount = 0;
        _heldCount = 0;
        _inPhrase = false;
        _step = 0;
    }

    // True for a note-on (it joins the held keys).
    private bool Apply(in LiveEvent e, LiveEventBuffer output)
    {
        if (e.IsNoteOn)
        {
            if (_heldCount == HeldCapacity)
            {
                Dropped++;
                return false;
            }

            _held[_heldCount++] = new Held(e.Tick, e.Channel, e.Data1, e.Data2);
            return true;
        }

        if (e.IsNoteOff)
        {
            for (var k = 0; k < _heldCount; k++)
            {
                if (_held[k].Channel == e.Channel && _held[k].Note == e.Data1)
                {
                    Array.Copy(_held, k + 1, _held, k, _heldCount - k - 1);
                    _heldCount--;
                    return false;
                }
            }
        }

        // Unpaired releases and per-note pressure pass on unchanged.
        output.Add(e);
        return false;
    }

    // Whether a key held now was already down at tick - 1.
    private bool HeldBefore(long tick)
    {
        for (var k = 0; k < _heldCount; k++)
        {
            if (_held[k].Start < tick)
            {
                return true;
            }
        }

        return false;
    }

    private void Step(long tick, LiveEventBuffer output)
    {
        var count = CollectActive();
        if (count == 0)
        {
            _inPhrase = false;
            return;
        }

        Emit(tick, count, output);
        _inPhrase = true;
        _previous = tick;
        _next = tick + StepLength;
        _step++;
    }

    private void Emit(long tick, int count, LiveEventBuffer output)
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
        var pitch = source.Note + (12 * (index / count));
        if (pitch > 127 || _pendingCount == PendingCapacity)
        {
            Dropped++;
            return;
        }

        var duration = Math.Max(1, (long)Math.Round(StepLength * _gate));
        output.Add(LiveEvent.NoteOn(tick, source.Channel, pitch, source.Velocity));
        _pending[_pendingCount++] = new Pending(tick + duration, source.Channel, (byte)pitch);
    }

    // Generated notes ending at or before the tick, earliest first.
    private void FlushReleases(long upTo, LiveEventBuffer output)
    {
        while (true)
        {
            var earliest = -1;
            for (var k = 0; k < _pendingCount; k++)
            {
                if (_pending[k].Tick <= upTo && (earliest < 0 || _pending[k].Tick < _pending[earliest].Tick))
                {
                    earliest = k;
                }
            }

            if (earliest < 0)
            {
                return;
            }

            var ending = _pending[earliest];
            output.Add(LiveEvent.NoteOff(ending.Tick, ending.Channel, ending.Note));
            Array.Copy(_pending, earliest + 1, _pending, earliest, _pendingCount - earliest - 1);
            _pendingCount--;
        }
    }

    // The held keys, lowest first, each pitch once (the first held wins).
    private int CollectActive()
    {
        var count = 0;
        for (var h = 0; h < _heldCount; h++)
        {
            var held = _held[h];
            var at = count;
            var duplicate = false;
            while (at > 0)
            {
                var lower = _active[at - 1].Note;
                if (lower == held.Note)
                {
                    duplicate = true;
                    break;
                }

                if (lower < held.Note)
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

        return count;
    }

    private readonly record struct Held(long Start, byte Channel, byte Note, byte Velocity);

    private readonly record struct Pending(long Tick, byte Channel, byte Note);
}
