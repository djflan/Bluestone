using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Time;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The bar ruler above the arrangement. Click or drag in the lower part to move the playhead; drag
/// in the cycle strip on top to set the loop, or click the loop to turn it off.
/// </summary>
public sealed class RulerView : Control
{
    public static readonly StyledProperty<Project?> ProjectProperty = AvaloniaProperty.Register<RulerView, Project?>(nameof(Project));
    public static readonly StyledProperty<double> PixelsPerQuarterProperty = AvaloniaProperty.Register<RulerView, double>(nameof(PixelsPerQuarter), 40);
    public static readonly StyledProperty<double> ScrollXProperty = AvaloniaProperty.Register<RulerView, double>(nameof(ScrollX));
    public static readonly StyledProperty<long> PlayheadTickProperty = AvaloniaProperty.Register<RulerView, long>(nameof(PlayheadTick));
    public static readonly StyledProperty<bool> IsRecordingProperty = AvaloniaProperty.Register<RulerView, bool>(nameof(IsRecording));

    private long? _cycleAnchor;
    private TickRange? _cycleDraft;
    private bool _dragged;

    static RulerView()
    {
        AffectsRender<RulerView>(ProjectProperty, PixelsPerQuarterProperty, ScrollXProperty, PlayheadTickProperty, IsRecordingProperty);
        ClipToBoundsProperty.OverrideDefaultValue<RulerView>(true);
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

    /// <summary>Raised with the tick under the pointer when the user clicks or drags to seek.</summary>
    public event EventHandler<long>? SeekRequested;

    /// <summary>Raised with a new loop, or null to turn the loop off.</summary>
    public event EventHandler<TickRange?>? LoopRequested;

    public override void Render(DrawingContext context)
    {
        if (Project is not { } project)
        {
            return;
        }

        var loop = _cycleDraft ?? project.Loop;
        TimeGrid.DrawRuler(context, project.Sequence, loop, PixelsPerQuarter, ScrollX, new Rect(Bounds.Size), PlayheadTick, IsRecording);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Project is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        e.Pointer.Capture(this);
        _dragged = false;
        if (point.Y < TimeGrid.CycleStripHeight + 2)
        {
            _cycleAnchor = SnapCycle(TickAt(point.X));
        }
        else
        {
            SeekRequested?.Invoke(this, TickAt(point.X));
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!ReferenceEquals(e.Pointer.Captured, this))
        {
            Cursor = e.GetPosition(this).Y < TimeGrid.CycleStripHeight + 2 ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
            return;
        }

        var tick = TickAt(e.GetPosition(this).X);
        if (_cycleAnchor is { } anchor)
        {
            var end = SnapCycle(tick);
            if (end != anchor)
            {
                _dragged = true;
                _cycleDraft = new TickRange(new Tick(Math.Min(anchor, end)), new Tick(Math.Max(anchor, end)));
                InvalidateVisual();
            }
        }
        else
        {
            SeekRequested?.Invoke(this, tick);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_cycleAnchor is { } anchor && Project is { } project)
        {
            if (_dragged && _cycleDraft is { } draft)
            {
                LoopRequested?.Invoke(this, draft);
            }
            else if (project.Loop is { } loop && anchor >= loop.Start.Value && anchor <= loop.End.Value)
            {
                LoopRequested?.Invoke(this, null);
            }
        }

        _cycleAnchor = null;
        _cycleDraft = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    private long TickAt(double x) => Project is { } p ? TimeGrid.XToTick(x + ScrollX, p.Sequence, PixelsPerQuarter) : 0;

    /// <summary>Loops snap to beats when beats are wide enough to aim at, otherwise to bars.</summary>
    private long SnapCycle(long tick)
    {
        var sequence = Project!.Sequence;
        var meter = sequence.MeterMap;
        var bar = meter.BarStart(new Tick(tick));
        var beat = meter.SignatureAt(bar).TryGetTicksPerBeat(sequence.Ppqn, out var span) ? span.Value : sequence.Ppqn.TicksPerQuarterNote;
        if (TimeGrid.TickToX(beat, sequence, PixelsPerQuarter) >= 24)
        {
            return bar.Value + ((long)Math.Round((tick - bar.Value) / (double)beat) * beat);
        }

        var next = meter.NextBarStart(bar);
        return tick - bar.Value < next.Value - tick ? bar.Value : next.Value;
    }
}
