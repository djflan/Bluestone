using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Bluestone.Application.Editing;
using Bluestone.Application.Recording;
using Bluestone.Desktop.Theme;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;
using Bluestone.Presentation;
using SelectionMode = Bluestone.Presentation.SelectionMode;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The piano roll: a keyboard, a bar ruler, a note grid, and a controller lane (velocity or a
/// controller) for the editor's track. Gestures are previewed while the pointer moves and committed
/// to <see cref="EditorViewModel"/> once, on release, so each gesture is one undo step.
/// </summary>
/// <remarks>
/// Arrow tool: click to select (⇧ toggles), drag notes to move (⌥ copies, ⌘/Ctrl ignores the grid),
/// drag a note's edge to resize, drag empty space to select with a marquee, double-click to add a
/// note. Pencil: click or drag to add notes; draws lines in controller lanes. Eraser: click or drag
/// over notes or controller points. Click a key to hear it.
/// </remarks>
public sealed class PianoRollView : Control
{
    public const double KeyboardWidth = 64;
    public const double RulerHeight = 26;
    public const double LaneHeight = 92;
    private const double EdgeGrip = 5;

    public static readonly StyledProperty<Project?> ProjectProperty = AvaloniaProperty.Register<PianoRollView, Project?>(nameof(Project));
    public static readonly StyledProperty<long> PlayheadTickProperty = AvaloniaProperty.Register<PianoRollView, long>(nameof(PlayheadTick));
    public static readonly StyledProperty<bool> IsRecordingProperty = AvaloniaProperty.Register<PianoRollView, bool>(nameof(IsRecording));
    public static readonly StyledProperty<UInt128> SoundingNotesProperty = AvaloniaProperty.Register<PianoRollView, UInt128>(nameof(SoundingNotes));
    public static readonly StyledProperty<IReadOnlyList<RecordingNote>> RecordingPreviewProperty = AvaloniaProperty.Register<PianoRollView, IReadOnlyList<RecordingNote>>(nameof(RecordingPreview), []);

    private static readonly IBrush WhiteRow = new SolidColorBrush(Palette.WhiteKeyRow);
    private static readonly IBrush BlackRow = new SolidColorBrush(Palette.BlackKeyRow);
    private static readonly IBrush OutsideClip = new SolidColorBrush(Colors.Black, 0.28);
    private static readonly IPen OctavePen = new Pen(new SolidColorBrush(Palette.OctaveLine), 1);
    private static readonly IPen RowPen = new Pen(new SolidColorBrush(Color.Parse("#202023")), 1);
    private static readonly IBrush WhiteKey = new SolidColorBrush(Palette.WhiteKey);
    private static readonly IBrush PressedKey = new SolidColorBrush(Palette.WhiteKeyPressed);
    private static readonly IBrush BlackKey = new SolidColorBrush(Palette.BlackKey);
    private static readonly IPen KeyEdge = new Pen(new SolidColorBrush(Color.Parse("#9A9AA0")), 1);
    private static readonly IBrush KeyLabel = new SolidColorBrush(Color.Parse("#5C5C63"));
    private static readonly IBrush Corner = new SolidColorBrush(Color.Parse("#2B2B2E"));
    private static readonly IPen Divider = new Pen(new SolidColorBrush(Palette.Divider), 1);
    private static readonly IBrush LaneBrush = new SolidColorBrush(Color.Parse("#1F1F21"));
    private static readonly IPen LaneMid = new Pen(new SolidColorBrush(Color.Parse("#2C2C30")), 1, new DashStyle([3, 3], 0));
    private static readonly IBrush MarqueeFill = new SolidColorBrush(Palette.Accent, 0.12);
    private static readonly IPen MarqueeEdge = new Pen(new SolidColorBrush(Palette.Accent, 0.8), 1);
    private static readonly IBrush PlayheadBrush = new SolidColorBrush(Palette.Playhead, 0.9);
    private static readonly IBrush RecordBrush = new SolidColorBrush(Palette.Record);
    private static readonly IBrush RecordNote = new SolidColorBrush(Palette.Lighten(Palette.Record, 0.15), 0.85);
    private static readonly IBrush NoteText = new SolidColorBrush(Color.Parse("#111113"));
    private static readonly IBrush LaneText = new SolidColorBrush(Palette.TextMuted);
    private static readonly IBrush EmptyText = new SolidColorBrush(Palette.TextMuted);
    private static readonly Typeface Face = new("Inter", FontStyle.Normal, FontWeight.Medium);

    private EditorViewModel? _editor;
    private double _scrollX;
    private double _scrollY = -1;
    private TrackId? _centeredFor;

    // Gesture state
    private Gesture _gesture;
    private Point _press;
    private NoteEvent? _anchor;
    private long _dt;
    private int _dp;
    private bool _copy;
    private bool _moved;
    private bool _free;
    private Rect _marquee;
    private SelectionMode _marqueeMode;
    private long _drawStart;
    private long _drawEnd;
    private int _drawPitch;
    private readonly HashSet<EventId> _erasing = [];
    private readonly Dictionary<EventId, int> _velocityPreview = [];
    private Dictionary<EventId, int> _velocityStart = [];
    private (long Tick, int Value) _lineStart;
    private (long Tick, int Value) _lineEnd;
    private int _auditionPitch = -1;

    static PianoRollView()
    {
        AffectsRender<PianoRollView>(ProjectProperty, PlayheadTickProperty, IsRecordingProperty, SoundingNotesProperty, RecordingPreviewProperty);
        FocusableProperty.OverrideDefaultValue<PianoRollView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<PianoRollView>(true);
    }

    private enum Gesture
    {
        None,
        Move,
        ResizeStart,
        ResizeEnd,
        Marquee,
        Draw,
        Erase,
        Velocity,
        VelocityLine,
        ControllerLine,
        ControllerErase,
        Keyboard,
        Seek,
    }

    public Project? Project
    {
        get => GetValue(ProjectProperty);
        set => SetValue(ProjectProperty, value);
    }

    public long PlayheadTick
    {
        get => GetValue(PlayheadTickProperty);
        set => SetValue(PlayheadTickProperty, value);
    }

    public bool IsRecording
    {
        get => GetValue(IsRecordingProperty);
        set => SetValue(IsRecordingProperty, value);
    }

    /// <summary>Keys drawn pressed because they are playing or held on an input, one bit per note number.</summary>
    public UInt128 SoundingNotes
    {
        get => GetValue(SoundingNotesProperty);
        set => SetValue(SoundingNotesProperty, value);
    }

    /// <summary>Notes of a take being recorded into this track, drawn live.</summary>
    public IReadOnlyList<RecordingNote> RecordingPreview
    {
        get => GetValue(RecordingPreviewProperty);
        set => SetValue(RecordingPreviewProperty, value);
    }

    public EditorViewModel? Editor
    {
        get => _editor;
        set
        {
            if (_editor is not null)
            {
                _editor.Changed -= OnEditorChanged;
                _editor.PropertyChanged -= OnEditorPropertyChanged;
            }

            _editor = value;
            if (_editor is not null)
            {
                _editor.Changed += OnEditorChanged;
                _editor.PropertyChanged += OnEditorPropertyChanged;
            }

            InvalidateVisual();
        }
    }

    /// <summary>Horizontal scroll in pixels.</summary>
    public double ScrollX
    {
        get => _scrollX;
        set => SetScroll(value, _scrollY);
    }

    /// <summary>Vertical scroll of the note grid in pixels.</summary>
    public double ScrollY
    {
        get => _scrollY;
        set => SetScroll(_scrollX, value);
    }

    public double GridWidth => Math.Max(0, Bounds.Width - KeyboardWidth);

    public double GridHeight => Math.Max(0, Bounds.Height - RulerHeight - LaneHeight);

    public double ExtentWidth => Project is { } p && _editor is { } e
        ? TimeGrid.TickToX(Math.Max(p.Sequence.EndPosition.Value, _editor.Clip is { } clip ? clip.Origin + clip.Content.EndPosition.Value : 0) + (32L * p.Sequence.Ppqn.TicksPerQuarterNote), p.Sequence, e.Zoom)
        : 0;

    public double ExtentHeight => 128 * KeyHeight;

    /// <summary>Raised when the scroll position, extent, or viewport changes, so scroll bars can follow.</summary>
    public event EventHandler? ScrollStateChanged;

    /// <summary>Raised with the tick under the pointer when the user clicks the ruler.</summary>
    public event EventHandler<long>? SeekRequested;

    private double Zoom => _editor?.Zoom ?? 80;

    private double KeyHeight => _editor?.KeyHeight ?? 12;

    private Track? Track => _editor?.Track;

    /// <summary>Scrolls so <paramref name="tick"/> is visible, paging forward as the playhead advances.</summary>
    public void Reveal(long tick)
    {
        if (Project is not { } p)
        {
            return;
        }

        var x = TimeGrid.TickToX(tick, p.Sequence, Zoom);
        if (x < _scrollX || x > _scrollX + (GridWidth * 0.92))
        {
            ScrollX = Math.Max(0, x - (GridWidth * 0.08));
        }
    }

    /// <summary>Zooms horizontally around a point in the grid, keeping the time under it in place.</summary>
    public void ZoomAround(double gridX, double factor)
    {
        if (_editor is null || Project is not { } p || factor <= 0)
        {
            return;
        }

        var tick = TimeGrid.XToTick(_scrollX + gridX, p.Sequence, Zoom);
        _editor.Zoom *= factor;
        ScrollX = Math.Max(0, TimeGrid.TickToX(tick, p.Sequence, Zoom) - gridX);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(LaneBrush, bounds);
        if (Project is not { } project || _editor is null)
        {
            return;
        }

        var sequence = project.Sequence;
        if (Track is not { } track)
        {
            var message = new FormattedText("Select a track to edit its notes.", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 12, EmptyText);
            context.DrawText(message, new Point((bounds.Width - message.Width) / 2, (bounds.Height - message.Height) / 2));
            return;
        }

        EnsureCentered(track);
        var grid = new Rect(KeyboardWidth, RulerHeight, GridWidth, GridHeight);
        var lane = new Rect(KeyboardWidth, grid.Bottom + 1, GridWidth, LaneHeight - 1);
        var color = Palette.Track(_editor.ColorIndex);

        // Note grid
        using (context.PushClip(grid))
        {
            DrawRows(context, grid);
            using (context.PushTransform(Matrix.CreateTranslation(KeyboardWidth, 0)))
            {
                var local = new Rect(0, grid.Top, grid.Width, grid.Height);
                TimeGrid.DrawLines(context, sequence, Zoom, _scrollX, local, _editor.Snap.Division);
                TimeGrid.ShadeCycle(context, sequence, project.Loop, Zoom, _scrollX, local);
                ShadeOutsideClip(context, sequence, local);
                DrawNotes(context, sequence, track, color, local);
                DrawRecording(context, sequence, local);
                if (_gesture == Gesture.Marquee)
                {
                    context.DrawRectangle(MarqueeFill, MarqueeEdge, _marquee.Translate(new Vector(-KeyboardWidth, 0)));
                }

                DrawPlayhead(context, sequence, local);
            }
        }

        // Controller lane
        context.DrawLine(Divider, new Point(0, grid.Bottom + 0.5), new Point(bounds.Width, grid.Bottom + 0.5));
        using (context.PushClip(lane))
        {
            using (context.PushTransform(Matrix.CreateTranslation(KeyboardWidth, 0)))
            {
                var local = new Rect(0, lane.Top, lane.Width, lane.Height);
                TimeGrid.DrawLines(context, sequence, Zoom, _scrollX, local);
                context.DrawLine(LaneMid, new Point(0, Math.Round(local.Center.Y) + 0.5), new Point(local.Right, Math.Round(local.Center.Y) + 0.5));
                DrawLane(context, sequence, track, color, local);
                DrawPlayhead(context, sequence, local);
            }
        }

        // Ruler, keyboard, and lane label
        using (context.PushClip(new Rect(KeyboardWidth, 0, GridWidth, RulerHeight)))
        using (context.PushTransform(Matrix.CreateTranslation(KeyboardWidth, 0)))
        {
            TimeGrid.DrawRuler(context, sequence, project.Loop, Zoom, _scrollX, new Rect(0, 0, GridWidth, RulerHeight), PlayheadTick, IsRecording);
        }

        context.FillRectangle(Corner, new Rect(0, 0, KeyboardWidth, RulerHeight));
        context.DrawLine(Divider, new Point(0, RulerHeight - 0.5), new Point(KeyboardWidth, RulerHeight - 0.5));
        using (context.PushClip(new Rect(0, RulerHeight, KeyboardWidth, grid.Height)))
        {
            DrawKeyboard(context, grid);
        }

        context.FillRectangle(Corner, new Rect(0, lane.Top, KeyboardWidth, lane.Height));
        var laneName = new FormattedText(_editor.Lane.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, LaneText)
        {
            MaxTextWidth = KeyboardWidth - 10,
            TextAlignment = TextAlignment.Left,
        };
        context.DrawText(laneName, new Point(6, lane.Top + 5));
        context.DrawLine(Divider, new Point(KeyboardWidth - 0.5, 0), new Point(KeyboardWidth - 0.5, bounds.Height));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SetScroll(_scrollX, _scrollY);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var command = CommandModifiers;
        var point = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(command))
        {
            ZoomAround(point.X - KeyboardWidth, Math.Pow(1.12, e.Delta.Y));
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && _editor is not null)
        {
            var pitch = PitchAt(point.Y);
            _editor.KeyHeight *= Math.Pow(1.1, e.Delta.Y);
            ScrollY = ((127 - pitch) * KeyHeight) - (point.Y - RulerHeight);
        }
        else
        {
            var dx = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? e.Delta.Y : e.Delta.X;
            var dy = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0 : e.Delta.Y;
            SetScroll(_scrollX - (dx * 40), _scrollY - (dy * 40));
        }

        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (_editor is null || Track is null || Project is null || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        e.Pointer.Capture(this);
        e.Handled = true;
        _press = point.Position;
        _moved = false;
        _copy = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        _free = e.KeyModifiers.HasFlag(CommandModifiers);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var x = _press.X;
        var y = _press.Y;

        if (y < RulerHeight)
        {
            _gesture = x >= KeyboardWidth ? Gesture.Seek : Gesture.None;
            if (_gesture == Gesture.Seek)
            {
                SeekRequested?.Invoke(this, TickAt(x));
            }

            return;
        }

        if (y >= RulerHeight + GridHeight)
        {
            if (x >= KeyboardWidth)
            {
                PressLane(x, y);
            }

            return;
        }

        if (x < KeyboardWidth)
        {
            _gesture = Gesture.Keyboard;
            Audition(PitchAt(y), key: true);
            return;
        }

        var tick = TickAt(x);
        var pitch = PitchAt(y);
        var hit = HitNote(x, y, out var edge);

        if (_editor.Tool == EditTool.Eraser)
        {
            _gesture = Gesture.Erase;
            _erasing.Clear();
            if (hit is not null)
            {
                _erasing.Add(hit.Id);
            }

            InvalidateVisual();
            return;
        }

        if (hit is not null)
        {
            if (shift)
            {
                _editor.Select([hit.Id], SelectionMode.Toggle);
                _gesture = Gesture.None;
                return;
            }

            if (!_editor.SelectedEvents.Contains(hit.Id))
            {
                _editor.Select([hit.Id]);
            }

            _anchor = hit;
            _dt = 0;
            _dp = 0;
            _gesture = edge switch
            {
                NoteEdge.Start => Gesture.ResizeStart,
                NoteEdge.End => Gesture.ResizeEnd,
                _ => Gesture.Move,
            };
            Audition(hit.Note.Value, hit.Velocity.Value);
            return;
        }

        if (_editor.Tool == EditTool.Pencil || e.ClickCount >= 2)
        {
            _gesture = Gesture.Draw;
            _drawStart = _free ? tick : _editor.SnapTickDown(tick);
            _drawEnd = _drawStart + _editor.NewNoteLength;
            _drawPitch = pitch;
            Audition(pitch);
            InvalidateVisual();
            return;
        }

        _gesture = Gesture.Marquee;
        _marqueeMode = shift ? SelectionMode.Toggle : SelectionMode.Replace;
        _marquee = new Rect(_press, _press);
        if (!shift)
        {
            _editor.SelectNone();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        if (!ReferenceEquals(e.Pointer.Captured, this) || _editor is null || Project is not { } project)
        {
            UpdateCursor(position);
            return;
        }

        var sequence = project.Sequence;
        _moved |= Math.Abs(position.X - _press.X) > 2 || Math.Abs(position.Y - _press.Y) > 2;
        switch (_gesture)
        {
            case Gesture.Seek:
                SeekRequested?.Invoke(this, TickAt(position.X));
                break;
            case Gesture.Keyboard:
                if (PitchAt(position.Y) != _auditionPitch)
                {
                    Audition(PitchAt(position.Y), key: true);
                }

                break;
            case Gesture.Move when _anchor is { } anchor && _moved:
                var raw = DeltaTicks(position.X - _press.X);
                _dt = _free ? raw : _editor.SnapDelta(anchor.Position.Value, raw);
                var dp = PitchAt(position.Y) - PitchAt(_press.Y);
                if (dp != _dp)
                {
                    _dp = dp;
                    Audition(Math.Clamp(anchor.Note.Value + dp, 0, 127), anchor.Velocity.Value);
                }

                break;
            case Gesture.ResizeEnd when _anchor is { } anchor:
                var endRaw = anchor.EndPosition.Value + DeltaTicks(position.X - _press.X);
                _dt = (_free ? endRaw : _editor.SnapTick(endRaw)) - anchor.EndPosition.Value;
                break;
            case Gesture.ResizeStart when _anchor is { } anchor:
                var startRaw = anchor.Position.Value + DeltaTicks(position.X - _press.X);
                _dt = (_free ? startRaw : _editor.SnapTick(startRaw)) - anchor.Position.Value;
                break;
            case Gesture.Marquee:
                _marquee = new Rect(_press, position).Normalize();
                break;
            case Gesture.Draw:
                var end = TickAt(position.X);
                var snappedEnd = _free ? end : _editor.SnapTick(end);
                _drawEnd = Math.Max(_drawStart + Math.Max(1, _free ? 1 : _editor.GridStep / 4), snappedEnd > _drawStart ? snappedEnd : _drawStart + _editor.NewNoteLength);
                if (!_moved)
                {
                    _drawEnd = _drawStart + _editor.NewNoteLength;
                }

                break;
            case Gesture.Erase:
                if (HitNote(position.X, position.Y, out _) is { } erased)
                {
                    _erasing.Add(erased.Id);
                }

                break;
            case Gesture.Velocity:
                var dv = (int)Math.Round(-(position.Y - _press.Y) / (LaneHeight - 12) * 127);
                _velocityPreview.Clear();
                foreach (var (id, start) in _velocityStart)
                {
                    _velocityPreview[id] = Math.Clamp(start + dv, 1, 127);
                }

                break;
            case Gesture.VelocityLine:
                _lineEnd = (TickAt(position.X), LaneValue(position.Y, 127));
                PreviewVelocityLine();
                break;
            case Gesture.ControllerLine or Gesture.ControllerErase:
                _lineEnd = (TickAt(position.X), LaneValue(position.Y, _editor.Lane.MaxValue));
                break;
        }

        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // Clear the gesture before releasing capture: losing capture would otherwise cancel it.
        var gesture = _gesture;
        _gesture = Gesture.None;
        e.Pointer.Capture(null);
        if (_editor is not { } editor)
        {
            return;
        }

        EndAudition();
        switch (gesture)
        {
            case Gesture.Move when !_moved && _anchor is { } clicked && editor.SelectionCount > 1:
                editor.Select([clicked.Id]);
                break;
            case Gesture.Move when _moved && (_dt != 0 || _dp != 0):
                editor.MoveSelection(_dt, _dp, _copy);
                break;
            case Gesture.ResizeEnd when _dt != 0:
                editor.ResizeSelection(NoteEdge.End, _dt);
                break;
            case Gesture.ResizeStart when _dt != 0:
                editor.ResizeSelection(NoteEdge.Start, _dt);
                break;
            case Gesture.Marquee when _moved:
                editor.SelectNotesIn(TickAt(_marquee.Left), TickAt(_marquee.Right), PitchAt(_marquee.Bottom), PitchAt(_marquee.Top), _marqueeMode);
                break;
            case Gesture.Draw:
                editor.AddNote(_drawStart, _drawPitch, Math.Max(1, _drawEnd - _drawStart));
                break;
            case Gesture.Erase when _erasing.Count > 0:
                editor.Delete([.. _erasing]);
                _erasing.Clear();
                break;
            case Gesture.Velocity or Gesture.VelocityLine when _velocityPreview.Count > 0:
                editor.SetVelocities(new Dictionary<EventId, int>(_velocityPreview));
                break;
            case Gesture.ControllerLine:
                editor.DrawControllerLine(_lineStart.Tick, _lineStart.Value, _lineEnd.Tick, _lineEnd.Value);
                break;
            case Gesture.ControllerErase:
                editor.EraseControllers(_lineStart.Tick, _lineEnd.Tick);
                break;
        }

        _velocityPreview.Clear();
        _erasing.Clear();
        _anchor = null;
        _dt = 0;
        _dp = 0;
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_gesture != Gesture.None)
        {
            _gesture = Gesture.None;
            EndAudition();
            InvalidateVisual();
        }
    }

    /// <summary>Editing keys while the piano roll has focus. Returns true when handled.</summary>
    public bool HandleKey(Key key, KeyModifiers modifiers)
    {
        if (_editor is not { } editor || Track is null)
        {
            return false;
        }

        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var plain = (modifiers & ~(KeyModifiers.Shift | KeyModifiers.Alt)) == KeyModifiers.None;
        if (!plain)
        {
            return false;
        }

        switch (key)
        {
            case Key.Delete or Key.Back when !shift && !alt:
                editor.DeleteSelection();
                return true;
            case Key.Escape:
                editor.SelectNone();
                return true;
            case Key.Left or Key.Right when !alt:
                editor.NudgeSelection((key == Key.Right ? 1 : -1) * (shift ? 4 : 1));
                return true;
            case Key.Up or Key.Down when alt:
                editor.OffsetVelocity((key == Key.Up ? 1 : -1) * (shift ? 10 : 2));
                return true;
            case Key.Up or Key.Down:
                editor.TransposeSelection((key == Key.Up ? 1 : -1) * (shift ? 12 : 1));
                return true;
            case Key.Q when !alt:
                if (shift)
                {
                    editor.QuantizeEnds();
                }
                else
                {
                    editor.Quantize();
                }

                return true;
            default:
                return false;
        }
    }

    private void PressLane(double x, double y)
    {
        var editor = _editor!;
        var tick = TickAt(x);
        if (editor.Lane.Kind == ControllerLaneKind.Velocity)
        {
            if (editor.Tool == EditTool.Pencil)
            {
                _gesture = Gesture.VelocityLine;
                _lineStart = _lineEnd = (tick, LaneValue(y, 127));
                PreviewVelocityLine();
            }
            else if (HitStem(x) is { } note)
            {
                _gesture = Gesture.Velocity;
                if (!editor.SelectedEvents.Contains(note.Id))
                {
                    editor.Select([note.Id]);
                }

                _velocityStart = editor.SelectedNotes.ToDictionary(n => n.Id, n => (int)n.Velocity.Value);
                _velocityPreview.Clear();
            }

            InvalidateVisual();
            return;
        }

        _gesture = editor.Tool == EditTool.Eraser ? Gesture.ControllerErase : Gesture.ControllerLine;
        _lineStart = _lineEnd = (tick, LaneValue(y, editor.Lane.MaxValue));
        InvalidateVisual();
    }

    private void PreviewVelocityLine()
    {
        _velocityPreview.Clear();
        if (Track is not { } track)
        {
            return;
        }

        var (t0, v0) = _lineStart;
        var (t1, v1) = _lineEnd;
        if (t0 > t1)
        {
            (t0, v0, t1, v1) = (t1, v1, t0, v0);
        }

        foreach (var note in _editor!.Events.OfType<NoteEvent>())
        {
            var start = note.Position.Value;
            if (start >= t0 - 2 && start <= t1 + 2)
            {
                var value = t1 == t0 ? v1 : v0 + ((v1 - v0) * (start - t0) / (double)(t1 - t0));
                _velocityPreview[note.Id] = Math.Clamp((int)Math.Round(value), 1, 127);
            }
        }
    }

    private void DrawRows(DrawingContext context, Rect grid)
    {
        var (top, bottom) = VisiblePitches(grid);
        for (var pitch = top; pitch >= bottom; pitch--)
        {
            var y = RowTop(pitch);
            context.FillRectangle(IsBlack(pitch) ? BlackRow : WhiteRow, new Rect(grid.Left, y, grid.Width, KeyHeight));
            var line = Math.Round(y + KeyHeight) - 0.5;
            context.DrawLine(pitch % 12 == 0 ? OctavePen : RowPen, new Point(grid.Left, line), new Point(grid.Right, line));
        }
    }

    private void DrawKeyboard(DrawingContext context, Rect grid)
    {
        var (top, bottom) = VisiblePitches(grid);
        context.FillRectangle(WhiteKey, new Rect(0, grid.Top, KeyboardWidth, grid.Height));
        var blackWidth = KeyboardWidth * 0.6;
        var sounding = SoundingNotes;
        for (var pitch = top; pitch >= bottom; pitch--)
        {
            var y = RowTop(pitch);
            var pressed = pitch == _auditionPitch || ((sounding >> pitch) & 1) != 0;
            if (IsBlack(pitch))
            {
                context.FillRectangle(pressed ? PressedKey : BlackKey, new Rect(0, y, blackWidth, KeyHeight), 1);

                // White keys meet halfway along the black key between them.
                var mid = Math.Round(y + (KeyHeight / 2)) + 0.5;
                context.DrawLine(KeyEdge, new Point(blackWidth, mid), new Point(KeyboardWidth, mid));
            }
            else
            {
                if (pressed)
                {
                    context.FillRectangle(PressedKey, new Rect(0, y, KeyboardWidth, KeyHeight));
                }

                if (pitch % 12 is 0 or 5)
                {
                    var line = Math.Round(y + KeyHeight) - 0.5;
                    context.DrawLine(KeyEdge, new Point(0, line), new Point(KeyboardWidth, line));
                }

                if (pitch % 12 == 0 && KeyHeight >= 8)
                {
                    var label = new FormattedText(Formatting.NoteName(new Domain.Midi.NoteNumber(pitch)), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, Math.Min(9.5, KeyHeight - 1), KeyLabel);
                    context.DrawText(label, new Point(KeyboardWidth - label.Width - 4, y + ((KeyHeight - label.Height) / 2)));
                }
            }
        }
    }

    /// <summary>Darkens the time outside the edited clip, where notes are kept but do not play.</summary>
    private void ShadeOutsideClip(DrawingContext context, Sequence sequence, Rect area)
    {
        if (_editor?.Clip is not { } clip)
        {
            return;
        }

        var start = TimeGrid.TickToX(clip.Start.Value, sequence, Zoom) - _scrollX;
        var end = TimeGrid.TickToX(clip.End.Value, sequence, Zoom) - _scrollX;
        if (start > area.Left)
        {
            context.FillRectangle(OutsideClip, new Rect(area.Left, area.Top, Math.Min(start, area.Right) - area.Left, area.Height));
        }

        if (end < area.Right)
        {
            context.FillRectangle(OutsideClip, new Rect(Math.Max(end, area.Left), area.Top, area.Right - Math.Max(end, area.Left), area.Height));
        }
    }

    private void DrawNotes(DrawingContext context, Sequence sequence, Track track, Color color, Rect area)
    {
        var editor = _editor!;
        var firstTick = TimeGrid.XToTick(_scrollX - 4, sequence, Zoom);
        var lastTick = TimeGrid.XToTick(_scrollX + area.Width + 4, sequence, Zoom);
        var selected = editor.SelectedEvents;
        var dragging = _gesture is Gesture.Move or Gesture.ResizeStart or Gesture.ResizeEnd && _anchor is not null;
        IReadOnlyDictionary<EventId, NoteEvent>? preview = dragging ? DragPreview() : null;
        var showNames = KeyHeight >= 11;
        var border = new Pen(new SolidColorBrush(Palette.Darken(color, 0.5)), 1);
        var selectedBorder = new Pen(Brushes.White, 1);

        foreach (var e in _editor!.Events)
        {
            if (e is not NoteEvent original || original.EndPosition.Value < firstTick || original.Position.Value > lastTick)
            {
                continue;
            }

            var isSelected = selected.Contains(original.Id);
            var erased = _erasing.Contains(original.Id);
            if (preview is not null && isSelected && !_copy)
            {
                // The moving notes are drawn at their new place below; leave a faint ghost here.
                DrawNote(context, sequence, original, Palette.Mix(Palette.WhiteKeyRow, color, 0.25), null, 1, false, area);
                continue;
            }

            var velocity = _velocityPreview.TryGetValue(original.Id, out var v) ? v : original.Velocity.Value;
            var fill = isSelected ? Palette.Lighten(color, 0.55) : Palette.Mix(Palette.Darken(color, 0.35), color, 0.35 + (0.65 * velocity / 127.0));
            var hidden = editor.Clip is { } clip && !clip.Contains(original.Position);
            DrawNote(context, sequence, original, fill, isSelected ? selectedBorder : border, erased || hidden ? 0.3 : 1, showNames, area);
        }

        if (preview is not null)
        {
            foreach (var note in preview.Values)
            {
                DrawNote(context, sequence, note, Palette.Lighten(color, 0.55), selectedBorder, 0.95, showNames, area);
            }
        }

        if (_gesture == Gesture.Draw)
        {
            var draft = new NoteEvent(new Domain.Time.Tick(Math.Max(0, _drawStart)), new Domain.Time.TickSpan(Math.Max(1, _drawEnd - _drawStart)), Domain.Midi.MidiChannel.FromIndex(0), new Domain.Midi.NoteNumber(_drawPitch), new Domain.Midi.Velocity(Math.Clamp(editor.NewNoteVelocity, 1, 127)));
            DrawNote(context, sequence, draft, Palette.Lighten(color, 0.55), selectedBorder, 0.9, showNames, area);
        }
    }

    private void DrawNote(DrawingContext context, Sequence sequence, NoteEvent note, Color fill, IPen? pen, double opacity, bool showName, Rect area)
    {
        var x = TimeGrid.TickToX(note.Position.Value, sequence, Zoom) - _scrollX;
        var w = Math.Max(3, TimeGrid.TickToX(note.Duration.Value, sequence, Zoom) - 1);
        var y = RowTop(note.Note.Value) + 1;
        var h = Math.Max(2, KeyHeight - 2);
        if (x > area.Right || x + w < 0 || y > area.Bottom || y + h < area.Top)
        {
            return;
        }

        var rect = new Rect(Math.Round(x) + 0.5, Math.Round(y) + 0.5, Math.Round(w), Math.Round(h) - 1);
        using (context.PushOpacity(opacity))
        {
            context.DrawRectangle(new SolidColorBrush(fill), pen, rect, 2, 2);
            if (showName && rect.Width > 26)
            {
                var name = new FormattedText(Formatting.NoteName(note.Note), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, Math.Min(9.5, KeyHeight - 3), NoteText)
                {
                    MaxTextWidth = rect.Width - 4,
                    MaxLineCount = 1,
                };
                context.DrawText(name, new Point(rect.X + 3, rect.Y + ((rect.Height - name.Height) / 2)));
            }
        }
    }

    private void DrawRecording(DrawingContext context, Sequence sequence, Rect area)
    {
        foreach (var note in RecordingPreview)
        {
            var x = TimeGrid.TickToX(note.Start, sequence, Zoom) - _scrollX;
            var w = Math.Max(3, TimeGrid.TickToX(note.End - note.Start, sequence, Zoom));
            var y = RowTop(note.Note.Value) + 1;
            if (x <= area.Right && x + w >= 0)
            {
                context.FillRectangle(RecordNote, new Rect(x, y, w, Math.Max(2, KeyHeight - 3)), 2);
            }
        }
    }

    private void DrawPlayhead(DrawingContext context, Sequence sequence, Rect area)
    {
        var x = Math.Round(TimeGrid.TickToX(PlayheadTick, sequence, Zoom) - _scrollX);
        if (x >= -1 && x <= area.Right)
        {
            context.FillRectangle(IsRecording ? RecordBrush : PlayheadBrush, new Rect(x, area.Top, 1, area.Height));
        }
    }

    private void DrawLane(DrawingContext context, Sequence sequence, Track track, Color color, Rect area)
    {
        var editor = _editor!;
        var firstTick = TimeGrid.XToTick(_scrollX - 8, sequence, Zoom);
        var lastTick = TimeGrid.XToTick(_scrollX + area.Width + 8, sequence, Zoom);
        var usable = area.Height - 10;
        double Y(int value, int max) => area.Bottom - 4 - (usable * value / (double)max);

        if (editor.Lane.Kind == ControllerLaneKind.Velocity)
        {
            var stem = new Pen(new SolidColorBrush(Palette.Lighten(color, 0.1)), 1.5);
            var selectedStem = new Pen(new SolidColorBrush(Palette.Lighten(color, 0.7)), 1.5);
            foreach (var note in _editor!.Events.OfType<NoteEvent>())
            {
                if (note.Position.Value < firstTick || note.Position.Value > lastTick)
                {
                    continue;
                }

                var velocity = _velocityPreview.TryGetValue(note.Id, out var v) ? v : note.Velocity.Value;
                var x = Math.Round(TimeGrid.TickToX(note.Position.Value, sequence, Zoom) - _scrollX) + 0.5;
                var y = Y(velocity, 127);
                var isSelected = editor.SelectedEvents.Contains(note.Id);
                context.DrawLine(isSelected ? selectedStem : stem, new Point(x, area.Bottom - 2), new Point(x, y));
                context.FillRectangle(new SolidColorBrush(isSelected ? Palette.Selection : Palette.Lighten(color, 0.35)), new Rect(x - 3, y - 1.5, 6, 3), 1);
            }

            return;
        }

        var lane = editor.Lane;
        var line = new Pen(new SolidColorBrush(Palette.Lighten(color, 0.2)), 1.25);
        var dot = new SolidColorBrush(Palette.Lighten(color, 0.5));
        var fill = new SolidColorBrush(color, 0.22);
        var points = editor.LaneEvents().Select(e => (Tick: e.Position.Value, Value: ControllerLane.ValueOf(e))).ToList();
        if (_gesture is Gesture.ControllerLine or Gesture.ControllerErase)
        {
            var (t0, t1) = (Math.Min(_lineStart.Tick, _lineEnd.Tick), Math.Max(_lineStart.Tick, _lineEnd.Tick));
            points.RemoveAll(p => p.Tick >= t0 && p.Tick <= t1);
        }

        for (var i = 0; i < points.Count; i++)
        {
            var (tick, value) = points[i];
            var x = TimeGrid.TickToX(tick, sequence, Zoom) - _scrollX;
            var next = i + 1 < points.Count ? TimeGrid.TickToX(points[i + 1].Tick, sequence, Zoom) - _scrollX : area.Right;
            if (next < 0 || x > area.Right)
            {
                continue;
            }

            var y = Y(value, lane.MaxValue);
            context.FillRectangle(fill, new Rect(x, y, Math.Max(1, next - x), area.Bottom - y));
            context.DrawLine(line, new Point(x, y), new Point(next, y));
            if (i + 1 < points.Count)
            {
                context.DrawLine(line, new Point(next, y), new Point(next, Y(points[i + 1].Value, lane.MaxValue)));
            }

            context.FillRectangle(dot, new Rect(x - 2, y - 2, 4, 4));
        }

        if (_gesture == Gesture.ControllerLine)
        {
            var pen = new Pen(new SolidColorBrush(Palette.Selection), 1.5);
            context.DrawLine(pen, new Point(TimeGrid.TickToX(_lineStart.Tick, sequence, Zoom) - _scrollX, Y(_lineStart.Value, lane.MaxValue)), new Point(TimeGrid.TickToX(_lineEnd.Tick, sequence, Zoom) - _scrollX, Y(_lineEnd.Value, lane.MaxValue)));
        }
        else if (_gesture == Gesture.ControllerErase)
        {
            var x0 = TimeGrid.TickToX(Math.Min(_lineStart.Tick, _lineEnd.Tick), sequence, Zoom) - _scrollX;
            var x1 = TimeGrid.TickToX(Math.Max(_lineStart.Tick, _lineEnd.Tick), sequence, Zoom) - _scrollX;
            context.FillRectangle(new SolidColorBrush(Palette.Record, 0.15), new Rect(x0, area.Top, Math.Max(1, x1 - x0), area.Height));
        }
    }

    private Dictionary<EventId, NoteEvent> DragPreview()
    {
        var editor = _editor!;
        var selection = editor.Selection;
        IEnumerable<TrackEvent> moved = _gesture switch
        {
            Gesture.Move => EventEdits.Move(selection, _dt, _dp) is { Length: > 0 } m ? m : selection,
            Gesture.ResizeEnd => Merge(selection, EventEdits.Resize(editor.SelectedNotes, NoteEdge.End, _dt, Math.Max(1, editor.GridStep / 4))),
            Gesture.ResizeStart => Merge(selection, EventEdits.Resize(editor.SelectedNotes, NoteEdge.Start, _dt, Math.Max(1, editor.GridStep / 4))),
            _ => selection,
        };
        return moved.OfType<NoteEvent>().ToDictionary(n => n.Id);

        static IEnumerable<TrackEvent> Merge(IReadOnlyList<TrackEvent> all, IReadOnlyList<TrackEvent> changed)
        {
            var map = changed.ToDictionary(e => e.Id);
            return all.Select(e => map.TryGetValue(e.Id, out var c) ? c : e);
        }
    }

    private NoteEvent? HitNote(double x, double y, out NoteEdge? edge)
    {
        edge = null;
        if (Track is not { } track || Project is not { } project)
        {
            return null;
        }

        var pitch = PitchAt(y);
        var tick = TickAt(x);
        var sequence = project.Sequence;
        NoteEvent? best = null;
        foreach (var e in _editor!.Events)
        {
            if (e is NoteEvent note && note.Note.Value == pitch)
            {
                var x0 = KeyboardWidth + TimeGrid.TickToX(note.Position.Value, sequence, Zoom) - _scrollX;
                var x1 = x0 + Math.Max(3, TimeGrid.TickToX(note.Duration.Value, sequence, Zoom));
                if (x >= x0 - 1 && x <= x1 + 1)
                {
                    best = note;
                    var width = x1 - x0;
                    edge = width > EdgeGrip * 3 && x >= x1 - EdgeGrip ? NoteEdge.End
                        : width > EdgeGrip * 4 && x <= x0 + EdgeGrip ? NoteEdge.Start
                        : null;
                }
            }
            else if (e.Position.Value > tick)
            {
                break;
            }
        }

        return best;
    }

    private NoteEvent? HitStem(double x)
    {
        if (Track is not { } track || Project is not { } project)
        {
            return null;
        }

        var sequence = project.Sequence;
        var notes = _editor!.Events.OfType<NoteEvent>()
            .Select(n => (Note: n, Distance: Math.Abs(KeyboardWidth + TimeGrid.TickToX(n.Position.Value, sequence, Zoom) - _scrollX - x)))
            .Where(p => p.Distance <= 5)
            .OrderBy(p => _editor!.SelectedEvents.Contains(p.Note.Id) ? 0 : 1)
            .ThenBy(p => p.Distance)
            .ToList();
        return notes.Count > 0 ? notes[0].Note : null;
    }

    private void UpdateCursor(Point position)
    {
        if (_editor is null || position.X < KeyboardWidth || position.Y < RulerHeight)
        {
            Cursor = Cursor.Default;
            return;
        }

        if (position.Y >= RulerHeight + GridHeight)
        {
            Cursor = new Cursor(_editor.Tool == EditTool.Arrow && _editor.Lane.Kind == ControllerLaneKind.Velocity ? StandardCursorType.SizeNorthSouth : StandardCursorType.Cross);
            return;
        }

        var hit = HitNote(position.X, position.Y, out var edge);
        Cursor = _editor.Tool switch
        {
            EditTool.Eraser => new Cursor(StandardCursorType.Cross),
            _ when hit is not null && edge is not null => new Cursor(StandardCursorType.SizeWestEast),
            _ when hit is not null => new Cursor(StandardCursorType.Hand),
            EditTool.Pencil => new Cursor(StandardCursorType.Cross),
            _ => Cursor.Default,
        };
    }

    // A key on the keyboard is played (and recorded while recording); a note being edited is only heard.
    private void Audition(int pitch, int velocity = 100, bool key = false)
    {
        if (_auditionPitch == pitch)
        {
            return;
        }

        _auditionPitch = pitch;
        if (key)
        {
            _editor?.PlayKey(pitch, velocity);
        }
        else
        {
            _editor?.Audition(pitch, velocity);
        }

        InvalidateVisual();
    }

    private void EndAudition()
    {
        if (_auditionPitch >= 0)
        {
            _auditionPitch = -1;
            _editor?.EndAudition();
            InvalidateVisual();
        }
    }

    private void EnsureCentered(Track track)
    {
        if (_centeredFor == track.Id && _scrollY >= 0)
        {
            return;
        }

        _centeredFor = track.Id;
        var notes = _editor!.Events.OfType<NoteEvent>().ToList();
        var center = notes.Count > 0 ? (notes.Min(n => n.Note.Value) + notes.Max(n => n.Note.Value)) / 2.0 : 60;

        // Called while rendering, so the scroll is set without invalidating; scroll bars catch up after the frame.
        _scrollY = Math.Clamp(((127.5 - center) * KeyHeight) - (GridHeight / 2), 0, Math.Max(0, ExtentHeight - GridHeight));
        _scrollX = Math.Clamp(_scrollX, 0, Math.Max(0, ExtentWidth - GridWidth));
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ScrollStateChanged?.Invoke(this, EventArgs.Empty));
    }

    private void SetScroll(double x, double y, bool notify = true)
    {
        var maxX = Math.Max(0, ExtentWidth - GridWidth);
        var maxY = Math.Max(0, ExtentHeight - GridHeight);
        var nx = Math.Clamp(x, 0, maxX);
        var ny = Math.Clamp(y, 0, maxY);
        var changed = nx != _scrollX || ny != _scrollY;
        _scrollX = nx;
        _scrollY = ny;
        if (changed)
        {
            InvalidateVisual();
        }

        if (notify)
        {
            ScrollStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnEditorChanged(object? sender, EventArgs e)
    {
        SetScroll(_scrollX, _scrollY);
        InvalidateVisual();
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorViewModel.Zoom) or nameof(EditorViewModel.KeyHeight) or nameof(EditorViewModel.Lane) or nameof(EditorViewModel.Snap) or nameof(EditorViewModel.Tool) or nameof(EditorViewModel.ColorIndex))
        {
            SetScroll(_scrollX, _scrollY);
            InvalidateVisual();
        }
    }

    private (int Top, int Bottom) VisiblePitches(Rect grid) =>
        (Math.Clamp(PitchAt(grid.Top), 0, 127), Math.Clamp(PitchAt(grid.Bottom - 1), 0, 127));

    private double RowTop(int pitch) => RulerHeight + ((127 - pitch) * KeyHeight) - _scrollY;

    private int PitchAt(double y) => Math.Clamp(127 - (int)Math.Floor((y - RulerHeight + _scrollY) / KeyHeight), 0, 127);

    private long TickAt(double x) => Project is { } p ? TimeGrid.XToTick(Math.Max(0, x - KeyboardWidth + _scrollX), p.Sequence, Zoom) : 0;

    private long DeltaTicks(double dx) => Project is { } p ? (long)Math.Round(dx * p.Sequence.Ppqn.TicksPerQuarterNote / Zoom) : 0;

    private int LaneValue(double y, int max)
    {
        var top = RulerHeight + GridHeight + 1;
        var usable = LaneHeight - 1 - 10;
        var fraction = (top + LaneHeight - 1 - 4 - y) / usable;
        return Math.Clamp((int)Math.Round(fraction * max), 0, max);
    }

    private static KeyModifiers CommandModifiers =>
        Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    private static bool IsBlack(int pitch) => (pitch % 12) is 1 or 3 or 6 or 8 or 10;
}
