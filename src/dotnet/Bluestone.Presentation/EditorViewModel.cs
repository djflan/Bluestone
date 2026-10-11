using System.Collections.Immutable;
using System.Globalization;
using Bluestone.Application.Editing;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>The piano roll's mouse tool.</summary>
public enum EditTool
{
    /// <summary>Select, move, resize, and (with a double-click) create notes.</summary>
    Arrow,

    /// <summary>Click to create notes, drag to set their length; draws in controller lanes.</summary>
    Pencil,

    /// <summary>Click or drag across notes or controller points to delete them.</summary>
    Eraser,
}

/// <summary>How a selection request combines with the current selection.</summary>
public enum SelectionMode
{
    Replace,
    Add,
    Toggle,
}

public enum ControllerLaneKind
{
    Velocity,
    Controller,
    PitchBend,
    ChannelPressure,
}

/// <summary>A lane under the piano roll: note velocities, or one kind of channel event.</summary>
public sealed record ControllerLane(ControllerLaneKind Kind, string Name, int Controller = 0)
{
    public static readonly ControllerLane Velocity = new(ControllerLaneKind.Velocity, "Velocity");

    public static readonly IReadOnlyList<ControllerLane> Standard =
    [
        Velocity,
        new(ControllerLaneKind.Controller, "Modulation (CC 1)", 1),
        new(ControllerLaneKind.Controller, "Volume (CC 7)", 7),
        new(ControllerLaneKind.Controller, "Pan (CC 10)", 10),
        new(ControllerLaneKind.Controller, "Expression (CC 11)", 11),
        new(ControllerLaneKind.Controller, "Sustain (CC 64)", 64),
        new(ControllerLaneKind.Controller, "Brightness (CC 74)", 74),
        new(ControllerLaneKind.PitchBend, "Pitch Bend"),
        new(ControllerLaneKind.ChannelPressure, "Channel Pressure"),
    ];

    /// <summary>The largest value the lane shows: 16383 for pitch bend, otherwise 127.</summary>
    public int MaxValue => Kind == ControllerLaneKind.PitchBend ? FourteenBitValue.MaxValue : 127;

    /// <summary>True for channel events this lane shows.</summary>
    public bool Matches(ChannelEvent e) => (Kind, e) switch
    {
        (ControllerLaneKind.Controller, ControllerEvent c) => c.Controller.Value == Controller,
        (ControllerLaneKind.PitchBend, PitchBendEvent) => true,
        (ControllerLaneKind.ChannelPressure, ChannelPressureEvent) => true,
        _ => false,
    };

    /// <summary>The lane value of <paramref name="e"/>, at the lane's resolution (0-127, or 0-16383 for pitch bend).</summary>
    public static int ValueOf(ChannelEvent e) => e switch
    {
        PitchBendEvent bend => bend.Value.ToFourteenBit(),
        ChannelPressureEvent pressure => pressure.Pressure.ToSevenBit(),
        ControllerEvent controller => controller.Value.ToSevenBit(),
        _ => 0,
    };

    /// <summary>An event for this lane with <paramref name="value"/> (clamped to the lane's range).</summary>
    public ChannelEvent Create(Tick position, MidiChannel channel, int value)
    {
        value = Math.Clamp(value, 0, MaxValue);
        return Kind switch
        {
            ControllerLaneKind.PitchBend => new PitchBendEvent(position, channel, ControlValue.FromFourteenBit(value)),
            ControllerLaneKind.ChannelPressure => new ChannelPressureEvent(position, channel, ControlValue.FromSevenBit(value)),
            _ => new ControllerEvent(position, channel, new ControllerNumber(Controller), ControlValue.FromSevenBit(value)),
        };
    }

    public override string ToString() => Name;
}

/// <summary>A grid choice with a musician-friendly name ("1/16", "1/8T").</summary>
public sealed record GridOption(GridDivision Division, string Name)
{
    public static readonly IReadOnlyList<GridOption> All =
    [
        new(GridDivision.Bar, "Bar"),
        new(GridDivision.Half, "1/2"),
        new(GridDivision.Quarter, "1/4"),
        new(GridDivision.Eighth, "1/8"),
        new(GridDivision.Sixteenth, "1/16"),
        new(GridDivision.ThirtySecond, "1/32"),
        new(GridDivision.QuarterTriplet, "1/4T"),
        new(GridDivision.EighthTriplet, "1/8T"),
        new(GridDivision.SixteenthTriplet, "1/16T"),
    ];

    public static GridOption For(GridDivision division) => All.First(o => o.Division == division);

    public override string ToString() => Name;
}

/// <summary>
/// The MIDI editors (piano roll and event list) for one note clip of one track: the first selected
/// track. Holds the event selection, tool, grid, and clipboard; every change to the music is an
/// undoable command. Pointer gestures are previewed by the view and committed here once, when the
/// gesture ends.
/// </summary>
/// <remarks>
/// The edited clip stays the same across edits while it exists. Otherwise it is the clip under the
/// playhead, or the track's first note clip. Events are shown at timeline positions, hidden content
/// included, so views need not know about clips. Added events go where
/// <see cref="ProjectCommands.EditEvents"/> puts them, and the editor follows them to their clip.
/// </remarks>
public sealed partial class EditorViewModel : ObservableObject
{
    private static ImmutableArray<TrackEvent> _clipboard = [];

    private readonly MainViewModel _owner;
    private HashSet<EventId> _selected = [];
    private ClipId? _clipId;
    private bool _syncingInfo;

    internal EditorViewModel(MainViewModel owner) => _owner = owner;

    /// <summary>Raised when the edited track, its events, or the selection change, so views can redraw.</summary>
    public event EventHandler? Changed;

    /// <summary>The edited track as of the current project state, or null when no track is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrack))]
    public partial Track? Track { get; private set; }

    public bool HasTrack => Track is not null;

    /// <summary>The edited clip, or null when the track has no note clips.</summary>
    public NoteClip? Clip { get; private set; }

    /// <summary>The edited clip's content at timeline positions, in canonical order, hidden content included.</summary>
    public ImmutableArray<TrackEvent> Events { get; private set; } = [];

    /// <summary>Lane index of the edited track, for its colour.</summary>
    [ObservableProperty]
    public partial int ColorIndex { get; private set; }

    [ObservableProperty]
    public partial EditTool Tool { get; set; } = EditTool.Arrow;

    public bool IsArrow
    {
        get => Tool == EditTool.Arrow;
        set
        {
            if (value)
            {
                Tool = EditTool.Arrow;
            }
        }
    }

    public bool IsPencil
    {
        get => Tool == EditTool.Pencil;
        set
        {
            if (value)
            {
                Tool = EditTool.Pencil;
            }
        }
    }

    public bool IsEraser
    {
        get => Tool == EditTool.Eraser;
        set
        {
            if (value)
            {
                Tool = EditTool.Eraser;
            }
        }
    }

    partial void OnToolChanged(EditTool value)
    {
        OnPropertyChanged(nameof(IsArrow));
        OnPropertyChanged(nameof(IsPencil));
        OnPropertyChanged(nameof(IsEraser));
    }

    /// <summary>The grid that positions snap to while editing.</summary>
    [ObservableProperty]
    public partial GridOption Snap { get; set; } = GridOption.For(GridDivision.Sixteenth);

    [ObservableProperty]
    public partial bool IsSnapEnabled { get; set; } = true;

    /// <summary>The grid used by Quantize (Q).</summary>
    [ObservableProperty]
    public partial GridOption QuantizeGrid { get; set; } = GridOption.For(GridDivision.Sixteenth);

    /// <summary>Quantize strength in percent.</summary>
    [ObservableProperty]
    public partial decimal QuantizeStrength { get; set; } = 100;

    /// <summary>Swing in percent; 50 is straight.</summary>
    [ObservableProperty]
    public partial decimal Swing { get; set; } = 50;

    /// <summary>The length new notes get, in ticks; follows the last note created or resized.</summary>
    public long NewNoteLength { get; set; }

    public int NewNoteVelocity { get; set; } = 100;

    [ObservableProperty]
    public partial ControllerLane Lane { get; set; } = ControllerLane.Velocity;

    partial void OnLaneChanged(ControllerLane value)
    {
        OnPropertyChanged(nameof(IsLaneOverridden));
        RaiseChanged();
    }

    /// <summary>
    /// True when a track automation lane controls what the current controller lane shows, on the track's
    /// channel as its route sends it. Automation wins, so these clip events do not play (ADR 0020).
    /// </summary>
    /// <remarks>
    /// Uses playback's own rule (<see cref="TrackRendering.Replaces"/>) on the lane's events and on what
    /// drawing in the lane would add.
    /// </remarks>
    public bool IsLaneOverridden => Track is { } track && Lane.Kind != ControllerLaneKind.Velocity && !track.Automation.IsEmpty
        && LaneEvents().Append(Lane.Create(Tick.Zero, DefaultChannel(track), 0)).Any(e => TrackRendering.Replaces(track, e, _owner.RouteChannel(track.Id)));

    /// <summary>Keep the playhead in view while playing.</summary>
    [ObservableProperty]
    public partial bool FollowPlayhead { get; set; } = true;

    /// <summary>Pixels per quarter note in the editor, independent of the arrangement's zoom.</summary>
    [ObservableProperty]
    public partial double Zoom { get; set; } = 80;

    partial void OnZoomChanged(double value)
    {
        var clamped = Math.Clamp(value, MainViewModel.MinZoom, MainViewModel.MaxZoom * 2);
        if (clamped != value)
        {
            Zoom = clamped;
        }
    }

    /// <summary>Height of one key row in pixels.</summary>
    [ObservableProperty]
    public partial double KeyHeight { get; set; } = 12;

    partial void OnKeyHeightChanged(double value)
    {
        var clamped = Math.Clamp(value, 6, 28);
        if (clamped != value)
        {
            KeyHeight = clamped;
        }
    }

    public IReadOnlySet<EventId> SelectedEvents => _selected;

    public int SelectionCount => _selected.Count;

    public bool HasSelection => _selected.Count > 0;

    /// <summary>The selected events in track order.</summary>
    public IReadOnlyList<TrackEvent> Selection => [.. Events.Where(e => _selected.Contains(e.Id))];

    public IReadOnlyList<NoteEvent> SelectedNotes => [.. Selection.OfType<NoteEvent>()];

    // Event inspector fields for the selection: a value when shared, empty when mixed.
    [ObservableProperty]
    public partial string InfoPosition { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InfoLength { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InfoPitch { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InfoVelocity { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InfoChannel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectionSummary { get; private set; } = "No selection";

    private Sequence Sequence => _owner.Project.Sequence;

    private MeterMap Meter => Sequence.MeterMap;

    /// <summary>Snaps a tick to the grid when snapping is on.</summary>
    public long SnapTick(long tick) => IsSnapEnabled ? MusicalGrid.Snap(tick, Snap.Division, Meter) : Math.Max(0, tick);

    /// <summary>The grid line at or before a tick when snapping is on, e.g. where a pencil click lands.</summary>
    public long SnapTickDown(long tick) => IsSnapEnabled ? MusicalGrid.SnapDown(tick, Snap.Division, Meter) : Math.Max(0, tick);

    /// <summary>Snaps a move or resize delta so the anchor tick lands on the grid.</summary>
    public long SnapDelta(long anchorTick, long delta) => IsSnapEnabled ? SnapTick(anchorTick + delta) - anchorTick : delta;

    /// <summary>One grid step in ticks at the playhead, for nudging and default note lengths.</summary>
    public long GridStep => MusicalGrid.StepLength(Snap.Division, Meter, new Tick(_owner.PlayheadTick));

    internal void Sync(Track? track, int colorIndex)
    {
        var changedTrack = track?.Id != Track?.Id;
        Track = track;
        ColorIndex = colorIndex;
        if (changedTrack)
        {
            _clipId = null;
        }

        var changedClip = SyncClip();
        if (changedTrack || changedClip)
        {
            _selected = [];
        }
        else
        {
            // Keep the selection across edits and undo, dropping events that no longer exist.
            _selected.IntersectWith(Events.Select(e => e.Id));
        }

        if (NewNoteLength <= 0)
        {
            NewNoteLength = Sequence.Ppqn.TicksPerQuarterNote / 4;
        }

        SyncInfo();
        OnPropertyChanged(nameof(IsLaneOverridden));
        RaiseChanged();
    }

    /// <summary>Edits <paramref name="clip"/> of the current track, e.g. when it is opened from the arrangement.</summary>
    public void EditClip(ClipId clip)
    {
        if (Track?.FindClip(clip) is NoteClip && clip != Clip?.Id)
        {
            _clipId = clip;
            Sync(Track, ColorIndex);
        }
    }

    public void Select(IEnumerable<EventId> events, SelectionMode mode = SelectionMode.Replace)
    {
        ArgumentNullException.ThrowIfNull(events);
        var list = events.ToList();
        switch (mode)
        {
            case SelectionMode.Replace:
                _selected = [.. list];
                break;
            case SelectionMode.Add:
                _selected.UnionWith(list);
                break;
            case SelectionMode.Toggle:
                _selected.SymmetricExceptWith(list);
                break;
        }

        SyncInfo();
        RaiseChanged();
    }

    /// <summary>Selects notes starting within the ticks and pitches given (inclusive), e.g. from a marquee.</summary>
    public void SelectNotesIn(long fromTick, long toTick, int lowPitch, int highPitch, SelectionMode mode = SelectionMode.Replace)
    {
        if (Track is not { } track)
        {
            return;
        }

        var (t0, t1) = (Math.Min(fromTick, toTick), Math.Max(fromTick, toTick));
        var (p0, p1) = (Math.Min(lowPitch, highPitch), Math.Max(lowPitch, highPitch));
        Select(Events.OfType<NoteEvent>()
            .Where(n => n.EndPosition.Value > t0 && n.Position.Value <= t1 && n.Note.Value >= p0 && n.Note.Value <= p1)
            .Select(n => n.Id), mode);
    }

    [RelayCommand]
    public void SelectAll()
    {
        Select(Events.OfType<ChannelEvent>().Select(e => e.Id));
    }

    [RelayCommand]
    public void SelectNone() => Select([]);

    /// <summary>Creates a note and selects it. Length and velocity default to the last used.</summary>
    public EventId? AddNote(long tick, int pitch, long? length = null, int? velocity = null)
    {
        if (Track is not { } track || pitch is < 0 or > 127)
        {
            return null;
        }

        var note = new NoteEvent(
            new Tick(Math.Max(0, tick)),
            new TickSpan(Math.Max(1, length ?? NewNoteLength)),
            DefaultChannel(track),
            new NoteNumber(pitch),
            new Velocity(Math.Clamp(velocity ?? NewNoteVelocity, 1, 127)));
        NewNoteLength = note.Duration.Value;
        Add(track, "Add Note", [note]);
        Select([note.Id]);
        return note.Id;
    }

    /// <summary>Moves the selection; with <paramref name="copy"/>, moves copies and selects them.</summary>
    public void MoveSelection(long deltaTicks, int deltaSemitones, bool copy = false)
    {
        var selection = Selection;
        if (Track is not { } track || selection.Count == 0)
        {
            return;
        }

        if (copy)
        {
            var copies = EventEdits.Move(EventEdits.Copy(selection, 0), deltaTicks, deltaSemitones);
            if (copies.IsEmpty)
            {
                return;
            }

            Add(track, copies.Length == 1 ? "Copy Note" : "Copy Notes", copies);
            Select(copies.Select(e => e.Id));
            return;
        }

        var moved = EventEdits.Move(selection, deltaTicks, deltaSemitones);
        Replace(track, moved, deltaSemitones != 0 && deltaTicks == 0 ? "Transpose" : moved.Length == 1 ? "Move Note" : "Move Notes");
    }

    /// <summary>Moves the selection by whole grid steps (arrow keys).</summary>
    public void NudgeSelection(int steps) => MoveSelection(steps * GridStep, 0);

    public void TransposeSelection(int semitones) => MoveSelection(0, semitones);

    public void ResizeSelection(NoteEdge edge, long deltaTicks)
    {
        if (Track is not { } track)
        {
            return;
        }

        var resized = EventEdits.Resize(SelectedNotes, edge, deltaTicks, minimumLength: Math.Max(1, GridStep / 4));
        if (resized.Length > 0 && resized[0] is NoteEvent first)
        {
            NewNoteLength = first.Duration.Value;
        }

        Replace(track, resized, "Resize Notes");
    }

    [RelayCommand]
    public void DeleteSelection()
    {
        if (Track is { } track && _selected.Count > 0)
        {
            _owner.Execute(ProjectCommands.RemoveEvents(track.Id, [.. _selected]));
        }
    }

    public void Delete(IReadOnlyCollection<EventId> events)
    {
        if (Track is { } track && events.Count > 0)
        {
            _owner.Execute(ProjectCommands.RemoveEvents(track.Id, events));
        }
    }

    [RelayCommand]
    public void Quantize()
    {
        if (Track is not { } track)
        {
            return;
        }

        var targets = _selected.Count > 0 ? Selection : [.. Events.Where(e => e is NoteEvent)];
        var options = new QuantizeOptions((int)QuantizeStrength, (int)Swing);
        Replace(track, EventEdits.Quantize(targets, Meter, QuantizeGrid.Division, options), "Quantize");
    }

    [RelayCommand]
    public void QuantizeEnds()
    {
        if (Track is { } track)
        {
            var options = new QuantizeOptions((int)QuantizeStrength, (int)Swing, QuantizeEnds: true);
            Replace(track, EventEdits.Quantize(SelectedNotes, Meter, QuantizeGrid.Division, options), "Quantize Ends");
        }
    }

    [RelayCommand]
    public void Legato()
    {
        if (Track is { } track)
        {
            Replace(track, EventEdits.Legato(SelectedNotes), "Legato");
        }
    }

    public void OffsetVelocity(int delta)
    {
        if (Track is { } track)
        {
            Replace(track, EventEdits.AddVelocity(SelectedNotes, delta), "Change Velocity");
        }
    }

    /// <summary>Sets velocities per note, as dragged in the velocity lane.</summary>
    public void SetVelocities(IReadOnlyDictionary<EventId, int> velocities)
    {
        ArgumentNullException.ThrowIfNull(velocities);
        if (Track is not { } track)
        {
            return;
        }

        var edited = Events.OfType<NoteEvent>()
            .Where(n => velocities.ContainsKey(n.Id))
            .SelectMany(n => EventEdits.SetVelocity([n], velocities[n.Id]))
            .ToList();
        Replace(track, edited, "Change Velocity");
    }

    /// <summary>
    /// Replaces this lane's events between two ticks with a line of values, one per grid step, as drawn
    /// with the pencil. Values are lane values (0-127, or 0-16383 for pitch bend).
    /// </summary>
    public void DrawControllerLine(long fromTick, int fromValue, long toTick, int toValue)
    {
        if (Track is not { } track || Lane.Kind == ControllerLaneKind.Velocity)
        {
            return;
        }

        if (fromTick > toTick)
        {
            (fromTick, toTick, fromValue, toValue) = (toTick, fromTick, toValue, fromValue);
        }

        fromTick = Math.Max(0, fromTick);
        var channel = DefaultChannel(track);
        var step = Math.Max(1, MusicalGrid.StepLength(IsSnapEnabled ? Snap.Division : GridDivision.SixteenthTriplet, Meter, new Tick(fromTick)));
        var points = new List<TrackEvent>();
        int? last = null;
        for (var tick = fromTick; tick <= toTick; tick += step)
        {
            var value = toTick == fromTick ? toValue : (int)Math.Round(fromValue + ((toValue - fromValue) * (tick - fromTick) / (double)(toTick - fromTick)));
            if (value != last)
            {
                points.Add(Lane.Create(new Tick(tick), channel, value));
                last = value;
            }
        }

        // What the line covers, in this clip and in any other clip the line reaches.
        var replaced = LaneEvents().Concat(track.ArrangedEvents.OfType<ChannelEvent>().Where(Lane.Matches))
            .Where(e => e.Position.Value >= fromTick && e.Position.Value <= toTick)
            .Select(e => e.Id)
            .ToHashSet();
        _owner.Execute(ProjectCommands.EditEvents(track.Id, EditedClipId, $"Draw {Lane.Name}", replaced, points));
        FollowClipOf(points);
    }

    /// <summary>Removes this lane's events between two ticks (the eraser in a controller lane).</summary>
    public void EraseControllers(long fromTick, long toTick)
    {
        if (Lane.Kind == ControllerLaneKind.Velocity)
        {
            return;
        }

        var (t0, t1) = (Math.Min(fromTick, toTick), Math.Max(fromTick, toTick));
        Delete([.. LaneEvents().Where(e => e.Position.Value >= t0 && e.Position.Value <= t1).Select(e => e.Id)]);
    }

    /// <summary>The channel events shown in the current controller lane.</summary>
    public IEnumerable<ChannelEvent> LaneEvents() => Events.OfType<ChannelEvent>().Where(Lane.Matches);

    [RelayCommand]
    public void Copy()
    {
        var selection = Selection;
        if (selection.Count > 0)
        {
            var start = selection.Min(e => e.Position.Value);
            _clipboard = EventEdits.Copy(selection, -start);
        }
    }

    [RelayCommand]
    public void Cut()
    {
        Copy();
        if (Track is { } track && _selected.Count > 0)
        {
            _owner.Execute(ProjectCommands.RemoveEvents(track.Id, [.. _selected], "Cut"));
        }
    }

    public static bool CanPaste => !_clipboard.IsEmpty;

    /// <summary>Pastes the clipboard at the playhead (snapped) and selects what was pasted.</summary>
    [RelayCommand]
    public void Paste()
    {
        if (Track is not { } track || _clipboard.IsEmpty)
        {
            return;
        }

        var pasted = EventEdits.Copy(_clipboard, SnapTickDown(_owner.PlayheadTick));
        Add(track, "Paste", pasted);
        Select(pasted.Select(e => e.Id));
    }

    /// <summary>Repeats the selection right after itself, rounded up to the grid, and selects the copy.</summary>
    [RelayCommand]
    public void Duplicate()
    {
        var selection = Selection;
        if (Track is not { } track || selection.Count == 0)
        {
            return;
        }

        var start = selection.Min(e => e.Position.Value);
        var end = selection.Max(e => e.EndPosition.Value);
        var target = IsSnapEnabled ? MusicalGrid.SnapDown(end + GridStep - 1, Snap.Division, Meter) : end;
        var copies = EventEdits.Copy(selection, Math.Max(1, target - start));
        Add(track, "Duplicate", copies);
        Select(copies.Select(e => e.Id));
    }

    /// <summary>Plays a note on the edited track's output while a key or note is held.</summary>
    public void Audition(int pitch, int velocity = 100)
    {
        if (Track is { } track && pitch is >= 0 and <= 127)
        {
            _owner.Audition(track.Id, new NoteNumber(pitch), new Velocity(Math.Clamp(velocity, 1, 127)));
        }
    }

    /// <summary>
    /// Plays a key of the piano roll's keyboard: auditioned like <see cref="Audition"/>, but recorded when
    /// the edited track is being recorded.
    /// </summary>
    public void PlayKey(int pitch, int velocity = 100)
    {
        if (Track is { } track && pitch is >= 0 and <= 127)
        {
            _owner.PlayKey(track.Id, new NoteNumber(pitch), new Velocity(Math.Clamp(velocity, 1, 127)));
        }
    }

    public void EndAudition() => _owner.EndAudition();

    partial void OnInfoPositionChanged(string value)
    {
        if (_syncingInfo || Track is not { } track || !Formatting.TryParsePosition(value, Meter, out var tick))
        {
            return;
        }

        var selection = Selection;
        if (selection.Count > 0)
        {
            Replace(track, EventEdits.Move(selection, tick.Value - selection.Min(e => e.Position.Value), 0), "Move Notes");
        }
    }

    partial void OnInfoLengthChanged(string value)
    {
        var notes = SelectedNotes;
        if (!_syncingInfo && Track is { } track && notes.Count > 0 && Formatting.TryParseLength(value, Meter, notes[0].Position, out var length))
        {
            Replace(track, EventEdits.SetLength(notes, length.Value), "Change Length");
        }
    }

    partial void OnInfoPitchChanged(string value)
    {
        var notes = SelectedNotes;
        if (!_syncingInfo && notes.Count > 0 && Formatting.TryParseNote(value, out var note))
        {
            TransposeSelection(note.Value - notes[0].Note.Value);
        }
    }

    partial void OnInfoVelocityChanged(string value)
    {
        if (!_syncingInfo && Track is { } track && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var velocity))
        {
            Replace(track, EventEdits.SetVelocity(SelectedNotes, velocity), "Change Velocity");
        }
    }

    partial void OnInfoChannelChanged(string value)
    {
        if (!_syncingInfo && Track is { } track && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 16)
        {
            Replace(track, EventEdits.SetChannel(Selection, MidiChannel.FromNumber(number)), "Change Channel");
        }
    }

    /// <summary>Replaces events of the edited clip (matched by ID) with edited versions.</summary>
    internal void Replace(Track track, IReadOnlyCollection<TrackEvent> edited, string label)
    {
        if (edited.Count > 0 && Clip is { } clip)
        {
            _owner.Execute(ProjectCommands.ReplaceEvents(track.Id, clip.Id, label, edited));
        }
    }

    private void Add(Track track, string label, IReadOnlyCollection<TrackEvent> added)
    {
        if (added.Count > 0)
        {
            _owner.Execute(ProjectCommands.AddEvents(track.Id, EditedClipId, label, added));
            FollowClipOf(added);
        }
    }

    /// <summary>The edited clip's ID, or a new one for the clip the next added events will create.</summary>
    private ClipId EditedClipId => _clipId ??= ClipId.New();

    /// <summary>Switches to the clip holding the earliest of <paramref name="added"/>, unless the edited clip received any of them.</summary>
    private void FollowClipOf(IReadOnlyCollection<TrackEvent> added)
    {
        if (added.Count == 0 || Track is not { } track)
        {
            return;
        }

        if (Clip is { } edited)
        {
            var mine = edited.Content.Items.Select(e => e.Id).ToHashSet();
            if (added.Any(e => mine.Contains(e.Id)))
            {
                return;
            }
        }

        if (track.ClipOf(added.MinBy(e => e.Position)!.Id) is { } clip && clip.Id != Clip?.Id)
        {
            _clipId = clip.Id;
            Sync(track, ColorIndex);
        }
    }

    /// <summary>Picks the edited clip for the current track and refreshes <see cref="Events"/>. Returns whether the clip changed.</summary>
    private bool SyncClip()
    {
        var previous = Clip?.Id;
        Clip = Track is not { } track
            ? null
            : (_clipId is { } id ? track.FindClip(id) as NoteClip : null)
                ?? track.ClipAt(new Tick(Math.Max(0, _owner.PlayheadTick))) as NoteClip
                ?? track.Clips.OfType<NoteClip>().FirstOrDefault();
        if (Clip is not null)
        {
            _clipId = Clip.Id;
        }

        Events = Clip is null ? [] : [.. Clip.TimelineContent()];
        return Clip?.Id != previous && previous is not null;
    }

    private static MidiChannel DefaultChannel(Track track) => track.FirstChannel ?? MidiChannel.FromIndex(0);

    private void SyncInfo()
    {
        _syncingInfo = true;
        try
        {
            var selection = Selection;
            var notes = selection.OfType<NoteEvent>().ToList();
            InfoPosition = selection.Count == 0 ? string.Empty : Formatting.Position(Meter.ToBarBeatTick(new Tick(selection.Min(e => e.Position.Value))));
            InfoLength = Shared(notes, n => Formatting.Length(n.Duration, Meter, n.Position));
            InfoPitch = Shared(notes, n => Formatting.NoteName(n.Note));
            InfoVelocity = Shared(notes, n => n.Velocity.Value.ToString(CultureInfo.InvariantCulture));
            InfoChannel = Shared(selection, e => e is ChannelEvent c ? c.Channel.Number.ToString(CultureInfo.InvariantCulture) : string.Empty);
            SelectionSummary = selection.Count switch
            {
                0 => "No selection",
                1 when notes.Count == 1 => "1 note",
                1 => "1 event",
                _ when notes.Count == selection.Count => string.Create(CultureInfo.InvariantCulture, $"{notes.Count} notes"),
                _ => string.Create(CultureInfo.InvariantCulture, $"{selection.Count} events"),
            };
            OnPropertyChanged(nameof(SelectionCount));
            OnPropertyChanged(nameof(HasSelection));
        }
        finally
        {
            _syncingInfo = false;
        }
    }

    private static string Shared<T>(IReadOnlyCollection<T> items, Func<T, string> value)
    {
        var values = items.Select(value).Distinct().Take(2).ToList();
        return values.Count == 1 ? values[0] : string.Empty;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
