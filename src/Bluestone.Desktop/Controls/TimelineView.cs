using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Bluestone.Application.Recording;
using Bluestone.Desktop.Theme;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The arrangement: one row per track, with each of the track's clips drawn as a coloured block
/// (named on a header strip) containing its notes, and, for expanded tracks, one row per automation
/// lane below. Drawn directly with the GPU-backed renderer and culled to the visible region, so dense
/// projects stay smooth.
/// </summary>
/// <remarks>
/// Click a clip to select it (Shift adds, Cmd/Ctrl toggles) and drag to move the selection, also to
/// other tracks; hold Alt (Option) to copy. Drag a clip's edge to trim or extend it. Double-click a
/// clip to edit it, or its name strip to rename it, or empty space to create a clip filling that bar.
/// Click empty space to select the track.
/// In an automation lane, click to add a point and drag to move it; Alt-click deletes a point and
/// double-click switches it between holding and ramping. Shift turns snapping off.
/// </remarks>
public sealed class TimelineView : Control
{
    public static readonly StyledProperty<Project?> ProjectProperty = AvaloniaProperty.Register<TimelineView, Project?>(nameof(Project));
    public static readonly StyledProperty<double> PixelsPerQuarterProperty = AvaloniaProperty.Register<TimelineView, double>(nameof(PixelsPerQuarter), 40);
    public static readonly StyledProperty<double> LaneHeightProperty = AvaloniaProperty.Register<TimelineView, double>(nameof(LaneHeight), 50);
    public static readonly StyledProperty<double> AutomationLaneHeightProperty = AvaloniaProperty.Register<TimelineView, double>(nameof(AutomationLaneHeight), 36);
    public static readonly StyledProperty<IReadOnlySet<TrackId>> ExpandedTracksProperty = AvaloniaProperty.Register<TimelineView, IReadOnlySet<TrackId>>(nameof(ExpandedTracks), new HashSet<TrackId>());
    public static readonly StyledProperty<IReadOnlySet<TrackId>> HiddenTracksProperty = AvaloniaProperty.Register<TimelineView, IReadOnlySet<TrackId>>(nameof(HiddenTracks), new HashSet<TrackId>());
    public static readonly StyledProperty<IReadOnlySet<int>> SelectedLanesProperty = AvaloniaProperty.Register<TimelineView, IReadOnlySet<int>>(nameof(SelectedLanes), new HashSet<int>());
    public static readonly StyledProperty<double> VisibleLeftProperty = AvaloniaProperty.Register<TimelineView, double>(nameof(VisibleLeft));
    public static readonly StyledProperty<double> VisibleWidthProperty = AvaloniaProperty.Register<TimelineView, double>(nameof(VisibleWidth), double.PositiveInfinity);
    public static readonly StyledProperty<int> RecordingLaneProperty = AvaloniaProperty.Register<TimelineView, int>(nameof(RecordingLane), -1);
    public static readonly StyledProperty<IReadOnlyList<RecordingNote>> RecordingPreviewProperty = AvaloniaProperty.Register<TimelineView, IReadOnlyList<RecordingNote>>(nameof(RecordingPreview), []);
    public static readonly StyledProperty<IReadOnlySet<ClipId>> SelectedClipsProperty = AvaloniaProperty.Register<TimelineView, IReadOnlySet<ClipId>>(nameof(SelectedClips), new HashSet<ClipId>());

    private const double EdgeGrip = 5;
    private const double PointGrip = 5;
    private const double CurveInset = 4;
    private const double ClipHeaderHeight = 13;

    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor MoveCursor = new(StandardCursorType.DragMove);
    private static readonly Cursor CopyCursor = new(StandardCursorType.DragCopy);
    private static readonly IBrush LaneBrush = new SolidColorBrush(Palette.Lane);
    private static readonly IBrush LaneAltBrush = new SolidColorBrush(Palette.LaneAlt);
    private static readonly IBrush LaneSelected = new SolidColorBrush(Palette.LaneSelected);
    private static readonly IBrush AutomationBrush = new SolidColorBrush(Palette.AutomationLane);
    private static readonly IPen LaneDivider = new Pen(new SolidColorBrush(Palette.LaneDivider), 1);
    private static readonly IPen CenterLine = new Pen(new SolidColorBrush(Palette.GridBeat), 1, new DashStyle([3, 3], 0));
    private static readonly IBrush RecordFill = new SolidColorBrush(Palette.Record, 0.28);
    private static readonly IPen RecordEdge = new Pen(new SolidColorBrush(Palette.Record, 0.9), 1);
    private static readonly IBrush RecordNote = new SolidColorBrush(Palette.Lighten(Palette.Record, 0.35));
    private static readonly IBrush RegionText = new SolidColorBrush(Color.Parse("#141416"));
    private static readonly Typeface RegionFace = new("Inter", FontStyle.Normal, FontWeight.SemiBold);

    // Clip drag state
    private readonly List<(Track Track, Clip Clip, List<NoteEvent> Notes, (int Start, int End) Slice, (int Low, int High) Range, int Lane)> _moving = [];
    private Grip _drag;
    private Point _dragPress;
    private int _dragLane;
    private Clip? _dragClip;
    private long _dragDelta;
    private int _dragLanes;
    private bool _dragCopy;
    private bool _dragging;

    // Automation point drag state: the lane's points as they will be, and which one moves. The points
    // as they were at the press tell whether the lane changed underneath the drag (for example, undo).
    private readonly Dictionary<(int Color, bool Muted), (IPen Line, IBrush Handle)> _curveStyles = [];
    private AutomationLaneId? _pointLane;
    private ImmutableArray<AutomationPoint> _pointsAtPress = [];
    private List<AutomationPoint> _points = [];
    private int _pointIndex;
    private bool _pointAdded;
    private bool _pointMoved;
    private (AutomationLaneId Lane, Tick Position)? _justAdded;

    // The top of each track's row, laid out on measure and render.
    private double[] _tops = [];

    // Each track's notes and pitch range, kept with the (immutable) track so drags do not rebuild them.
    private readonly ConditionalWeakTable<Track, TrackNotes> _notes = [];

    private sealed record TrackNotes(List<NoteEvent> Notes, (int Low, int High) Range);

    private enum Grip
    {
        None,
        Body,
        StartEdge,
        EndEdge,
    }

    /// <summary>What is under the pointer: a track, and the automation lane in its row (or -1 for its clips).</summary>
    private readonly record struct Row(int Lane, int Automation);

    static TimelineView()
    {
        AffectsRender<TimelineView>(ProjectProperty, PixelsPerQuarterProperty, LaneHeightProperty, AutomationLaneHeightProperty, ExpandedTracksProperty, HiddenTracksProperty, SelectedLanesProperty, VisibleLeftProperty, VisibleWidthProperty, RecordingLaneProperty, RecordingPreviewProperty, SelectedClipsProperty);
        AffectsMeasure<TimelineView>(ProjectProperty, PixelsPerQuarterProperty, LaneHeightProperty, AutomationLaneHeightProperty, ExpandedTracksProperty, HiddenTracksProperty);
        FocusableProperty.OverrideDefaultValue<TimelineView>(true);
    }

    public Project? Project
    {
        get => GetValue(ProjectProperty);
        set => SetValue(ProjectProperty, value);
    }

    public double PixelsPerQuarter
    {
        get => GetValue(PixelsPerQuarterProperty);
        set => SetValue(PixelsPerQuarterProperty, value);
    }

    /// <summary>The height of a track's clip row.</summary>
    public double LaneHeight
    {
        get => GetValue(LaneHeightProperty);
        set => SetValue(LaneHeightProperty, value);
    }

    public double AutomationLaneHeight
    {
        get => GetValue(AutomationLaneHeightProperty);
        set => SetValue(AutomationLaneHeightProperty, value);
    }

    /// <summary>The tracks whose automation lanes are shown below their clips.</summary>
    public IReadOnlySet<TrackId> ExpandedTracks
    {
        get => GetValue(ExpandedTracksProperty);
        set => SetValue(ExpandedTracksProperty, value);
    }

    /// <summary>The tracks folded away under a group: their rows take no space and are not drawn.</summary>
    public IReadOnlySet<TrackId> HiddenTracks
    {
        get => GetValue(HiddenTracksProperty);
        set => SetValue(HiddenTracksProperty, value);
    }

    /// <summary>Indices of the lanes to highlight as selected.</summary>
    public IReadOnlySet<int> SelectedLanes
    {
        get => GetValue(SelectedLanesProperty);
        set => SetValue(SelectedLanesProperty, value);
    }

    /// <summary>The horizontal scroll offset, used to draw only what is visible.</summary>
    public double VisibleLeft
    {
        get => GetValue(VisibleLeftProperty);
        set => SetValue(VisibleLeftProperty, value);
    }

    public double VisibleWidth
    {
        get => GetValue(VisibleWidthProperty);
        set => SetValue(VisibleWidthProperty, value);
    }

    /// <summary>The lane being recorded into, or -1.</summary>
    public int RecordingLane
    {
        get => GetValue(RecordingLaneProperty);
        set => SetValue(RecordingLaneProperty, value);
    }

    /// <summary>Notes of the take being recorded, drawn live in red.</summary>
    public IReadOnlyList<RecordingNote> RecordingPreview
    {
        get => GetValue(RecordingPreviewProperty);
        set => SetValue(RecordingPreviewProperty, value);
    }

    /// <summary>The clips to highlight as selected.</summary>
    public IReadOnlySet<ClipId> SelectedClips
    {
        get => GetValue(SelectedClipsProperty);
        set => SetValue(SelectedClipsProperty, value);
    }

    /// <summary>Raised with the lane index and modifiers when empty space in a lane is clicked.</summary>
    public event EventHandler<(int Lane, KeyModifiers Modifiers)>? LaneClicked;

    /// <summary>Raised with the lane, clip, and modifiers when a clip is pressed, before any drag.</summary>
    public event EventHandler<(int Lane, ClipId Clip, KeyModifiers Modifiers)>? ClipPressed;

    public event EventHandler<(int Lane, ClipId Clip)>? ClipDoubleClicked;

    /// <summary>Raised when a clip's name strip is double-clicked, to rename it.</summary>
    public event EventHandler<ClipId>? ClipNameDoubleClicked;

    /// <summary>Raised with the lane and tick when empty space in a lane is double-clicked.</summary>
    public event EventHandler<(int Lane, long Tick)>? EmptyDoubleClicked;

    /// <summary>Raised when the selected clips are dragged: the snapped shift in ticks, in lanes, and whether to copy.</summary>
    public event EventHandler<(long DeltaTicks, int LaneDelta, bool Copy)>? ClipsDragged;

    /// <summary>Raised when a clip's edge is dragged, with its new bounds in ticks.</summary>
    public event EventHandler<(ClipId Clip, long Start, long End)>? ClipResized;

    /// <summary>Raised when an automation lane's points are edited: the lane's track, the lane, an undo label, and the new points.</summary>
    public event EventHandler<(int Lane, AutomationLaneId Automation, string Label, IReadOnlyList<AutomationPoint> Points)>? AutomationEdited;

    /// <summary>
    /// The visible part of a clip's name strip, in this control's coordinates, or null if the clip is not
    /// on any track or not in view.
    /// </summary>
    public Rect? ClipHeaderBounds(ClipId clip)
    {
        if (Project is not { } project)
        {
            return null;
        }

        LayoutRows(project.Sequence);
        for (var lane = 0; lane < project.Sequence.Tracks.Length; lane++)
        {
            if (project.Sequence.Tracks[lane].FindClip(clip) is { } found)
            {
                if (HiddenTracks.Contains(project.Sequence.Tracks[lane].Id))
                {
                    return null;
                }

                var header = HeaderOf(ClipBody(found, _tops[lane], 0));
                var visible = new Rect(VisibleLeft, header.Y, double.IsFinite(VisibleWidth) ? VisibleWidth : Bounds.Width, header.Height);
                var shown = header.Intersect(visible);
                return shown.Width > 0 ? shown : null;
            }
        }

        return null;
    }

    /// <summary>A clip's body as drawn in a row starting at <paramref name="rowTop"/>, shifted by <paramref name="offset"/> ticks.</summary>
    private Rect ClipBody(Clip clip, double rowTop, long offset)
    {
        var shift = TickToX(offset);
        var x0 = Math.Round(TickToX(clip.Start.Value) + shift) + 1;
        var x1 = Math.Round(TickToX(clip.End.Value) + shift);
        return new Rect(x0, rowTop + 2, Math.Max(3, x1 - x0 - 1), LaneHeight - 5);
    }

    private static Rect HeaderOf(Rect body) => new(body.X, body.Y, body.Width, ClipHeaderHeight + 1);

    public double TickToX(long tick) => Project is { } p ? TimeGrid.TickToX(tick, p.Sequence, PixelsPerQuarter) : 0;

    public long XToTick(double x) => Project is { } p ? TimeGrid.XToTick(x, p.Sequence, PixelsPerQuarter) : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Project is not { } project)
        {
            return default;
        }

        var sequence = project.Sequence;
        var endTick = sequence.EndPosition.Value + (32L * sequence.Ppqn.TicksPerQuarterNote);
        var width = Math.Max(TickToX(endTick), double.IsFinite(availableSize.Width) ? availableSize.Width : 0);
        LayoutRows(sequence);
        var rows = sequence.Tracks.IsEmpty ? 0 : _tops[^1] + RowHeight(sequence.Tracks[^1]);
        var height = Math.Max(rows, double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        if (Project is not { } project)
        {
            return;
        }

        var sequence = project.Sequence;
        var left = Math.Max(0, VisibleLeft - 50);
        var right = Math.Min(Bounds.Width, VisibleLeft + (double.IsFinite(VisibleWidth) ? VisibleWidth : Bounds.Width) + 50);
        var lanes = sequence.Tracks.Length;
        LayoutRows(sequence);
        var tops = _tops;

        context.FillRectangle(LaneBrush, new Rect(left, 0, right - left, Bounds.Height));
        for (var lane = 0; lane < lanes; lane++)
        {
            if (HiddenTracks.Contains(sequence.Tracks[lane].Id))
            {
                continue;
            }

            var brush = SelectedLanes.Contains(lane) ? LaneSelected : lane % 2 == 1 ? LaneAltBrush : null;
            if (brush is not null)
            {
                context.FillRectangle(brush, new Rect(left, tops[lane], right - left, LaneHeight));
            }

            var shown = ShownLanes(sequence.Tracks[lane]);
            if (shown > 0)
            {
                context.FillRectangle(AutomationBrush, new Rect(left, tops[lane] + LaneHeight, right - left, shown * AutomationLaneHeight));
            }
        }

        TimeGrid.DrawLines(context, sequence, PixelsPerQuarter, 0, new Rect(left, 0, right - left, Bounds.Height));
        TimeGrid.ShadeCycle(context, sequence, project.Loop, PixelsPerQuarter, 0, new Rect(left, 0, right - left, Bounds.Height));

        _moving.Clear();
        for (var lane = 0; lane < lanes; lane++)
        {
            var track = sequence.Tracks[lane];
            if (HiddenTracks.Contains(track.Id))
            {
                continue;
            }

            var top = tops[lane];
            HLine(context, Math.Round(top + LaneHeight) - 0.5, left, right);
            DrawClips(context, track, lane, top, left, right);
            for (var a = 0; a < ShownLanes(track); a++)
            {
                var laneTop = top + LaneHeight + (a * AutomationLaneHeight);
                HLine(context, Math.Round(laneTop + AutomationLaneHeight) - 0.5, left, right);
                DrawAutomation(context, track, track.Automation[a], lane, new Rect(left, laneTop, right - left, AutomationLaneHeight - 1));
            }
        }

        // Dragged clips go on top of every lane, where they would land.
        foreach (var (track, clip, notes, slice, range, lane) in _moving)
        {
            DrawClip(context, track, clip, notes, slice, range, lane, tops[Math.Clamp(lane + _dragLanes, 0, lanes - 1)], left, right, _dragDelta);
        }

        if (RecordingLane >= 0 && RecordingLane < lanes && RecordingPreview.Count > 0 && !HiddenTracks.Contains(sequence.Tracks[RecordingLane].Id))
        {
            DrawTake(context, tops[RecordingLane]);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (Project is not { } project || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var row = RowAt(project.Sequence, point.Position.Y);
        if (row.Lane < 0)
        {
            return;
        }

        Focus();
        e.Handled = true;
        var justAdded = _justAdded;
        _justAdded = null;
        if (row.Automation >= 0)
        {
            PressAutomation(project.Sequence, row, point.Position, e, justAdded);
            return;
        }

        var (clip, grip) = HitClip(project.Sequence.Tracks[row.Lane], point.Position.X);
        var lane = row.Lane;
        if (clip is null)
        {
            if (e.ClickCount >= 2)
            {
                EmptyDoubleClicked?.Invoke(this, (lane, XToTick(point.Position.X)));
            }
            else
            {
                LaneClicked?.Invoke(this, (lane, e.KeyModifiers));
            }

            return;
        }

        if (e.ClickCount >= 2)
        {
            if (HeaderOf(ClipBody(clip, _tops[lane], 0)).Contains(point.Position))
            {
                ClipNameDoubleClicked?.Invoke(this, clip.Id);
            }
            else
            {
                ClipDoubleClicked?.Invoke(this, (lane, clip.Id));
            }

            return;
        }

        ClipPressed?.Invoke(this, (lane, clip.Id, e.KeyModifiers));
        if (grip == Grip.Body && !SelectedClips.Contains(clip.Id))
        {
            // The press took the clip out of the selection; there is nothing to drag.
            return;
        }

        (_drag, _dragPress, _dragLane, _dragClip) = (grip, point.Position, lane, clip);
        (_dragDelta, _dragLanes, _dragCopy, _dragging) = (0, 0, e.KeyModifiers.HasFlag(KeyModifiers.Alt), false);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Project is not { } project)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (_pointLane is not null && ReferenceEquals(e.Pointer.Captured, this))
        {
            MovePoint(project.Sequence, position, e.KeyModifiers);
            return;
        }

        if (_drag == Grip.None || !ReferenceEquals(e.Pointer.Captured, this))
        {
            // Show where an edge can be grabbed.
            var row = RowAt(project.Sequence, position.Y);
            var edge = row.Lane >= 0 && row.Automation < 0 && HitClip(project.Sequence.Tracks[row.Lane], position.X).Grip is Grip.StartEdge or Grip.EndEdge;
            SetCursor(edge ? ResizeCursor : Cursor.Default);
            return;
        }

        if (_dragClip is not { } clip || _dragLane >= project.Sequence.Tracks.Length || project.Sequence.Tracks[_dragLane].FindClip(clip.Id) is null)
        {
            // The clip went away mid-drag (for example, undo).
            EndDrag();
            return;
        }

        _dragging |= Math.Abs(position.X - _dragPress.X) > 4 || Math.Abs(position.Y - _dragPress.Y) > LaneHeight / 2;
        if (!_dragging)
        {
            return;
        }

        var sequence = project.Sequence;
        var raw = (long)Math.Round((position.X - _dragPress.X) * sequence.Ppqn.TicksPerQuarterNote / PixelsPerQuarter);
        var track = sequence.Tracks[_dragLane];
        switch (_drag)
        {
            case Grip.Body:
                // The selection moves as one: no clip before tick 0, none past the first or last track.
                var (earliest, firstLane, lastLane) = SelectionExtent(sequence);
                var over = RowAt(sequence, position.Y).Lane is var hovered and >= 0 ? hovered : position.Y < 0 ? 0 : sequence.Tracks.Length - 1;
                _dragDelta = Math.Max(SnapClip(sequence, clip.Start.Value + raw) - clip.Start.Value, -earliest);
                _dragLanes = Math.Clamp(over - _dragLane, -firstLane, sequence.Tracks.Length - 1 - lastLane);
                SetCursor(_dragCopy ? CopyCursor : MoveCursor);
                break;
            case Grip.StartEdge:
                // Edges stop at neighbouring clips, as the resize does.
                _dragDelta = Math.Clamp(SnapClip(sequence, clip.Start.Value + raw), track.Neighbours(clip.Id).Before.Value, clip.End.Value - 1) - clip.Start.Value;
                break;
            case Grip.EndEdge:
                var after = track.Neighbours(clip.Id).After?.Value ?? long.MaxValue;
                _dragDelta = Math.Clamp(SnapClip(sequence, clip.End.Value + raw), clip.Start.Value + 1, after) - clip.End.Value;
                break;
        }

        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pointLane is { } automation)
        {
            var (lane, points, label) = (_dragLane, _points, _pointAdded ? "Add Automation Point" : "Move Automation Point");
            var changed = _pointAdded || _pointMoved;
            var current = Project is { } project && lane < project.Sequence.Tracks.Length && project.Sequence.Tracks[lane].FindLane(automation) is { } found && found.Points == _pointsAtPress;
            var added = _pointAdded ? points[_pointIndex].Position : (Tick?)null;
            e.Pointer.Capture(null);
            EndPointDrag();
            if (changed && current)
            {
                AutomationEdited?.Invoke(this, (lane, automation, label, points));
                _justAdded = added is { } at ? (automation, at) : null;
            }

            return;
        }

        var (grip, clip, delta, lanes, copy, dragged) = (_drag, _dragClip, _dragDelta, _dragLanes, _dragCopy, _dragging);
        e.Pointer.Capture(null);
        EndDrag();
        if (!dragged || clip is null)
        {
            return;
        }

        switch (grip)
        {
            case Grip.Body when delta != 0 || lanes != 0:
                ClipsDragged?.Invoke(this, (delta, lanes, copy));
                break;
            case Grip.StartEdge when delta != 0:
                ClipResized?.Invoke(this, (clip.Id, clip.Start.Value + delta, clip.End.Value));
                break;
            case Grip.EndEdge when delta != 0:
                ClipResized?.Invoke(this, (clip.Id, clip.Start.Value, clip.End.Value + delta));
                break;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        // The gesture was interrupted (another window, a popup): drop it rather than commit it later.
        if (_pointLane is not null)
        {
            EndPointDrag();
        }

        if (_drag != Grip.None)
        {
            EndDrag();
        }
    }

    private void EndDrag()
    {
        (_drag, _dragClip, _dragDelta, _dragLanes, _dragging) = (Grip.None, null, 0, 0, false);
        SetCursor(Cursor.Default);
        InvalidateVisual();
    }

    private void SetCursor(Cursor cursor)
    {
        if (!ReferenceEquals(Cursor, cursor))
        {
            Cursor = cursor;
        }
    }

    private int ShownLanes(Track track) => ExpandedTracks.Contains(track.Id) ? track.Automation.Length : 0;

    private double RowHeight(Track track) => HiddenTracks.Contains(track.Id) ? 0 : LaneHeight + (ShownLanes(track) * AutomationLaneHeight);

    private void LayoutRows(Sequence sequence)
    {
        if (_tops.Length != sequence.Tracks.Length)
        {
            _tops = new double[sequence.Tracks.Length];
        }

        for (var i = 1; i < _tops.Length; i++)
        {
            _tops[i] = _tops[i - 1] + RowHeight(sequence.Tracks[i - 1]);
        }
    }

    private Row RowAt(Sequence sequence, double y)
    {
        LayoutRows(sequence);
        for (var lane = 0; lane < sequence.Tracks.Length; lane++)
        {
            var top = _tops[lane];
            if (y >= top && y < top + RowHeight(sequence.Tracks[lane]))
            {
                return new Row(lane, y < top + LaneHeight ? -1 : (int)((y - top - LaneHeight) / AutomationLaneHeight));
            }
        }

        return new Row(-1, -1);
    }

    private static void HLine(DrawingContext context, double y, double left, double right) =>
        context.DrawLine(LaneDivider, new Point(left, y), new Point(right, y));

    /// <summary>The earliest start among the selected clips, and the first and last lanes holding any.</summary>
    private (long Earliest, int FirstLane, int LastLane) SelectionExtent(Sequence sequence)
    {
        var (earliest, first, last) = (long.MaxValue, int.MaxValue, -1);
        for (var lane = 0; lane < sequence.Tracks.Length; lane++)
        {
            foreach (var clip in sequence.Tracks[lane].Clips)
            {
                if (SelectedClips.Contains(clip.Id))
                {
                    (earliest, first, last) = (Math.Min(earliest, clip.Start.Value), Math.Min(first, lane), lane);
                }
            }
        }

        return last < 0 ? (0, 0, sequence.Tracks.Length - 1) : (earliest, first, last);
    }

    /// <summary>The clip at <paramref name="x"/> in a track's clip row, and which part of it: its body or an edge.</summary>
    /// <remarks>
    /// Edges can be grabbed a few pixels either side. Where two clips touch, the side of the line the
    /// pointer is on decides which clip's edge it is.
    /// </remarks>
    private (Clip? Clip, Grip Grip) HitClip(Track track, double x)
    {
        var tick = new Tick(Math.Max(0, XToTick(x)));
        if (track.ClipAt(tick) is { } clip)
        {
            var (x0, x1) = (TickToX(clip.Start.Value), TickToX(clip.End.Value));

            // Edges of clips too narrow to hold both are not offered, so the clip can still be dragged.
            var roomy = x1 - x0 > 4 * EdgeGrip;
            var grip = !roomy ? Grip.Body : x - x0 <= EdgeGrip ? Grip.StartEdge : x1 - x <= EdgeGrip ? Grip.EndEdge : Grip.Body;
            return (clip, grip);
        }

        var (from, until) = track.GapAt(tick);
        if (from > Tick.Zero && x - TickToX(from.Value) <= EdgeGrip && track.ClipAt(new Tick(from.Value - 1)) is { } before)
        {
            return (before, Grip.EndEdge);
        }

        if (until is { } next && TickToX(next.Value) - x <= EdgeGrip && track.ClipAt(next) is { } after)
        {
            return (after, Grip.StartEdge);
        }

        return (null, Grip.None);
    }

    /// <summary>Clips snap to beats when beats are wide enough to aim at, otherwise to bars.</summary>
    private long SnapClip(Sequence sequence, long tick)
    {
        var meter = sequence.MeterMap;
        var bar = meter.BarStart(new Tick(Math.Max(0, tick)));
        var beat = meter.SignatureAt(bar).TryGetTicksPerBeat(sequence.Ppqn, out var span) ? span.Value : sequence.Ppqn.TicksPerQuarterNote;
        if (TickToX(beat) >= 24)
        {
            return bar.Value + ((long)Math.Round((tick - bar.Value) / (double)beat) * beat);
        }

        var next = meter.NextBarStart(bar);
        return tick - bar.Value < next.Value - tick ? bar.Value : next.Value;
    }

    private void PressAutomation(Sequence sequence, Row row, Point position, PointerPressedEventArgs e, (AutomationLaneId Lane, Tick Position)? justAdded)
    {
        var track = sequence.Tracks[row.Lane];
        if (row.Automation >= track.Automation.Length)
        {
            return;
        }

        var lane = track.Automation[row.Automation];
        var area = AutomationArea(_tops[row.Lane] + LaneHeight + (row.Automation * AutomationLaneHeight));
        var hit = PointAt(lane, position, area);
        if (e.ClickCount >= 2 && (hit < 0 || justAdded == (lane.Id, lane.Points[hit].Position)))
        {
            // The second click of a double-click that started by adding a point, or that missed: nothing more to do.
            return;
        }

        var points = lane.Points.ToList();
        if (hit >= 0 && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            points.RemoveAt(hit);
            AutomationEdited?.Invoke(this, (row.Lane, lane.Id, "Delete Automation Point", points));
            return;
        }

        if (hit >= 0 && e.ClickCount >= 2)
        {
            var point = points[hit];
            points[hit] = point with { Curve = point.Curve == AutomationCurve.Hold ? AutomationCurve.Linear : AutomationCurve.Hold };
            AutomationEdited?.Invoke(this, (row.Lane, lane.Id, "Change Automation Curve", points));
            return;
        }

        _pointAdded = hit < 0;
        if (_pointAdded)
        {
            var tick = PointTick(sequence, position.X, e.KeyModifiers);
            if (points.Any(p => p.Position.Value == tick))
            {
                return;
            }

            points.Add(new AutomationPoint(new Tick(tick), YToValue(position.Y, area)));
            points.Sort((a, b) => a.Position.CompareTo(b.Position));
            hit = points.FindIndex(p => p.Position.Value == tick);
        }

        (_pointLane, _pointsAtPress, _points, _pointIndex, _pointMoved, _dragLane) = (lane.Id, lane.Points, points, hit, false, row.Lane);
        _dragPress = position;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    private void MovePoint(Sequence sequence, Point position, KeyModifiers modifiers)
    {
        if (_dragLane >= sequence.Tracks.Length || sequence.Tracks[_dragLane].FindLane(_pointLane!.Value) is not { } lane)
        {
            // The lane went away mid-drag (for example, undo).
            EndPointDrag();
            return;
        }

        if (!_pointMoved && Math.Abs(position.X - _dragPress.X) < 3 && Math.Abs(position.Y - _dragPress.Y) < 3)
        {
            return;
        }

        // The point stays between its neighbours, so points keep their order and one tick each.
        var index = sequence.Tracks[_dragLane].Automation.IndexOf(lane);
        LayoutRows(sequence);
        var area = AutomationArea(_tops[_dragLane] + LaneHeight + (index * AutomationLaneHeight));
        var low = _pointIndex > 0 ? _points[_pointIndex - 1].Position.Value + 1 : 0;
        var high = _pointIndex + 1 < _points.Count ? _points[_pointIndex + 1].Position.Value - 1 : long.MaxValue;
        var tick = Math.Clamp(PointTick(sequence, position.X, modifiers), low, Math.Max(low, high));
        _points[_pointIndex] = _points[_pointIndex] with { Position = new Tick(tick), Value = YToValue(position.Y, area) };
        _pointMoved = true;
        InvalidateVisual();
    }

    private void EndPointDrag()
    {
        (_pointLane, _pointsAtPress, _points, _pointAdded, _pointMoved) = (null, [], [], false, false);
        InvalidateVisual();
    }

    /// <summary>The index of the point under <paramref name="position"/>, or -1. Only points near it in time are looked at.</summary>
    private int PointAt(AutomationLane lane, Point position, Rect area)
    {
        var points = lane.Points;
        for (var i = FirstAtOrAfter(points, XToTick(Math.Max(0, position.X - PointGrip))); i < points.Length && TickToX(points[i].Position.Value) <= position.X + PointGrip; i++)
        {
            if (Math.Abs(ValueToY(points[i].Value, area) - position.Y) <= PointGrip)
            {
                return i;
            }
        }

        return -1;
    }

    private long PointTick(Sequence sequence, double x, KeyModifiers modifiers)
    {
        var tick = Math.Max(0, XToTick(x));
        return modifiers.HasFlag(KeyModifiers.Shift) ? tick : Math.Max(0, SnapClip(sequence, tick));
    }

    private Rect AutomationArea(double laneTop) =>
        new(0, laneTop + CurveInset, Bounds.Width, AutomationLaneHeight - 1 - (2 * CurveInset));

    private static double ValueToY(ControlValue value, Rect area) => area.Bottom - (area.Height * value.Value / uint.MaxValue);

    private static ControlValue YToValue(double y, Rect area) =>
        new((uint)Math.Round(Math.Clamp((area.Bottom - y) / area.Height, 0, 1) * uint.MaxValue));

    /// <summary>A lane's curve: a step at hold points, a ramp after linear ones, flat before the first and after the last.</summary>
    private void DrawAutomation(DrawingContext context, Track track, AutomationLane lane, int trackLane, Rect row)
    {
        var area = new Rect(row.X, row.Y + CurveInset, row.Width, row.Height - (2 * CurveInset));
        if (!_curveStyles.TryGetValue((trackLane % 8, track.IsMuted), out var style))
        {
            var color = Palette.Track(trackLane);
            style = (new Pen(new SolidColorBrush(Palette.Lighten(color, 0.3), track.IsMuted ? 0.4 : 0.95), 1.5), new SolidColorBrush(Palette.Lighten(color, 0.6)));
            _curveStyles.Add((trackLane % 8, track.IsMuted), style);
        }

        var line = style.Line;
        if (lane.Target.Parameter == AutomationParameter.PitchBend)
        {
            var center = Math.Round(ValueToY(ControlValue.Center, area)) + 0.5;
            context.DrawLine(CenterLine, new Point(row.Left, center), new Point(row.Right, center));
        }

        var points = _pointLane == lane.Id ? (IReadOnlyList<AutomationPoint>)_points : lane.Points;
        if (points.Count == 0)
        {
            return;
        }

        Point At(AutomationPoint p) => new(TickToX(p.Position.Value), ValueToY(p.Value, area));

        // Only the points around the visible part of the lane, plus one either side for the joining lines.
        var (fromTick, toTick) = (XToTick(Math.Max(0, row.Left - PointGrip)), XToTick(row.Right + PointGrip));
        var start = Math.Max(0, FirstAtOrAfter(points, fromTick) - 1);
        var end = Math.Min(points.Count, FirstAtOrAfter(points, toTick + 1) + 1);
        var first = At(points[0]);
        if (start == 0)
        {
            context.DrawLine(line, new Point(row.Left, first.Y), first);
        }

        for (var i = start; i < end; i++)
        {
            var here = At(points[i]);
            if (i + 1 == points.Count)
            {
                context.DrawLine(line, here, new Point(row.Right, here.Y));
                break;
            }

            var next = At(points[i + 1]);
            if (next.X < row.Left || here.X > row.Right)
            {
                continue;
            }

            if (points[i].Curve == AutomationCurve.Linear)
            {
                context.DrawLine(line, here, next);
            }
            else
            {
                context.DrawLine(line, here, new Point(next.X, here.Y));
                context.DrawLine(line, new Point(next.X, here.Y), next);
            }
        }

        var handle = style.Handle;
        for (var i = start; i < end; i++)
        {
            var p = At(points[i]);
            if (p.X >= row.Left - PointGrip && p.X <= row.Right + PointGrip)
            {
                var dragged = _pointLane == lane.Id && i == _pointIndex;
                var size = dragged ? 7 : 5;
                context.FillRectangle(dragged ? Brushes.White : handle, new Rect(p.X - (size / 2.0), p.Y - (size / 2.0), size, size));
            }
        }
    }

    private static int FirstAtOrAfter(IReadOnlyList<AutomationPoint> points, long tick)
    {
        int low = 0, high = points.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (points[mid].Position.Value < tick)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private bool IsMoving => _dragging && _drag == Grip.Body && (_dragDelta != 0 || _dragLanes != 0);

    private void DrawClips(DrawingContext context, Track track, int lane, double top, double left, double right)
    {
        // One pitch range for the whole track, so a note sits at the same height in every clip.
        var (notes, range) = _notes.GetValue(track, t =>
        {
            var all = t.ArrangedEvents.OfType<NoteEvent>().ToList();
            var low = all.Count == 0 ? 0 : all.Min(n => n.Note.Value);
            return new TrackNotes(all, (low, Math.Max(low + 12, all.Count == 0 ? 0 : all.Max(n => n.Note.Value))));
        });
        var first = 0;
        foreach (var clip in track.Clips)
        {
            // Notes are in timeline order and clips do not overlap, so each clip's notes follow the last clip's.
            while (first < notes.Count && notes[first].Position < clip.Start)
            {
                first++;
            }

            var end = first;
            while (end < notes.Count && notes[end].Position < clip.End)
            {
                end++;
            }

            var slice = (first, end);
            first = end;
            if (_dragging && _drag is Grip.StartEdge or Grip.EndEdge && clip.Id == _dragClip?.Id)
            {
                // The clip as it will be: trimmed content hidden, or hidden content revealed.
                var (start, until) = _drag == Grip.StartEdge ? (clip.Start.Value + _dragDelta, clip.End.Value) : (clip.Start.Value, clip.End.Value + _dragDelta);
                var resized = clip.WithBounds(new Tick(start), new Tick(until));
                var shown = resized is NoteClip noteClip ? noteClip.Arrange().OfType<NoteEvent>().ToList() : [];
                DrawClip(context, track, resized, shown, (0, shown.Count), range, lane, top, left, right, 0);
                continue;
            }

            if (IsMoving && SelectedClips.Contains(clip.Id))
            {
                // The original stays in place (dimmed unless copying) under the clip being dragged.
                _moving.Add((track, clip, notes, slice, range, lane));
                using (context.PushOpacity(_dragCopy ? 1 : 0.35))
                {
                    DrawClip(context, track, clip, notes, slice, range, lane, top, left, right, 0);
                }

                continue;
            }

            DrawClip(context, track, clip, notes, slice, range, lane, top, left, right, 0);
        }
    }

    // notes[slice] are this clip's notes, in timeline order. The clip has its track's colour
    // (colorLane) wherever it is drawn (rowTop).
    private void DrawClip(DrawingContext context, Track track, Clip clip, List<NoteEvent> notes, (int Start, int End) slice, (int Low, int High) range, int colorLane, double rowTop, double left, double right, long offset)
    {
        var shift = TickToX(offset);
        var body = ClipBody(clip, rowTop, offset);
        if (body.Right + 1 < left || body.X > right)
        {
            return;
        }

        var color = Palette.Track(colorLane);
        var selected = SelectedClips.Contains(clip.Id);
        var muted = track.IsMuted || (track.Group is not null && Project is { } project && GroupTracks.IsMutedByGroup(project.Sequence, track));
        var fill = Palette.Mix(Palette.Lane, color, muted ? 0.16 : selected ? 0.5 : 0.32);
        var header = Palette.Mix(Palette.Lane, color, muted ? 0.32 : selected ? 0.95 : 0.68);
        var edge = selected ? new Pen(Brushes.White, 1.5) : new Pen(new SolidColorBrush(Palette.Darken(color, 0.25)), 1);

        context.DrawRectangle(new SolidColorBrush(fill), edge, body, 2, 2);
        var strip = HeaderOf(body);
        context.DrawRectangle(new SolidColorBrush(header), null, new Rect(strip.X + 0.5, strip.Y + 0.5, strip.Width - 1, strip.Height - 1), 1.5, 1.5);

        // The name stays readable at the left edge of the view while the clip scrolls past.
        var labelX = Math.Max(body.X + 4, VisibleLeft + 4);
        if (labelX < body.Right - 24)
        {
            var name = new FormattedText(track.ClipName(clip), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, RegionFace, 10, RegionText)
            {
                MaxTextWidth = Math.Max(1, body.Right - labelX - 4),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            context.DrawText(name, new Point(labelX, body.Y + 0.5));
        }

        if (clip is AudioClip audio)
        {
            // There is no audio engine yet, so no waveform: say what the clip holds, plainly.
            var source = new FormattedText($"Audio · {Path.GetFileName(audio.Source.Location)}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, RegionFace, 9, RegionText)
            {
                MaxTextWidth = Math.Max(1, body.Right - labelX - 4),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            var middle = body.Y + 16 + ((body.Height - 19) / 2);
            context.DrawLine(new Pen(new SolidColorBrush(Palette.Lighten(color, 0.3), 0.6), 1), new Point(body.X + 2, middle), new Point(body.Right - 2, middle));
            if (labelX < body.Right - 24)
            {
                context.DrawText(source, new Point(labelX, body.Y + 15));
            }

            return;
        }

        if (slice.Start == slice.End)
        {
            return;
        }

        var (low, high) = range;
        var noteTop = body.Y + 16;
        var noteArea = body.Bottom - noteTop - 3;
        var span = high - low + 1;
        var noteHeight = Math.Clamp(noteArea / span, 1.5, 4);
        var noteColor = muted ? Palette.Mix(color, Palette.Lane, 0.4) : Palette.Lighten(color, selected ? 0.6 : 0.45);
        var firstTick = Math.Max(clip.Start.Value, XToTick(Math.Max(0, left - shift)));
        var lastVisible = Math.Min(clip.End.Value - 1, XToTick(Math.Max(0, right - shift)));
        using (context.PushClip(body))
        {
            for (var i = slice.Start; i < slice.End; i++)
            {
                var note = notes[i];
                if (note.Position.Value > lastVisible)
                {
                    break;
                }

                if (note.EndPosition.Value < firstTick)
                {
                    continue;
                }

                var x = TickToX(note.Position.Value) + shift;
                var w = Math.Max(1.5, TickToX(note.EndPosition.Value) - x - 0.5);
                var y = noteTop + ((high - note.Note.Value) * (noteArea - noteHeight) / Math.Max(1, span - 1));
                var opacity = 0.55 + (0.45 * note.Velocity.Value / 127.0);
                context.FillRectangle(new SolidColorBrush(noteColor, opacity), new Rect(x, y, w, noteHeight));
            }
        }
    }

    private void DrawTake(DrawingContext context, double rowTop)
    {
        var preview = RecordingPreview;
        var start = preview.Min(n => n.Start);
        var end = preview.Max(n => n.End);
        var top = rowTop + 2;
        var body = new Rect(TickToX(start), top, Math.Max(3, TickToX(end) - TickToX(start)), LaneHeight - 5);
        context.DrawRectangle(RecordFill, RecordEdge, body, 2, 2);

        var low = preview.Min(n => n.Note.Value);
        var high = Math.Max(low + 12, preview.Max(n => n.Note.Value));
        var span = high - low + 1;
        var area = body.Height - 8;
        foreach (var note in preview)
        {
            var x = TickToX(note.Start);
            var y = body.Y + 4 + ((high - note.Note.Value) * (area - 3) / Math.Max(1, span - 1));
            context.FillRectangle(RecordNote, new Rect(x, y, Math.Max(1.5, TickToX(note.End) - x), 3));
        }
    }
}
