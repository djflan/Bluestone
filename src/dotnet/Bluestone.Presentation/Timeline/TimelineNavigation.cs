namespace Bluestone.Presentation.Timeline;

/// <summary>
/// A horizontal musical-time view: its zoom (pixels per quarter note) and scroll offset (pixels from the timeline's
/// start), always finite, within the zoom limits, and never scrolled before the start. Time-based editors each keep
/// their own; these functions make them behave alike.
/// </summary>
/// <param name="PixelsPerQuarter">The zoom.</param>
/// <param name="ScrollX">Pixels of the timeline scrolled out of view on the left.</param>
public readonly record struct TimelineViewport(double PixelsPerQuarter, double ScrollX)
{
    /// <summary>The musical position (in quarter notes) at <paramref name="viewportX"/> pixels into the view.</summary>
    public double QuartersAt(double viewportX) => (ScrollX + viewportX) / PixelsPerQuarter;

    /// <summary>
    /// The view zoomed by <paramref name="factor"/> with the musical position under <paramref name="viewportX"/> kept
    /// there (unless that would scroll before the start). A factor that is not a positive finite number changes nothing.
    /// </summary>
    public TimelineViewport ZoomedAround(double viewportX, double factor, TimelineZoomLimits limits)
    {
        if (!double.IsFinite(factor) || factor <= 0 || !double.IsFinite(viewportX))
        {
            return this;
        }

        return Anchored(QuartersAt(viewportX), viewportX, PixelsPerQuarter * factor, limits);
    }

    /// <summary>
    /// A view at zoom <paramref name="pixelsPerQuarter"/> (clamped) that puts <paramref name="quarters"/> at
    /// <paramref name="viewportX"/>, scrolled no earlier than the start.
    /// </summary>
    public static TimelineViewport Anchored(double quarters, double viewportX, double pixelsPerQuarter, TimelineZoomLimits limits)
    {
        var zoom = limits.Clamp(pixelsPerQuarter);
        var scroll = (quarters * zoom) - viewportX;
        return new TimelineViewport(zoom, double.IsFinite(scroll) ? Math.Max(0, scroll) : 0);
    }

    /// <summary>
    /// The view that shows quarter notes [<paramref name="fromQuarters"/>, <paramref name="toQuarters"/>) across
    /// <paramref name="viewportWidth"/> pixels, with a little room on each side. Nothing changes for an empty range or
    /// an unmeasured viewport.
    /// </summary>
    public TimelineViewport Fitted(double fromQuarters, double toQuarters, double viewportWidth, TimelineZoomLimits limits)
    {
        var span = toQuarters - fromQuarters;
        if (!double.IsFinite(span) || span <= 0 || !double.IsFinite(viewportWidth) || viewportWidth <= 0)
        {
            return this;
        }

        const double Margin = 0.04;
        var zoom = limits.Clamp(viewportWidth * (1 - (2 * Margin)) / span);
        return Anchored(fromQuarters, viewportWidth * Margin, zoom, limits);
    }
}

/// <summary>How far a view may zoom, in pixels per quarter note.</summary>
public readonly record struct TimelineZoomLimits(double Minimum, double Maximum)
{
    public double Clamp(double pixelsPerQuarter) =>
        double.IsFinite(pixelsPerQuarter) ? Math.Clamp(pixelsPerQuarter, Minimum, Maximum) : Minimum;
}

/// <summary>
/// A press and drag in a timeline's beat ruler: a click (no movement past the threshold) seeks; a drag zooms
/// (vertically: up zooms in, down zooms out, smoothly and exponentially) around the musical position first pressed,
/// and pans (horizontally: that position follows the pointer). Each axis has its own dead zone, so a vertical zoom does
/// not drift sideways and a pan does not zoom. Pure: the control feeds it positions and applies the views it returns.
/// </summary>
public sealed class RulerNavigationGesture
{
    /// <summary>Pixels the pointer must move before a press stops being a click.</summary>
    public const double Threshold = 4;

    /// <summary>Zoom factor per pixel dragged vertically: 100 pixels up zooms in about 2.7×.</summary>
    public const double ZoomPerPixel = 0.01;

    private readonly double _pressX;
    private readonly double _pressY;
    private readonly double _quarters;
    private readonly double _zoom;
    private readonly TimelineZoomLimits _limits;

    /// <param name="viewportX">Where the press is, in pixels into the view.</param>
    /// <param name="y">Where the press is vertically, in any consistent pixels.</param>
    /// <param name="view">The view when pressed.</param>
    /// <param name="limits">The view's zoom limits.</param>
    public RulerNavigationGesture(double viewportX, double y, TimelineViewport view, TimelineZoomLimits limits)
    {
        _pressX = viewportX;
        _pressY = y;
        _quarters = view.QuartersAt(viewportX);
        _zoom = view.PixelsPerQuarter;
        _limits = limits;
    }

    /// <summary>Whether the pointer has moved far enough to be navigating rather than clicking.</summary>
    public bool IsNavigating { get; private set; }

    /// <summary>The view for the pointer at (<paramref name="viewportX"/>, <paramref name="y"/>), or null while it is still a click.</summary>
    public TimelineViewport? Move(double viewportX, double y)
    {
        var dx = viewportX - _pressX;
        var dy = y - _pressY;
        if (!double.IsFinite(dx) || !double.IsFinite(dy))
        {
            return null;
        }

        IsNavigating |= Math.Abs(dx) >= Threshold || Math.Abs(dy) >= Threshold;
        if (!IsNavigating)
        {
            return null;
        }

        var zoom = _zoom * Math.Exp(-Beyond(dy) * ZoomPerPixel);
        return TimelineViewport.Anchored(_quarters, _pressX + Beyond(dx), zoom, _limits);
    }

    // Movement past the dead zone, with its sign.
    private static double Beyond(double delta) => Math.Abs(delta) <= Threshold ? 0 : delta - (Math.Sign(delta) * Threshold);
}

/// <summary>A request to a timeline's view: zoom by <see cref="Factor"/> around its anchor, or show quarter notes [<see cref="From"/>, <see cref="To"/>).</summary>
/// <remarks><see cref="IsSelection"/> says the range is the selection's, so a view with its own selection can show that instead.</remarks>
public sealed record TimelineZoomRequest(double? Factor, double From = 0, double To = 0, bool IsSelection = false)
{
    public static TimelineZoomRequest By(double factor) => new(factor);

    public static TimelineZoomRequest Show(double fromQuarters, double toQuarters, bool selection = false) => new(null, fromQuarters, toQuarters, selection);
}
