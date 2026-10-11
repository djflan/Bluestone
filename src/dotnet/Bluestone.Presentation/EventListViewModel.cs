using System.Collections.ObjectModel;
using System.Globalization;
using Bluestone.Application.Editing;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.SysEx;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bluestone.Presentation;

/// <summary>Which events the list shows.</summary>
public sealed record EventFilter(string Name, Func<TrackEvent, bool> Includes)
{
    public static readonly IReadOnlyList<EventFilter> All =
    [
        new("All Events", _ => true),
        new("Notes", e => e is NoteEvent),
        new("Controllers", e => e is ControllerEvent),
        new("Program Changes", e => e is ProgramEvent),
        new("Pitch Bend & Pressure", e => e is PitchBendEvent or ChannelPressureEvent or PolyPressureEvent),
        new("SysEx & Meta", e => e is SysExEvent or RawMidiEvent or MetaEvent),
    ];

    public override string ToString() => Name;
}

/// <summary>
/// The event list editor: every event on the edited track as a row, with editable position, channel,
/// data, and length. Shares its selection with the piano roll.
/// </summary>
public sealed partial class EventListViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly EditorViewModel _editor;
    private bool _syncingSelection;
    private bool _stale = true;

    internal EventListViewModel(MainViewModel owner, EditorViewModel editor)
    {
        _owner = owner;
        _editor = editor;
        _editor.Changed += (_, _) => Rebuild();
    }

    public ObservableCollection<EventRow> Rows { get; } = [];

    /// <summary>Rows selected in the list, kept in step with the editor's selection.</summary>
    public ObservableCollection<EventRow> SelectedRows { get; } = [];

    [ObservableProperty]
    public partial EventFilter Filter { get; set; } = EventFilter.All[0];

    partial void OnFilterChanged(EventFilter value) => Rebuild();

    [ObservableProperty]
    public partial string CountText { get; private set; } = string.Empty;

    /// <summary>
    /// True while the list is on screen. Rows are only built while active, so editing a large track
    /// with the list hidden costs nothing.
    /// </summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    partial void OnIsActiveChanged(bool value)
    {
        if (value && _stale)
        {
            Rebuild();
        }
    }

    /// <summary>Called by the view when the user changes the list's selection.</summary>
    public void OnRowsSelected()
    {
        if (!_syncingSelection)
        {
            _editor.Select(SelectedRows.Select(r => r.Id));
        }
    }

    internal Sequence Sequence => _owner.Project.Sequence;

    internal void Commit(TrackEvent original, TrackEvent edited, string label)
    {
        if (_editor.Track is { } track && edited != original)
        {
            _editor.Replace(track, [edited], label);
        }
    }

    private void Rebuild()
    {
        if (!IsActive)
        {
            _stale = true;
            return;
        }

        _stale = false;
        _syncingSelection = true;
        try
        {
            RebuildRows();
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    /// <summary>
    /// Brings <see cref="Rows"/> in line with <paramref name="rows"/> with as few changes as possible,
    /// so an edit to one event replaces one row and the list keeps its scroll position and focus.
    /// </summary>
    private void Reconcile(List<EventRow> rows)
    {
        // Edited events keep their IDs and their place: replace those rows where they stand.
        if (rows.Count == Rows.Count)
        {
            var moved = false;
            for (var i = 0; i < rows.Count && !moved; i++)
            {
                moved = rows[i].Id != Rows[i].Id;
            }

            if (!moved)
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    if (!ReferenceEquals(rows[i], Rows[i]))
                    {
                        Rows[i] = rows[i];
                    }
                }

                return;
            }
        }

        var wanted = rows.ToHashSet();
        var stale = Rows.Count(r => !wanted.Contains(r));
        if (stale + Math.Abs(rows.Count - Rows.Count) > 64)
        {
            // Too different to patch cheaply (a whole-track edit): start over.
            Rows.Clear();
            foreach (var row in rows)
            {
                Rows.Add(row);
            }

            return;
        }

        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Rows[i]))
            {
                Rows.RemoveAt(i);
            }
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], rows[i]))
            {
                continue;
            }

            var current = -1;
            for (var j = i + 1; j < Rows.Count; j++)
            {
                if (ReferenceEquals(Rows[j], rows[i]))
                {
                    current = j;
                    break;
                }
            }

            if (current >= 0)
            {
                Rows.Move(current, i);
            }
            else
            {
                Rows.Insert(i, rows[i]);
            }
        }
    }

    private void RebuildRows()
    {
        var events = _editor.Events.Where(Filter.Includes).ToList();
        var existing = Rows.ToDictionary(r => r.Id);

        // Reuse rows whose event is unchanged so the list keeps its scroll position and focus.
        var rows = events.Select(e => existing.TryGetValue(e.Id, out var row) && ReferenceEquals(row.Event, e) ? row : new EventRow(this, e)).ToList();
        Reconcile(rows);

        var selected = _editor.SelectedEvents;
        var present = Rows.ToHashSet();
        for (var i = SelectedRows.Count - 1; i >= 0; i--)
        {
            if (!selected.Contains(SelectedRows[i].Id) || !present.Contains(SelectedRows[i]))
            {
                SelectedRows.RemoveAt(i);
            }
        }

        var already = SelectedRows.ToHashSet();
        foreach (var row in Rows.Where(r => selected.Contains(r.Id) && !already.Contains(r)))
        {
            SelectedRows.Add(row);
        }

        CountText = string.Create(CultureInfo.InvariantCulture, $"{Rows.Count} event{(Rows.Count == 1 ? string.Empty : "s")}");
    }
}

/// <summary>
/// One event in the list. Editing a cell parses the text and replaces the event (one undo step);
/// text that does not parse is ignored and the cell shows the event's value again.
/// </summary>
public sealed partial class EventRow : ObservableObject
{
    private readonly EventListViewModel _owner;

    internal EventRow(EventListViewModel owner, TrackEvent e)
    {
        _owner = owner;
        Event = e;
        var meter = owner.Sequence.MeterMap;
        Position = Formatting.Position(meter.ToBarBeatTick(e.Position));
        (Kind, Channel, Data1, Data2, Length) = Describe(e, meter);
        CanEditData = e is ChannelEvent;
        CanEditLength = e is NoteEvent;
    }

    public TrackEvent Event { get; }

    public EventId Id => Event.Id;

    public string Kind { get; }

    public bool CanEditData { get; }

    public bool CanEditLength { get; }

    [ObservableProperty]
    public partial string Position { get; set; }

    [ObservableProperty]
    public partial string Channel { get; set; }

    /// <summary>Note name, controller number, or program.</summary>
    [ObservableProperty]
    public partial string Data1 { get; set; }

    /// <summary>Velocity or value.</summary>
    [ObservableProperty]
    public partial string Data2 { get; set; }

    [ObservableProperty]
    public partial string Length { get; set; }

    partial void OnPositionChanged(string value)
    {
        if (Formatting.TryParsePosition(value, _owner.Sequence.MeterMap, out var tick))
        {
            _owner.Commit(Event, Event with { Position = tick }, "Move Event");
        }
    }

    partial void OnChannelChanged(string value)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 16
            && EventEdits.SetChannel([Event], MidiChannel.FromNumber(number)) is [var edited])
        {
            _owner.Commit(Event, edited, "Change Channel");
        }
    }

    partial void OnData1Changed(string value)
    {
        switch (Event)
        {
            case NoteEvent note when Formatting.TryParseNote(value, out var pitch):
                _owner.Commit(Event, note with { Note = pitch }, "Change Pitch");
                break;
            case NoteOffEvent off when Formatting.TryParseNote(value, out var pitch):
                _owner.Commit(Event, off with { Note = pitch }, "Edit Event");
                break;
            case PolyPressureEvent pressure when Formatting.TryParseNote(value, out var pitch):
                _owner.Commit(Event, pressure with { Note = pitch }, "Edit Event");
                break;
            case ControllerEvent controller when TryByte(value, out var number):
                _owner.Commit(Event, controller with { Controller = new ControllerNumber(number) }, "Edit Event");
                break;
            case ProgramEvent program when TryByte(value, out var number):
                _owner.Commit(Event, program with { Selection = program.Selection with { Program = new ProgramNumber(number) } }, "Edit Event");
                break;
            case ChannelPressureEvent pressure when TryByte(value, out var amount):
                _owner.Commit(Event, pressure with { Pressure = ControlValue.FromSevenBit(amount) }, "Edit Event");
                break;
        }
    }

    partial void OnData2Changed(string value)
    {
        if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            return;
        }

        switch (Event)
        {
            case NoteEvent note when number is >= 1 and <= 127:
                _owner.Commit(Event, note with { Velocity = new Velocity(number) }, "Change Velocity");
                break;
            case PitchBendEvent bend when number is >= -8192 and <= 8191:
                _owner.Commit(Event, bend with { Value = ControlValue.FromFourteenBit(FourteenBitValue.FromOffsetFromCenter(number).Value) }, "Edit Event");
                break;
            case ControllerEvent controller when number is >= 0 and <= 127:
                _owner.Commit(Event, controller with { Value = ControlValue.FromSevenBit(number) }, "Edit Event");
                break;
            case PolyPressureEvent pressure when number is >= 0 and <= 127:
                _owner.Commit(Event, pressure with { Pressure = ControlValue.FromSevenBit(number) }, "Edit Event");
                break;
            case NoteOffEvent off when number is >= 0 and <= 127:
                _owner.Commit(Event, off with { ReleaseVelocity = new Velocity(number) }, "Edit Event");
                break;
        }
    }

    partial void OnLengthChanged(string value)
    {
        if (Event is NoteEvent note && Formatting.TryParseLength(value, _owner.Sequence.MeterMap, note.Position, out var length))
        {
            _owner.Commit(Event, note with { Duration = length }, "Change Length");
        }
    }

    private static bool TryByte(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value is >= 0 and <= 127;

    // Recognized SysEx shows what it is, then its size; unrecognized SysEx shows only its size.
    private static (string Kind, string Channel, string Data1, string Data2, string Length) DescribeSysEx(SysExMessage message)
    {
        var size = string.Create(CultureInfo.InvariantCulture, $"{message.Length} bytes");
        return SysExInterpreter.Interpret(message) is { } meaning
            ? ("SysEx", string.Empty, meaning.Summary, size, string.Empty)
            : ("SysEx", string.Empty, size, string.Empty, string.Empty);
    }

    private static (string Kind, string Channel, string Data1, string Data2, string Length) Describe(TrackEvent e, MeterMap meter)
    {
        static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

        // Bank MSB and LSB as "msb/lsb", with "–" for a part that is not selected.
        static string Bank(ProgramSelection s) => s.BankMsb is null && s.BankLsb is null
            ? string.Empty
            : $"{s.BankMsb?.Value.ToString(CultureInfo.InvariantCulture) ?? "–"}/{s.BankLsb?.Value.ToString(CultureInfo.InvariantCulture) ?? "–"}";
        return e switch
        {
            NoteEvent n => ("Note", Number(n.Channel.Number), Formatting.NoteName(n.Note), Number(n.Velocity.Value), Formatting.Length(n.Duration, meter, n.Position)),
            ControllerEvent c => ("Control", Number(c.Channel.Number), Number(c.Controller.Value), Number(c.Value.ToSevenBit()), string.Empty),
            ProgramEvent p => ("Program", Number(p.Channel.Number), Number(p.Selection.Program.Value), Bank(p.Selection), string.Empty),
            PitchBendEvent b => ("Pitch Bend", Number(b.Channel.Number), string.Empty, Number(b.Value.ToFourteenBit() - FourteenBitValue.Center.Value), string.Empty),
            ChannelPressureEvent p => ("Pressure", Number(p.Channel.Number), Number(p.Pressure.ToSevenBit()), string.Empty, string.Empty),
            PolyPressureEvent p => ("Poly Pressure", Number(p.Channel.Number), Formatting.NoteName(p.Note), Number(p.Pressure.ToSevenBit()), string.Empty),
            NoteOffEvent n => ("Note Off", Number(n.Channel.Number), Formatting.NoteName(n.Note), Number(n.ReleaseVelocity.Value), string.Empty),
            SysExEvent s => DescribeSysEx(s.Message),
            RawMidiEvent r => ("Raw MIDI", string.Empty, string.Create(CultureInfo.InvariantCulture, $"{r.Bytes.Length} bytes"), string.Empty, string.Empty),
            MetaEvent m => ("Meta", string.Empty, string.Create(CultureInfo.InvariantCulture, $"type {m.Type:X2}"), string.Empty, string.Empty),
            _ => ("Event", string.Empty, string.Empty, string.Empty, string.Empty),
        };
    }
}
