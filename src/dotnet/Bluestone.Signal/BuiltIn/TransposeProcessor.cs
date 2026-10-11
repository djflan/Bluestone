using System.Globalization;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Signal.BuiltIn;

/// <summary>
/// Runs <see cref="BuiltInDevices.Transpose"/>: moves notes by a number of semitones. Notes that would
/// leave 0-127 are dropped and reported, as the old per-track transpose did. Everything else passes
/// around it (it handles only notes).
/// </summary>
public sealed class TransposeProcessor : ISignalProcessor, IReportingProcessor
{
    private int _semitones;
    private int _dropped;

    public void SetParameter(ParameterId parameter, ControlValue value)
    {
        if (parameter == BuiltInDevices.TransposeSemitones)
        {
            _semitones = BuiltInDevices.SemitonesFrom(value);
        }
    }

    public void Process(in SignalBlock block, ReadOnlySpan<SignalEvent> input, SignalBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var e in input)
        {
            if (_semitones == 0)
            {
                output.Add(e);
                continue;
            }

            TrackEvent? moved = e.Event switch
            {
                NoteEvent n => n.Note.TryTranspose(_semitones, out var note) ? n with { Note = note } : null,
                NoteOffEvent n => n.Note.TryTranspose(_semitones, out var note) ? n with { Note = note } : null,
                PolyPressureEvent p => p.Note.TryTranspose(_semitones, out var note) ? p with { Note = note } : null,
                _ => e.Event,
            };

            if (moved is null)
            {
                _dropped++;
            }
            else
            {
                output.Add(e.With(moved));
            }
        }
    }

    public void Reset()
    {
    }

    public IReadOnlyList<string> TakeReport()
    {
        if (_dropped == 0)
        {
            return [];
        }

        var message = string.Create(CultureInfo.InvariantCulture, $"{_dropped} notes were transposed outside the MIDI range and will not play.");
        _dropped = 0;
        return [message];
    }
}
