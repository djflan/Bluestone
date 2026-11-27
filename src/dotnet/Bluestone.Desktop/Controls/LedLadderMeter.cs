using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Bluestone.Audio;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The Machined signal meter: a stereo pair of LED columns in a recessed dark well. Each column is 3 DIP cells on a
/// 4 DIP pitch, coloured from the floor up by the <see cref="MeterGradient"/> its <see cref="GradientTarget"/> names; while the LED glow is on
/// (<see cref="Glow"/>) every lit cell shows a hot core and throws a little baked light (<see cref="LedLight"/>) drawn under all the
/// cells, so the column stays crisp and saturated and its light pools on the well (the held peak and clip lamp brighter). The top three lit cells, the crest, are lighter toward the top, the top one with a hotter core and its own wider light; the ramp shows with the glow off too; unlit cells stay dark at <see cref="Palette.UnlitAlpha"/> of their colour, and the held peak
/// is the first unlit cell lightened. Above each column sits a separate clip lamp, always <see cref="Palette.Clip"/>,
/// dim until a peak passes 0 dBFS and lit until reset. Peak + RMS readings follow the console taper
/// (<see cref="ConsoleRail.Position"/>) so the meter, scale and fader agree; VU, K and LUFS are linear over their range.
/// Each cell fades as it goes dark (<see cref="CellFadeMs"/>) and warms up as it lights (<see cref="CellWarmupMs"/>); the
/// held peak and the clip lamps fade but light at once (<see cref="LedMotion"/>). The fade is per cell, after the levels'
/// own ballistics, so it never slows the fall itself.
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
    private const double CellLight = 0.5;

    /// <summary>The crest: how many of the top lit cells of a column are brighter, the top one most.</summary>
    internal const int CrestCells = 3;

    // Per crest position (0 the top lit cell): how much lighter the fill and the hot core are than the cell's colour, and
    // how many times the cell's own light it throws (the top cell its crest light as well, once fully lit).
    private static readonly double[] CrestFill = [0.32, 0.18, 0.08];
    private static readonly double[] CrestCore = [0.82, 0.68, 0.5];
    private static readonly int[] CrestLights = [2, 3, 2];
    private const double CellSpread = 0.8;
    private const double ClipLight = 1.2;
    private const int LightStride = LedLight.Levels + 1;
    private static readonly IBrush ClipCore = ConsolePaint.Brush(Palette.Lighten(Palette.Clip, 0.5));

    /// <summary>How long a cell and the held peak take to go dark, in milliseconds.</summary>
    internal const double CellFadeMs = 90;

    /// <summary>How long a cell takes to light, in milliseconds.</summary>
    internal const double CellWarmupMs = 20;

    /// <summary>How long a clip lamp takes to go dark, in milliseconds.</summary>
    internal const double LampFadeMs = 140;

    private const int Stride = LedLevels.Steps + 1;

    private Color[] _colors = [];
    private IBrush[] _lit = [];
    private IBrush[] _unlit = [];
    private IBrush[] _held = [];
    private IBrush[] _cellSteps = [];
    private IBrush[] _heldSteps = [];
    private IBrush[] _cores = [];
    private IBrush[] _topCores = [];
    private IBrush[] _crestFills = [];
    private IBrush[] _crestCores = [];
    private LedSprite?[] _crestLights = [];
    private LedSprite?[] _lights = [];
    private readonly LedSprite?[] _lampLights = new LedSprite?[LedLight.Levels + 1];
    private int _version = -1;
    private int _glowVersion = -1;
    private double _lightWidth = -1;
    private MeterGradient? _coloursFrom;
    private MeterGradient? _subscribed;

    // Levels fed through Show (the Device View's meters), as plain fields: a styled property allocates on every change, and
    // these change on every meter tick. While _fed, Render reads these instead of the per-channel properties.
    private bool _fed;
    private double _fedLeft = double.NaN;
    private double _fedRight = double.NaN;
    private double _fedLeftHold = double.NaN;
    private double _fedRightHold = double.NaN;

    private readonly LedLevels _leftCells;
    private readonly LedLevels _rightCells;
    private readonly LedLevels _leftHeld;
    private readonly LedLevels _rightHeld;
    private readonly LedLevels _lamps;

    static LedLadderMeter()
    {
        AffectsRender<LedLadderMeter>(ModeProperty, ValueProperty, LeftValueProperty, RightValueProperty, PeakHoldProperty, LeftPeakHoldProperty,
            RightPeakHoldProperty, IsReadyProperty, IsClippedProperty, MeterWidthProperty, ColumnWidthProperty);
        AffectsMeasure<LedLadderMeter>(MeterWidthProperty);
    }

    public LedLadderMeter()
    {
        _leftCells = new LedLevels(this, CellFadeMs, CellWarmupMs);
        _rightCells = new LedLevels(this, CellFadeMs, CellWarmupMs);
        _leftHeld = new LedLevels(this, CellFadeMs);
        _rightHeld = new LedLevels(this, CellFadeMs);
        _lamps = new LedLevels(this, LampFadeMs, count: 2);
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

    /// <summary>The left column's cells, its held peak, and both clip lamps (left 0, right 1), for tests.</summary>
    internal (LedLevels Cells, LedLevels Held, LedLevels Lamps) LeftLeds => (_leftCells, _leftHeld, _lamps);

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
        var now = LedMotion.Now;
        var leftColumn = new Rect(inner.X, inner.Y, column, inner.Height);
        var rightColumn = new Rect(inner.Right - column, inner.Y, column, inner.Height);

        // The light first, both columns', so no cell's spill lies over another cell: the cells stay crisp and saturated.
        Light(context, leftColumn, left, leftHold, _leftCells, _leftHeld, 0, now);
        Light(context, rightColumn, right, rightHold, _rightCells, _rightHeld, 1, now);
        Column(context, leftColumn, left, leftHold, _leftCells, _leftHeld, 0);
        Column(context, rightColumn, right, rightHold, _rightCells, _rightHeld, 1);
    }

    private static int CellsFor(double wellHeight)
    {
        var body = wellHeight - 4 - ClipHeight - 2;
        return Math.Max(0, (int)Math.Floor((body + 1) / Pitch));
    }

    private static int CountFor(Rect column) => (int)Math.Floor((column.Height - ClipHeight - 2 + 1) / Pitch);

    internal static Rect CellRect(Rect column, int i) => new(column.X, Math.Round(column.Bottom - ((i + 1) * Pitch) + 1), column.Width, Cell);

    /// <summary>
    /// Moves every lamp of a column to its target for <paramref name="now"/> and, while the glow is on, draws each lit one's
    /// baked light (<see cref="LedLight"/>): ordinary cells quietly, so a full column pools to its colour instead of
    /// white; the top cell, the held peak, and the clip lamp brighter.
    /// </summary>
    private void Light(DrawingContext context, Rect column, double value, double peakHold, LedLevels cells, LedLevels heldCells, int side, double now)
    {
        var glow = Glow.IsOn;
        _lamps.Set(side, IsClipped, now);
        var lampLevel = glow ? LedLight.Level(_lamps.Step(side)) : 0;
        var count = CountFor(column);
        if (count <= 0)
        {
            return;
        }

        EnsureColors(count, column.Width);
        if (lampLevel > 0 && _lampLights[lampLevel] is { } lampLight)
        {
            lampLight.Draw(context, new Rect(column.X, column.Y, column.Width, ClipHeight));
        }

        var fresh = cells.Resize(count) | heldCells.Resize(count);
        var (topLit, held) = Cells(value, peakHold, count);
        for (var i = 0; i < count; i++)
        {
            var lit = i <= topLit;
            cells.Set(i, lit, now, fresh);
            heldCells.Set(i, !lit && i == held, now, fresh);
            if (!glow)
            {
                continue;
            }

            var level = Math.Max(LedLight.Level(cells.Step(i)), LedLight.Level(heldCells.Step(i)));
            if (level > 0 && _lights[(i * LightStride) + level] is { } light)
            {
                var cell = CellRect(column, i);
                var crest = Crest(i, topLit);
                var times = crest >= 0 ? CrestLights[crest] : i == held ? 2 : 1;
                for (var n = 0; n < times; n++)
                {
                    light.Draw(context, cell);
                }

                // The top lit cell's crest light, brighter and wider, once it is fully lit (its own light once more while it warms up).
                if (crest == 0)
                {
                    if (level == LedLight.Levels && _crestLights[i] is { } crestLight)
                    {
                        crestLight.Draw(context, cell);
                    }
                    else
                    {
                        light.Draw(context, cell);
                    }
                }
            }
        }
    }

    private void Column(DrawingContext context, Rect column, double value, double peakHold, LedLevels cells, LedLevels heldCells, int side)
    {
        var glow = Glow.IsOn;
        var lamp = new Rect(column.X, column.Y, column.Width, ClipHeight);
        var lampStep = _lamps.Step(side);
        if (lampStep == LedLevels.Steps)
        {
            context.FillRectangle(ConsolePaint.Brush(Palette.Clip), lamp);
            if (glow)
            {
                context.FillRectangle(ClipCore, Core(lamp));
            }
        }
        else if (lampStep == 0)
        {
            context.FillRectangle(ConsolePaint.Brush(Palette.Alpha(Palette.Clip, 0.14)), lamp);
        }
        else
        {
            // Fading: the fill between lit and dim, one of a few cached steps.
            var lit = lampStep / (double)LedLevels.Steps;
            context.FillRectangle(ConsolePaint.Brush(Palette.Alpha(Palette.Clip, 0.14 + (0.86 * lit))), lamp);
        }

        var count = CountFor(column);
        if (count <= 0)
        {
            return;
        }

        var (topLit, _) = Cells(value, peakHold, count);
        for (var i = 0; i < count; i++)
        {
            var cell = CellRect(column, i);
            var step = cells.Step(i);
            var heldStep = heldCells.Step(i);
            var crest = Crest(i, topLit);
            if (crest >= 0 && step > 0)
            {
                // The crest: the top lit cells a little lighter, the top one most, at the cell's own step of its warm-up so
                // a rising column stays smooth. The ramp shows with the glow off too; only the hot core is the glow's.
                var slot = (i * CrestCells) + crest;
                context.FillRectangle(_crestFills[(slot * Stride) + step], cell);
                if (glow && step == LedLevels.Steps)
                {
                    context.FillRectangle(_crestCores[slot], Core(cell));
                }
            }
            else if (step == LedLevels.Steps)
            {
                context.FillRectangle(_lit[i], cell);
                if (glow)
                {
                    // The die's hot core, a little lighter than the cell.
                    context.FillRectangle(_cores[i], Core(cell));
                }
            }
            else if (step == 0 && heldStep == LedLevels.Steps)
            {
                context.FillRectangle(_held[i], cell);
                if (glow)
                {
                    context.FillRectangle(_topCores[i], Core(cell));
                }
            }
            else
            {
                // Dark, or between dark and lit; a fading held peak shows over it.
                context.FillRectangle(_cellSteps[(i * Stride) + step], cell);
                if (heldStep > 0)
                {
                    context.FillRectangle(_heldSteps[(i * Stride) + heldStep], cell);
                }
            }
        }
    }

    /// <summary>Where lit cell <paramref name="i"/> sits in its column's crest (0 the top lit cell), or −1 below it or unlit.</summary>
    internal static int Crest(int i, int topLit) => i <= topLit && topLit - i < CrestCells ? topLit - i : -1;

    /// <summary>A lamp's hot core: its middle, inset a DIP from each side, one DIP tall.</summary>
    private static Rect Core(Rect lamp) => new(lamp.X + 1, lamp.Y + Math.Floor((lamp.Height - 1) / 2), Math.Max(1, lamp.Width - 2), 1);

    /// <summary>
    /// One brush per cell for each state, rebuilt only when the cell count or the gradient changes. They come from
    /// <see cref="MeterGradient"/>'s caches, which empty on each change, not the app-lifetime ones in <see cref="ConsolePaint"/>.
    /// </summary>
    private void EnsureColors(int count, double width)
    {
        var gradient = Gradient;
        if (_colors.Length == count && ReferenceEquals(_coloursFrom, gradient) && _version == gradient.Version && _glowVersion == Glow.Version && _lightWidth == width)
        {
            return;
        }

        _coloursFrom = gradient;
        _version = gradient.Version;
        _glowVersion = Glow.Version;
        _lightWidth = width;
        _colors = new Color[count];
        _lit = new IBrush[count];
        _unlit = new IBrush[count];
        _held = new IBrush[count];
        _cores = new IBrush[count];
        _topCores = new IBrush[count];
        _crestFills = new IBrush[count * CrestCells * Stride];
        _crestCores = new IBrush[count * CrestCells];
        _crestLights = new LedSprite?[count];
        _lights = new LedSprite?[count * LightStride];
        _cellSteps = new IBrush[count * Stride];
        _heldSteps = new IBrush[count * Stride];
        var cellSize = new Size(width, Cell);
        for (var i = 0; i < count; i++)
        {
            var color = gradient.At((i + 0.5) / count);
            var heldColor = ConsolePaint.Lighter(color, 0.3);
            _colors[i] = color;
            _lit[i] = gradient.Brush(color);
            _unlit[i] = gradient.Brush(Palette.Alpha(color, Palette.UnlitAlpha));
            _held[i] = gradient.Brush(heldColor);
            _cores[i] = gradient.Brush(Palette.Lighten(color, 0.3));
            _topCores[i] = gradient.Brush(Palette.Lighten(color, 0.55));

            // Each level of the cell's light, baked now so a first fade allocates nothing. Cells sit 1 DIP apart and their
            // light adds up, so each throws a little and not far.
            for (var level = 1; level <= LedLight.Levels; level++)
            {
                _lights[(i * LightStride) + level] = gradient.Light(color, cellSize, 0.5, ellipse: false, CellLight * level / LedLight.Levels, CellSpread);
            }

            // Any cell can be the top of the column: its crest fills at every step of a warm-up, its cores, and the top
            // cell's crest light (lit only, so one level), all built now so the crest moving up and down allocates nothing.
            _crestLights[i] = gradient.Light(Palette.Lighten(color, 0.1), cellSize, 0.5, ellipse: false, 1.0);
            for (var crest = 0; crest < CrestCells; crest++)
            {
                var slot = (i * CrestCells) + crest;
                var crestColor = Palette.Lighten(color, CrestFill[crest]);
                _crestCores[slot] = gradient.Brush(Palette.Lighten(color, CrestCore[crest]));
                for (var step = 1; step <= LedLevels.Steps; step++)
                {
                    var lit = step / (double)LedLevels.Steps;
                    _crestFills[(slot * Stride) + step] = gradient.Brush(step == LedLevels.Steps ? crestColor
                        : Palette.Alpha(crestColor, Palette.UnlitAlpha + ((1 - Palette.UnlitAlpha) * lit)));
                }
            }

            // Between dark and lit: the cell's colour from its unlit alpha up to opaque, and the held colour over it.
            for (var step = 0; step <= LedLevels.Steps; step++)
            {
                var lit = step / (double)LedLevels.Steps;
                _cellSteps[(i * Stride) + step] = step == 0 ? _unlit[i] : step == LedLevels.Steps ? _lit[i]
                    : gradient.Brush(Palette.Alpha(color, Palette.UnlitAlpha + ((1 - Palette.UnlitAlpha) * lit)));
                _heldSteps[(i * Stride) + step] = step == LedLevels.Steps ? _held[i] : gradient.Brush(Palette.Alpha(heldColor, lit));

            }
        }

        WarmClipLamp();
        var lampSize = new Size(width, ClipHeight);
        for (var level = 1; level <= LedLight.Levels; level++)
        {
            _lampLights[level] = LedLight.For(Palette.Clip, lampSize, 0.5, ellipse: false, ClipLight * level / LedLight.Levels);
        }
    }

    private static bool _clipLampWarm;

    // The clip lamp's fixed colour has one brush per step, in the app-lifetime caches: build them all once.
    private static void WarmClipLamp()
    {
        if (_clipLampWarm)
        {
            return;
        }

        _clipLampWarm = true;
        for (var step = 0; step <= LedLevels.Steps; step++)
        {
            var lit = step / (double)LedLevels.Steps;
            _ = ConsolePaint.Brush(Palette.Alpha(Palette.Clip, 0.14 + (0.86 * lit)));
        }

        _ = ConsolePaint.Brush(Palette.Clip);
        _ = ConsolePaint.Brush(Palette.Alpha(Palette.Clip, 0.14));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(Gradient);
        Glow.Changed += OnGradientChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Glow.Changed -= OnGradientChanged;
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
