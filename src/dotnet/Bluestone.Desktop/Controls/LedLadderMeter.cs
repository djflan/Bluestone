using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Bluestone.Audio;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The Machined signal meter: a stereo pair of LED columns in a recessed dark well. Each column is 3 DIP cells on a
/// 4 DIP pitch, coloured from the floor up by the <see cref="MeterGradient"/> its <see cref="GradientTarget"/> names; lit cells throw a tight glow (the top cell
/// and every third cell), unlit cells stay dark at <see cref="Palette.UnlitAlpha"/> of their colour, and the held peak
/// is the first unlit cell lightened. Above each column sits a separate clip lamp, always <see cref="Palette.Clip"/>,
/// dim until a peak passes 0 dBFS and lit until reset. Peak + RMS readings follow the console taper
/// (<see cref="ConsoleRail.Position"/>) so the meter, scale and fader agree; VU, K and LUFS are linear over their range.
/// </summary>
public sealed class LedLadderMeter : Control
{
    public static readonly StyledProperty<MixerMeterMode> ModeProperty = AvaloniaProperty.Register<LedLadderMeter, MixerMeterMode>(nameof(Mode));

    /// <summary>The louder channel in the mode's own unit; drawn on both columns when the per-channel values are NaN.</summary>
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<LedLadderMeter, double>(nameof(Value), double.NaN);

    public static readonly StyledProperty<double> LeftValueProperty = AvaloniaProperty.Register<LedLadderMeter, double>(nameof(LeftValue), double.NaN);

    public static readonly StyledProperty<double> RightValueProperty = AvaloniaProperty.Register<LedLadderMeter, double>(nameof(RightValue), double.NaN);

    /// <summary>The higher held peak, in dBFS; used for a column whose own held peak is NaN.</summary>
    public static readonly StyledProperty<double> PeakHoldProperty = AvaloniaProperty.Register<LedLadderMeter, double>(nameof(PeakHold), double.NaN);

    public static readonly StyledProperty<double> LeftPeakHoldProperty = AvaloniaProperty.Register<LedLadderMeter, double>(nameof(LeftPeakHold), double.NaN);

    public static readonly StyledProperty<double> RightPeakHoldProperty = AvaloniaProperty.Register<LedLadderMeter, double>(nameof(RightPeakHold), double.NaN);

    public static readonly StyledProperty<bool> IsReadyProperty = AvaloniaProperty.Register<LedLadderMeter, bool>(nameof(IsReady));

    public static readonly StyledProperty<bool> IsClippedProperty = AvaloniaProperty.Register<LedLadderMeter, bool>(nameof(IsClipped));

    /// <summary>The width of the well; the columns sit at its inner edges.</summary>
    public static readonly StyledProperty<double> MeterWidthProperty = AvaloniaProperty.Register<LedLadderMeter, double>(nameof(MeterWidth), 14);

    /// <summary>The width of each LED column (4 on a track, 8 on Master).</summary>
    public static readonly StyledProperty<double> ColumnWidthProperty = AvaloniaProperty.Register<LedLadderMeter, double>(nameof(ColumnWidth), 4);

    /// <summary>Which gradient the cells are coloured from: the mixer's (the default) or a device chain's.</summary>
    public static readonly StyledProperty<MeterGradientTarget> GradientTargetProperty =
        AvaloniaProperty.Register<LedLadderMeter, MeterGradientTarget>(nameof(GradientTarget), MeterGradientTarget.Mixer);

    private const double Cell = 3;
    private const double Pitch = 4;
    private const double ClipHeight = 4;
    private const double TopCellTier = 1.2;
    private const double CellTier = 0.55;

    private Color[] _colors = [];
    private IBrush[] _lit = [];
    private IBrush[] _unlit = [];
    private IBrush[] _held = [];
    private int _version = -1;
    private MeterGradient? _coloursFrom;
    private MeterGradient? _subscribed;

    // Levels fed through Show (the Device View's meters), as plain fields: a styled property allocates on every change, and
    // these change on every meter tick. While _fed, Render reads these instead of the per-channel properties.
    private bool _fed;
    private double _fedLeft = double.NaN;
    private double _fedRight = double.NaN;
    private double _fedLeftHold = double.NaN;
    private double _fedRightHold = double.NaN;

    static LedLadderMeter()
    {
        AffectsRender<LedLadderMeter>(ModeProperty, ValueProperty, LeftValueProperty, RightValueProperty, PeakHoldProperty, LeftPeakHoldProperty,
            RightPeakHoldProperty, IsReadyProperty, IsClippedProperty, MeterWidthProperty, ColumnWidthProperty);
        AffectsMeasure<LedLadderMeter>(MeterWidthProperty);
    }

    public MixerMeterMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

    public MeterGradientTarget GradientTarget { get => GetValue(GradientTargetProperty); set => SetValue(GradientTargetProperty, value); }

    /// <summary>The gradient the cells are coloured from now.</summary>
    internal MeterGradient Gradient => MeterGradient.For(GradientTarget);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public double LeftValue { get => GetValue(LeftValueProperty); set => SetValue(LeftValueProperty, value); }

    public double RightValue { get => GetValue(RightValueProperty); set => SetValue(RightValueProperty, value); }

    public double PeakHold { get => GetValue(PeakHoldProperty); set => SetValue(PeakHoldProperty, value); }

    public double LeftPeakHold { get => GetValue(LeftPeakHoldProperty); set => SetValue(LeftPeakHoldProperty, value); }

    public double RightPeakHold { get => GetValue(RightPeakHoldProperty); set => SetValue(RightPeakHoldProperty, value); }

    public bool IsReady { get => GetValue(IsReadyProperty); set => SetValue(IsReadyProperty, value); }

    public bool IsClipped { get => GetValue(IsClippedProperty); set => SetValue(IsClippedProperty, value); }

    public double MeterWidth { get => GetValue(MeterWidthProperty); set => SetValue(MeterWidthProperty, value); }

    public double ColumnWidth { get => GetValue(ColumnWidthProperty); set => SetValue(ColumnWidthProperty, value); }

    /// <summary>The well's rectangle inside the control: full width, inset top and bottom to sit inside the fader travel.</summary>
    internal Rect WellRect => new(Math.Round((Bounds.Width - MeterWidth) / 2), ConsoleRail.MeterInset, MeterWidth, Math.Max(0, Bounds.Height - (2 * ConsoleRail.MeterInset)));

    /// <summary>How many cells a column holds at the current height.</summary>
    internal int CellCount => CellsFor(WellRect.Height);

    /// <summary>The top lit cell and the held-peak cell (−1 for none) for a reading on a column of <paramref name="count"/> cells.</summary>
    internal (int TopLit, int Held) Cells(double value, double peakHold, int count)
    {
        if (!IsReady || double.IsNaN(value))
        {
            return (-1, -1);
        }

        var topLit = Math.Clamp((int)Math.Floor(ConsoleRail.Fraction(Mode, value) * count) - 1, -1, count - 1);
        var held = -1;
        if (Mode is MixerMeterMode.PeakRms or MixerMeterMode.K20 or MixerMeterMode.K14 or MixerMeterMode.K12 && !double.IsNaN(peakHold))
        {
            var hold = peakHold + MixerRail.KOffset(Mode);
            var (floor, _) = MixerRail.Range(Mode);
            if (hold > floor)
            {
                held = Math.Clamp((int)Math.Round(ConsoleRail.Fraction(Mode, hold) * count) - 1, -1, count - 1);
            }
        }

        return (topLit, held);
    }

    /// <summary>
    /// Shows a reading per channel without the property system, so a meter tick allocates nothing (ADR 0043); it redraws
    /// only when a value differs from the last shown. From the first call these take the place of
    /// <see cref="LeftValue"/>, <see cref="RightValue"/>, <see cref="LeftPeakHold"/> and <see cref="RightPeakHold"/>.
    /// </summary>
    internal void Show(double left, double right, double leftHold, double rightHold)
    {
        if (_fed && left.Equals(_fedLeft) && right.Equals(_fedRight) && leftHold.Equals(_fedLeftHold) && rightHold.Equals(_fedRightHold))
        {
            return;
        }

        _fed = true;
        _fedLeft = left;
        _fedRight = right;
        _fedLeftHold = leftHold;
        _fedRightHold = rightHold;
        InvalidateVisual();
    }

    /// <summary>What each column shows now: <see cref="Show"/>'s reading once fed, otherwise the properties.</summary>
    internal (double Left, double Right, double LeftHold, double RightHold) Shown => _fed
        ? (_fedLeft, _fedRight, _fedLeftHold, _fedRightHold)
        : (double.IsNaN(LeftValue) ? Value : LeftValue, double.IsNaN(RightValue) ? Value : RightValue,
            double.IsNaN(LeftPeakHold) ? PeakHold : LeftPeakHold, double.IsNaN(RightPeakHold) ? PeakHold : RightPeakHold);

    protected override Size MeasureOverride(Size availableSize) => new(MeterWidth, 0);

    public override void Render(DrawingContext context)
    {
        var well = WellRect;
        if (well.Height < 12)
        {
            return;
        }

        ConsolePaint.Well(context, well, 2);
        var inner = ConsolePaint.Snap(well).Deflate(2);
        var column = Math.Min(ColumnWidth, inner.Width / 2);
        var (left, right, leftHold, rightHold) = Shown;
        Column(context, new Rect(inner.X, inner.Y, column, inner.Height), left, leftHold);
        Column(context, new Rect(inner.Right - column, inner.Y, column, inner.Height), right, rightHold);
    }

    private static int CellsFor(double wellHeight)
    {
        var body = wellHeight - 4 - ClipHeight - 2;
        return Math.Max(0, (int)Math.Floor((body + 1) / Pitch));
    }

    private void Column(DrawingContext context, Rect column, double value, double peakHold)
    {
        var lamp = new Rect(column.X, column.Y, column.Width, ClipHeight);
        if (IsClipped)
        {
            Glow.Rect(context, lamp, 0.5, Palette.Clip, TopCellTier);
            context.FillRectangle(ConsolePaint.Brush(Palette.Clip), lamp);
        }
        else
        {
            context.FillRectangle(ConsolePaint.Brush(Palette.Alpha(Palette.Clip, 0.14)), lamp);
        }

        var bottom = column.Bottom;
        var count = (int)Math.Floor((column.Height - ClipHeight - 2 + 1) / Pitch);
        if (count <= 0)
        {
            return;
        }

        EnsureColors(count);
        var (topLit, held) = Cells(value, peakHold, count);
        for (var i = 0; i < count; i++)
        {
            var cell = new Rect(column.X, Math.Round(bottom - ((i + 1) * Pitch) + 1), column.Width, Cell);
            var lit = i <= topLit;
            if (lit && (i == topLit || i % 3 == 0))
            {
                Glow.Rect(context, cell, 0.5, _coloursFrom!.GlowBrushes(_colors[i], i == topLit ? TopCellTier : CellTier));
            }

            context.FillRectangle(lit ? _lit[i] : i == held ? _held[i] : _unlit[i], cell);
        }
    }

    /// <summary>
    /// One brush per cell for each state, rebuilt only when the cell count or the gradient changes. They come from
    /// <see cref="MeterGradient"/>'s caches, which empty on each change, not the app-lifetime ones in <see cref="ConsolePaint"/>.
    /// </summary>
    private void EnsureColors(int count)
    {
        var gradient = Gradient;
        if (_colors.Length == count && ReferenceEquals(_coloursFrom, gradient) && _version == gradient.Version)
        {
            return;
        }

        _coloursFrom = gradient;
        _version = gradient.Version;
        _colors = new Color[count];
        _lit = new IBrush[count];
        _unlit = new IBrush[count];
        _held = new IBrush[count];
        for (var i = 0; i < count; i++)
        {
            var color = gradient.At((i + 0.5) / count);
            _colors[i] = color;
            _lit[i] = gradient.Brush(color);
            _unlit[i] = gradient.Brush(Palette.Alpha(color, Palette.UnlitAlpha));
            _held[i] = gradient.Brush(ConsolePaint.Lighter(color, 0.3));
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(Gradient);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Subscribe(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GradientTargetProperty)
        {
            if (_subscribed is not null)
            {
                Subscribe(Gradient);
            }

            InvalidateVisual();
        }
    }

    /// <summary>The gradient whose changes redraw this meter (null while detached), for tests.</summary>
    internal MeterGradient? SubscribedTo => _subscribed;

    // Listens to one gradient at a time: the target's while attached, none while detached.
    private void Subscribe(MeterGradient? gradient)
    {
        if (ReferenceEquals(_subscribed, gradient))
        {
            return;
        }

        if (_subscribed is not null)
        {
            _subscribed.Changed -= OnGradientChanged;
        }

        _subscribed = gradient;
        if (gradient is not null)
        {
            gradient.Changed += OnGradientChanged;
        }
    }

    private void OnGradientChanged(object? sender, EventArgs e) => InvalidateVisual();
}
