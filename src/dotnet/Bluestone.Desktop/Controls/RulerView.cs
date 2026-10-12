using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Bluestone.Domain.Projects;
using Bluestone.Presentation.Timeline;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The bar ruler above the arrangement. In the beat ruler, a click moves the playhead, and a drag navigates: up zooms
/// in, down zooms out (around the position first pressed), sideways scrolls (see <see cref="RulerNavigationGesture"/>).
/// The loop strip on top
/// edits the arranger loop: drag empty space to draw a loop (and turn looping on), drag the loop to move it, drag an
/// edge to resize it; ⌘ (Ctrl on Windows and Linux) ignores the grid while dragging, as in the piano roll. A click selects the loop (arrow keys then nudge it by
/// the grid, Shift + arrow by a bar) and never deletes it; a double-click turns looping on or off.
/// </summary>
public sealed class RulerView : Control
{
    public static readonly StyledProperty<Project?> ProjectProperty = AvaloniaProperty.Register<RulerView, Project?>(nameof(Project));
    public static readonly StyledProperty<double> PixelsPerQuarterProperty = AvaloniaProperty.Register<RulerView, double>(nameof(PixelsPerQuarter), 40);
    public static readonly StyledProperty<double> ScrollXProperty = AvaloniaProperty.Register<RulerView, double>(nameof(ScrollX));
    public static readonly StyledProperty<long> PlayheadTickProperty = AvaloniaProperty.Register<RulerView, long>(nameof(PlayheadTick));
    public static readonly StyledProperty<bool> IsRecordingProperty = AvaloniaProperty.Register<RulerView, bool>(nameof(IsRecording));

    private const double EdgeGrip = 5;
    private const double DragThreshold = 3;
    private const string BeatRulerTip = "Click to move the playhead. Drag up or down to zoom, sideways to scroll.";
    private const string LoopStripTip = "Loop strip: drag to draw a loop, drag the loop to move it or its edges to resize; double-click it to turn looping on or off.";
    private static readonly Cursor NavigateCursor = new(StandardCursorType.SizeAll);

    private LoopStripDrag? _loopDrag;
    private Point _pressPoint;
    private TickRange? _loopDraft;
    private bool _dragged;
    private RulerNavigationGesture? _navigation;
    private bool _loopSelected;
    private LoopStripPart _hover = LoopStripPart.Empty;

    static RulerView()
    {
        AffectsRender<RulerView>(ProjectProperty, PixelsPerQuarterProperty, ScrollXProperty, PlayheadTickProperty, IsRecordingProperty);
        ClipToBoundsProperty.OverrideDefaultValue<RulerView>(true);
        FocusableProperty.OverrideDefaultValue<RulerView>(true);
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

    /// <summary>Horizontal scroll offset in pixels, matching the content below.</summary>
    public double ScrollX
    {
        get => GetValue(ScrollXProperty);
        set => SetValue(ScrollXProperty, value);
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

    /// <summary>Raised with the tick under the pointer when the user clicks the beat ruler.</summary>
    public event EventHandler<long>? SeekRequested;

    /// <summary>Raised while the beat ruler is dragged, with the zoom and scroll the view should take.</summary>
    public event EventHandler<TimelineViewport>? NavigateRequested;

    /// <summary>How far the view this ruler belongs to may zoom.</summary>
    public TimelineZoomLimits ZoomLimits { get; set; } = Presentation.MainViewModel.ArrangerZoomLimits;

    /// <summary>Raised once at the end of a loop-strip drag with the new range, whether to turn looping on, and the undo label.</summary>
    public event EventHandler<LoopRangeRequest>? LoopRangeRequested;

    /// <summary>Raised when the loop is double-clicked, to turn looping on or off.</summary>
    public event EventHandler? LoopToggleRequested;

    /// <summary>Raised with a distance in ticks when the selected loop is nudged from the keyboard.</summary>
    public event EventHandler<long>? LoopNudgeRequested;

    /// <summary>Whether the loop is selected, so the arrow keys nudge it while the ruler has focus.</summary>
    public bool IsLoopSelected
    {
        get => _loopSelected;
        set
        {
            if (_loopSelected != value)
            {
                _loopSelected = value;
                InvalidateVisual();
            }
        }
    }

    /// <summary>The part of the loop strip under the pointer, for tests and the cursor.</summary>
    internal LoopStripPart HoverPart => _hover;

    public override void Render(DrawingContext context)
    {
        if (Project is not { } project)
        {
            return;
        }

        var loop = project.Loop;
        var range = _loopDraft ?? loop.Range;
        var enabled = loop.IsEnabled || (_loopDrag?.Enables == true && _loopDraft is not null);
        string? feedback = null;
        if (_dragged && _loopDraft is { } draft)
        {
            var meter = project.Sequence.MeterMap;
            feedback = $"{Presentation.Formatting.Position(meter.ToBarBeatTick(draft.Start))}  ·  {Presentation.Formatting.Length(draft.Length, meter, draft.Start)}";
        }

        var visual = new LoopStripVisual(range, enabled, _loopSelected, _loopDrag?.Part ?? _hover, feedback);
        TimeGrid.DrawRuler(context, project.Sequence, visual, PixelsPerQuarter, ScrollX, new Rect(Bounds.Size), PlayheadTick, IsRecording);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Project is not { } project || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        e.Pointer.Capture(this);
        _pressPoint = point;
        _dragged = false;
        if (InLoopStrip(point))
        {
            Focus();
            var part = PartAt(point.X);
            if (e.ClickCount == 2 && part is LoopStripPart.Body)
            {
                LoopToggleRequested?.Invoke(this, EventArgs.Empty);
                e.Pointer.Capture(null);
            }
            else
            {
                _loopDrag = new LoopStripDrag(part, TickAt(point.X), project.Loop.Range);
            }
        }
        else
        {
            _navigation = new RulerNavigationGesture(point.X, point.Y, new TimelineViewport(PixelsPerQuarter, ScrollX), ZoomLimits);
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (!ReferenceEquals(e.Pointer.Captured, this))
        {
            UpdateHover(point);
            return;
        }

        if (_loopDrag is { } drag)
        {
            if (!_dragged && Math.Abs(point.X - _pressPoint.X) < DragThreshold)
            {
                return;
            }

            _dragged = true;
            var unsnapped = e.KeyModifiers.HasFlag(TimeGrid.CommandModifier);
            _loopDraft = drag.RangeAt(TickAt(point.X), tick => unsnapped ? tick : Snap(tick));
            InvalidateVisual();
        }
        else if (_navigation?.Move(point.X, point.Y) is { } view)
        {
            NavigateRequested?.Invoke(this, view);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_loopDrag is { } drag)
        {
            if (_dragged && _loopDraft is { } draft && !Equals(draft, drag.Original))
            {
                LoopRangeRequested?.Invoke(this, new LoopRangeRequest(draft, drag.Enables, drag.Label));
                IsLoopSelected = true;
            }
            else if (!_dragged)
            {
                // A click selects the loop it lands on, or clears the selection; it never deletes anything.
                IsLoopSelected = drag.Part != LoopStripPart.Empty && drag.Original is not null;
            }
        }

        else if (_navigation is { IsNavigating: false })
        {
            // A click in the beat ruler moves the playhead (on release, so a drag never jumps it first).
            SeekRequested?.Invoke(this, TickAt(_pressPoint.X));
        }

        EndGesture(e.Pointer);
        UpdateHover(e.GetPosition(this));
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _loopDrag = null;
        _loopDraft = null;
        _dragged = false;
        _navigation = null;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover != LoopStripPart.Empty)
        {
            _hover = LoopStripPart.Empty;
            InvalidateVisual();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Project is not { } project || !_loopSelected || project.Loop.Range is not { } range || e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            return;
        }

        var direction = e.Key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            _ => 0,
        };
        if (direction == 0)
        {
            if (e.Key == Key.Escape)
            {
                IsLoopSelected = false;
                e.Handled = true;
            }

            return;
        }

        var sequence = project.Sequence;
        var from = direction < 0 ? Math.Max(0, range.Start.Value - 1) : range.Start.Value;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? TimeGrid.BarTicks(sequence, from) : TimeGrid.AdaptiveStep(from, sequence, PixelsPerQuarter);
        LoopNudgeRequested?.Invoke(this, direction * step);
        e.Handled = true;
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        IsLoopSelected = false;
    }

    private void EndGesture(IPointer pointer)
    {
        _loopDrag = null;
        _loopDraft = null;
        _dragged = false;
        _navigation = null;
        pointer.Capture(null);
        InvalidateVisual();
    }

    private void UpdateHover(Point point)
    {
        var part = InLoopStrip(point) ? PartAt(point.X) : LoopStripPart.Empty;
        ToolTip.SetTip(this, InLoopStrip(point) ? LoopStripTip : BeatRulerTip);
        Cursor = !InLoopStrip(point)
            ? NavigateCursor
            : part switch
            {
                LoopStripPart.StartEdge or LoopStripPart.EndEdge => new Cursor(StandardCursorType.SizeWestEast),
                LoopStripPart.Body => new Cursor(StandardCursorType.Hand),
                _ => new Cursor(StandardCursorType.Cross),
            };
        if (part != _hover)
        {
            _hover = part;
            InvalidateVisual();
        }
    }

    private static bool InLoopStrip(Point point) => point.Y < TimeGrid.CycleStripHeight + 2;

    private LoopStripPart PartAt(double x)
    {
        if (Project?.Loop.Range is not { } range)
        {
            return LoopStripPart.Empty;
        }

        var sequence = Project.Sequence;
        return LoopStripDrag.HitTest(
            x,
            TimeGrid.TickToX(range.Start.Value, sequence, PixelsPerQuarter) - ScrollX,
            TimeGrid.TickToX(range.End.Value, sequence, PixelsPerQuarter) - ScrollX,
            EdgeGrip);
    }

    private long TickAt(double x) => Project is { } p ? TimeGrid.XToTick(x + ScrollX, p.Sequence, PixelsPerQuarter) : 0;

    private long Snap(long tick) => TimeGrid.SnapAdaptive(tick, Project!.Sequence, PixelsPerQuarter);
}

/// <summary>A new arranger-loop range from the loop strip, whether to turn looping on, and the undo label.</summary>
public sealed record LoopRangeRequest(TickRange Range, bool Enable, string Label);
