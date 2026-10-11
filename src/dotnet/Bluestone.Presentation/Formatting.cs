using System.Globalization;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Time;
using Bluestone.Midi.SysEx;
using Bluestone.Midi.Wire;

namespace Bluestone.Presentation;

/// <summary>Consistent, culture-invariant display text for musical values.</summary>
public static class Formatting
{
    private static readonly string[] PitchClasses = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];

    /// <summary>Scientific pitch notation with middle C (60) as C4.</summary>
    public static string NoteName(NoteNumber note) =>
        string.Create(CultureInfo.InvariantCulture, $"{PitchClasses[note.Value % 12]}{(note.Value / 12) - 1}");

    public static string Position(BarBeatTick position) =>
        string.Create(CultureInfo.InvariantCulture, $"{position.Bar}.{position.Beat}.{position.Tick:000}");

    /// <summary>A note name for display in the piano roll and event list, e.g. "C♯4 (61)".</summary>
    public static string NoteWithNumber(NoteNumber note) =>
        string.Create(CultureInfo.InvariantCulture, $"{NoteName(note)} ({note.Value})");

    /// <summary>
    /// A duration as bars.beats.ticks, counting from zero, in the meter at <paramref name="at"/>:
    /// a quarter note in 4/4 is "0.1.000".
    /// </summary>
    public static string Length(TickSpan length, MeterMap meter, Tick at)
    {
        ArgumentNullException.ThrowIfNull(meter);
        var (beat, perBar) = BeatShape(meter, at);
        var bars = length.Value / (beat * perBar);
        var rest = length.Value % (beat * perBar);
        return string.Create(CultureInfo.InvariantCulture, $"{bars}.{rest / beat}.{rest % beat:000}");
    }

    /// <summary>
    /// Parses "bar.beat.tick" (beat and tick optional; separators '.', ':', or space) to a tick.
    /// Beats and ticks beyond the bar carry over, as in most sequencers' position fields.
    /// </summary>
    public static bool TryParsePosition(string? text, MeterMap meter, out Tick tick)
    {
        ArgumentNullException.ThrowIfNull(meter);
        tick = Tick.Zero;
        if (!TryParseParts(text, out var parts) || parts[0] < 1 || (parts.Length > 1 && parts[1] < 1)
            || !meter.TryGetTick(new BarBeatTick(parts[0], 1, 0), out var barStart))
        {
            return false;
        }

        var (beat, _) = BeatShape(meter, barStart);
        var beats = parts.Length > 1 ? parts[1] - 1 : 0;
        var ticks = parts.Length > 2 ? parts[2] : 0;
        tick = new Tick(barStart.Value + (beats * beat) + ticks);
        return true;
    }

    /// <summary>Parses a "bars.beats.ticks" length (as shown by <see cref="Length"/>), or a plain tick count with a "t" suffix.</summary>
    public static bool TryParseLength(string? text, MeterMap meter, Tick at, out TickSpan length)
    {
        ArgumentNullException.ThrowIfNull(meter);
        length = TickSpan.Zero;
        if (text?.Trim() is { Length: > 1 } raw && raw.EndsWith('t') && long.TryParse(raw[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var plain))
        {
            length = new TickSpan(plain);
            return plain > 0;
        }

        if (!TryParseParts(text, out var parts))
        {
            return false;
        }

        var (beat, perBar) = BeatShape(meter, at);
        var bars = parts[0];
        var beats = parts.Length > 1 ? parts[1] : 0;
        var ticks = parts.Length > 2 ? parts[2] : 0;
        var value = (bars * perBar * beat) + (beats * beat) + ticks;
        length = new TickSpan(Math.Max(0, value));
        return value > 0;
    }

    /// <summary>Parses a note number ("61") or name ("C#4", "Db4", "c♯4"), with middle C as C4.</summary>
    public static bool TryParseNote(string? text, out NoteNumber note)
    {
        note = default;
        var raw = text?.Trim().Replace('♯', '#').Replace('♭', 'b');
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            if (number is < 0 or > 127)
            {
                return false;
            }

            note = new NoteNumber(number);
            return true;
        }

        var pitchClass = char.ToUpperInvariant(raw[0]) switch
        {
            'C' => 0,
            'D' => 2,
            'E' => 4,
            'F' => 5,
            'G' => 7,
            'A' => 9,
            'B' => 11,
            _ => -1,
        };
        if (pitchClass < 0)
        {
            return false;
        }

        var index = 1;
        while (index < raw.Length && raw[index] is '#' or 'b')
        {
            pitchClass += raw[index] == '#' ? 1 : -1;
            index++;
        }

        if (!int.TryParse(raw[index..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var octave))
        {
            return false;
        }

        var value = ((octave + 1) * 12) + pitchClass;
        if (value is < 0 or > 127)
        {
            return false;
        }

        note = new NoteNumber(value);
        return true;
    }

    private static bool TryParseParts(string? text, out long[] parts)
    {
        parts = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var pieces = text.Split(['.', ':', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pieces.Length is 0 or > 3)
        {
            return false;
        }

        var values = new long[pieces.Length];
        for (var i = 0; i < pieces.Length; i++)
        {
            if (!long.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
            {
                return false;
            }
        }

        parts = values;
        return true;
    }

    private static (long Beat, long PerBar) BeatShape(MeterMap meter, Tick at)
    {
        var signature = meter.SignatureAt(at);
        var beat = signature.TryGetTicksPerBeat(meter.Ppqn, out var span) ? span.Value : meter.Ppqn.TicksPerQuarterNote;
        return (beat, signature.Numerator);
    }

    public static string Time(TimeSpan time) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}");

    public static string Tempo(Tempo tempo) =>
        string.Create(CultureInfo.InvariantCulture, $"{tempo.BeatsPerMinute:0.0}");

    public static string Milliseconds(TimeSpan time) =>
        string.Create(CultureInfo.InvariantCulture, $"{time.TotalMilliseconds:0.0} ms");

    /// <summary>
    /// Describes a MIDI message for the monitor. SysEx payloads are never shown: only what Bluestone
    /// recognized it as, or else its manufacturer, and its size.
    /// </summary>
    public static string Message(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return "(empty)";
        }

        if (bytes[0] == SysExMessage.Start)
        {
            if (SysExInterpreter.Interpret(bytes) is { } meaning)
            {
                return string.Create(CultureInfo.InvariantCulture, $"SysEx · {meaning.Summary} · {bytes.Length} bytes");
            }

            var manufacturer = bytes.Length > 1 ? bytes[1].ToString("X2", CultureInfo.InvariantCulture) : "?";
            return string.Create(CultureInfo.InvariantCulture, $"SysEx · {bytes.Length} bytes · manufacturer {manufacturer}");
        }

        if (bytes.Length >= 2 && ChannelMessage.TryCreate(bytes[0], bytes[1], bytes.Length > 2 ? bytes[2] : (byte)0, out var m))
        {
            var ch = string.Create(CultureInfo.InvariantCulture, $"Ch {m.Channel.Number}");
            return m.Kind switch
            {
                _ when m.IsNoteOn => string.Create(CultureInfo.InvariantCulture, $"Note On · {ch} · {NoteName(m.Note)} · vel {m.Data2}"),
                _ when m.IsNoteOff => string.Create(CultureInfo.InvariantCulture, $"Note Off · {ch} · {NoteName(m.Note)}"),
                ChannelMessageKind.ControlChange => string.Create(CultureInfo.InvariantCulture, $"CC {m.Data1} · {ch} · {m.Data2}"),
                ChannelMessageKind.ProgramChange => string.Create(CultureInfo.InvariantCulture, $"Program {m.Program.Number} · {ch}"),
                ChannelMessageKind.PitchBend => string.Create(CultureInfo.InvariantCulture, $"Pitch Bend · {ch} · {m.PitchBendValue.OffsetFromCenter:+0;-0;0}"),
                ChannelMessageKind.ChannelPressure => string.Create(CultureInfo.InvariantCulture, $"Pressure · {ch} · {m.Data1}"),
                ChannelMessageKind.PolyPressure => string.Create(CultureInfo.InvariantCulture, $"Poly Pressure · {ch} · {NoteName(m.Note)} · {m.Data2}"),
                _ => Convert.ToHexString(bytes),
            };
        }

        return string.Join(' ', bytes.ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }
}
