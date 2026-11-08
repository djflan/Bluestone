using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Bluestone.Desktop.Theme;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Controls;

/// <summary>Where a seam's note sits: at header height, or coupled with the level, over it or under it.</summary>
internal enum JunctionLampPlacement
{
    Header,
    AboveLevel,
    BelowLevel,
}

/// <summary>
/// The seam between two devices of a chain (ADR 0037): the hairline the signal flows along, and a slim segmented stereo
/// level for its audio with a small MIDI icon under it that glows while notes are held there and lets go softly. Where they
/// sit is the musician's choice (Settings ▸ Display). Only what can be there is drawn; an idle seam is quiet. Hovering
/// shows what is there in words. Drawing allocates nothing.
/// </summary>
internal sealed class JunctionSeam : Control, ICustomHitTest
{
    public static readonly StyledProperty<JunctionLevelStyle> LevelStyleProperty = AvaloniaProperty.Register<JunctionSeam, JunctionLevelStyle>(nameof(LevelStyle));

    public static readonly StyledProperty<ICommand?> InsertCommandProperty = AvaloniaProperty.Register<JunctionSeam, ICommand?>(nameof(InsertCommand));

    public static readonly StyledProperty<ICommand?> RouteCommandProperty = AvaloniaProperty.Register<JunctionSeam, ICommand?>(nameof(RouteCommand));

    public static readonly StyledProperty<Color> AccentColorProperty = AvaloniaProperty.Register<JunctionSeam, Color>(nameof(AccentColor), Palette.Accent);

    public static readonly StyledProperty<JunctionLampPlacement> LampPlacementProperty = AvaloniaProperty.Register<JunctionSeam, JunctionLampPlacement>(nameof(LampPlacement), JunctionLampPlacement.BelowLevel);

    public static readonly StyledProperty<double> LampHeightProperty = AvaloniaProperty.Register<JunctionSeam, double>(nameof(LampHeight), 11);

    public static readonly StyledProperty<double> LampGapProperty = AvaloniaProperty.Register<JunctionSeam, double>(nameof(LampGap), 7);

    public static readonly StyledProperty<Color> LampColorProperty = AvaloniaProperty.Register<JunctionSeam, Color>(nameof(LampColor), Palette.JunctionLamp);

    public static readonly StyledProperty<Color> LevelLowColorProperty = AvaloniaProperty.Register<JunctionSeam, Color>(nameof(LevelLowColor), Palette.MeterLow);

    public static readonly StyledProperty<Color> LevelMidColorProperty = AvaloniaProperty.Register<JunctionSeam, Color>(nameof(LevelMidColor), Palette.MeterMid);

    public static readonly StyledProperty<Color> LevelHotColorProperty = AvaloniaProperty.Register<JunctionSeam, Color>(nameof(LevelHotColor), Palette.MeterHot);

    public static readonly StyledProperty<Color> LevelClipColorProperty = AvaloniaProperty.Register<JunctionSeam, Color>(nameof(LevelClipColor), Palette.MeterClip);

    public static readonly StyledProperty<JunctionSignal?> SignalProperty = AvaloniaProperty.Register<JunctionSeam, JunctionSignal?>(nameof(Signal));

    public static readonly StyledProperty<bool> IsQuietProperty = AvaloniaProperty.Register<JunctionSeam, bool>(nameof(IsQuiet));

    public static readonly StyledProperty<double> HeaderHeightProperty = AvaloniaProperty.Register<JunctionSeam, double>(nameof(HeaderHeight), 30);

    public static readonly StyledProperty<double> LinkYProperty = AvaloniaProperty.Register<JunctionSeam, double>(nameof(LinkY), 105);

    public static readonly StyledProperty<double> LevelHeightProperty = AvaloniaProperty.Register<JunctionSeam, double>(nameof(LevelHeight), 52);

    public static readonly StyledProperty<JunctionIndicatorPosition> IndicatorPositionProperty = AvaloniaProperty.Register<JunctionSeam, JunctionIndicatorPosition>(nameof(IndicatorPosition), JunctionIndicatorPosition.LowerHalf);

    public static readonly StyledProperty<double> FooterHeightProperty = AvaloniaProperty.Register<JunctionSeam, double>(nameof(FooterHeight), 22);

    public static readonly StyledProperty<double> LevelBarWidthProperty = AvaloniaProperty.Register<JunctionSeam, double>(nameof(LevelBarWidth), 5);

    public static readonly StyledProperty<double> LevelSegmentHeightProperty = AvaloniaProperty.Register<JunctionSeam, double>(nameof(LevelSegmentHeight), 1);

    public static readonly StyledProperty<double> LevelSegmentGapProperty = AvaloniaProperty.Register<JunctionSeam, double>(nameof(LevelSegmentGap), 1);

    public static readonly StyledProperty<TimeSpan> LampReleaseProperty = AvaloniaProperty.Register<JunctionSeam, TimeSpan>(nameof(LampRelease), TimeSpan.FromMilliseconds(220));

    private static readonly IBrush Link = new SolidColorBrush(Palette.Lighten(Palette.Border, 0.08));
    private static readonly IBrush QuietLink = new SolidColorBrush(Palette.Border);
    private static readonly IBrush PlusFill = new SolidColorBrush(Palette.Raised);
    private static readonly IPen PlusRim = new Pen(new SolidColorBrush(Palette.Lighten(Palette.Border, 0.12)), 1);
    private static readonly IPen PlusMark = new Pen(new SolidColorBrush(Palette.TextMuted), 1.25, lineCap: PenLineCap.Round);
    private static readonly IPen PlusMarkHot = new Pen(Brushes.White, 1.25, lineCap: PenLineCap.Round);
    private static readonly IBrush RoutedHole = new SolidColorBrush(Palette.Sunken);
    private const double PlusSize = 14;
    private const double RoutedSize = 7;
    private const double LowFraction = 0.4;
    private const double MidFraction = 0.62;
    private const double NoteWidth = 12;

    // The device panels' border (Border.devicePanel): Top and Bottom line up with the panel just inside it, where its
    // accent stripe and its last row are.
    private const double PanelBorder = 1;
    private const double NoteHeight = 12;
    private static readonly Color NoteOff = Palette.Lighten(Palette.Border, 0.06);
    private static readonly IBrush NoteOffBrush = new SolidColorBrush(NoteOff);
    private static readonly double HotFraction = MeterLevels.Fraction(-6);
    private static readonly double ZeroFraction = MeterLevels.Fraction(0);

    // A MIDI connector in a 12 x 12 box: the ring with its key notch at the top, and the five pins in a horseshoe.
    private static readonly Geometry NoteGlyph = new GeometryGroup
    {
        FillRule = FillRule.EvenOdd,
        Children =
        [
            new EllipseGeometry(new Rect(0, 0, 12, 12)),
            new EllipseGeometry(new Rect(1.25, 1.25, 9.5, 9.5)),
            new RectangleGeometry(new Rect(5.1, 0, 1.8, 1.25)),
            Pin(180),
            Pin(135),
            Pin(90),
            Pin(45),
            Pin(0),
        ],
    };

    private readonly EventHandler _updated;
    private readonly PropertyChangedEventHandler _changed;
    private JunctionSignal? _subscribed;
    private double _glow;
    private long _lastFrame;
    private (bool Active, double PeakL, double PeakR, double RmsL, double RmsR, double HoldL, double HoldR, double Glow) _drawn = (false, -1, -1, -1, -1, -1, -1, -1);
    private (SolidColorBrush[] Lit, SolidColorBrush[] Halo, SolidColorBrush[] Dim)? _segments;
    private SolidColorBrush? _lampBrush;
    private RadialGradientBrush? _haloBrush;
    private SolidColorBrush? _accentBrush;
    private IPen? _accentPen;
    private SolidColorBrush? _markerBrush;
    private Cursor? _hand;
    private bool _hovered;
    private Part _over;

    static JunctionSeam()
    {
        AffectsRender<JunctionSeam>(SignalProperty, IsQuietProperty, HeaderHeightProperty, LinkYProperty, LevelHeightProperty, IndicatorPositionProperty, FooterHeightProperty, LevelBarWidthProperty,
            LampPlacementProperty, LampHeightProperty, LampGapProperty, LampColorProperty, LevelLowColorProperty, LevelMidColorProperty, LevelHotColorProperty, LevelClipColorProperty,
            LevelSegmentHeightProperty, LevelSegmentGapProperty, LevelStyleProperty, InsertCommandProperty, AccentColorProperty);
        FocusableProperty.OverrideDefaultValue<JunctionSeam>(false);
    }

    public JunctionSeam()
    {
        _updated = (_, _) => OnFrame();
        _changed = (_, e) =>
        {
            if (e.PropertyName is nameof(JunctionSignal.Label) or nameof(JunctionSignal.CanCarryNotes) or nameof(JunctionSignal.CanCarryAudio)
                or nameof(JunctionSignal.Routes) or nameof(JunctionSignal.IsInsertionPoint))
            {
                UpdateName();
                InvalidateVisual();
            }
        };
        ClipToBounds = false;
        var insert = new MenuItem { Header = "Insert Device Here" };
        insert.Click += (_, _) => Run(InsertCommand);
        var route = new MenuItem { Header = "Route from Here…" };
        route.Click += (_, _) => Run(RouteCommand);
        ContextMenu = new ContextMenu { Items = { insert, route } };
        ContextMenu.Opening += (_, _) =>
        {
            insert.IsEnabled = InsertCommand?.CanExecute(Signal) == true;
            route.IsEnabled = RouteCommand?.CanExecute(Signal) == true;
        };
    }

    private enum Part
    {
        None,
        Plus,
        Routed,
    }

    /// <summary>What the level shows: the average with the peak held above it, or the peak (Settings ▸ Display).</summary>
    public JunctionLevelStyle LevelStyle
    {
        get => GetValue(LevelStyleProperty);
        set => SetValue(LevelStyleProperty, value);
    }

    /// <summary>Run with <see cref="Signal"/> by the hover +, Enter, and "Insert Device Here": aims the Browser at this gap.</summary>
    public ICommand? InsertCommand
    {
        get => GetValue(InsertCommandProperty);
        set => SetValue(InsertCommandProperty, value);
    }

    /// <summary>Run with <see cref="Signal"/> by the routed mark and "Route from Here…": opens the connections from this point.</summary>
    public ICommand? RouteCommand
    {
        get => GetValue(RouteCommandProperty);
        set => SetValue(RouteCommandProperty, value);
    }

    /// <summary>The colour of the routed mark, the + under the pointer, and the insertion mark.</summary>
    public Color AccentColor
    {
        get => GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    public JunctionLampPlacement LampPlacement
    {
        get => GetValue(LampPlacementProperty);
        set => SetValue(LampPlacementProperty, value);
    }

    /// <summary>The note's height when it is coupled with the level.</summary>
    public double LampHeight
    {
        get => GetValue(LampHeightProperty);
        set => SetValue(LampHeightProperty, value);
    }

    /// <summary>The space between a coupled note and the level's bars.</summary>
    public double LampGap
    {
        get => GetValue(LampGapProperty);
        set => SetValue(LampGapProperty, value);
    }

    public Color LampColor
    {
        get => GetValue(LampColorProperty);
        set => SetValue(LampColorProperty, value);
    }

    /// <summary>The level's colour at ordinary levels.</summary>
    public Color LevelLowColor
    {
        get => GetValue(LevelLowColorProperty);
        set => SetValue(LevelLowColorProperty, value);
    }

    /// <summary>The level's colour between ordinary levels and hot ones.</summary>
    public Color LevelMidColor
    {
        get => GetValue(LevelMidColorProperty);
        set => SetValue(LevelMidColorProperty, value);
    }

    /// <summary>The level's colour approaching full scale (from about -6 dB).</summary>
    public Color LevelHotColor
    {
        get => GetValue(LevelHotColorProperty);
        set => SetValue(LevelHotColorProperty, value);
    }

    /// <summary>The level's colour at and past full scale, and the clip mark.</summary>
    public Color LevelClipColor
    {
        get => GetValue(LevelClipColorProperty);
        set => SetValue(LevelClipColorProperty, value);
    }

    public JunctionSignal? Signal
    {
        get => GetValue(SignalProperty);
        set => SetValue(SignalProperty, value);
    }

    /// <summary>The device after it is bypassed: the seam's hairline is quieter, as the chain link was.</summary>
    public bool IsQuiet
    {
        get => GetValue(IsQuietProperty);
        set => SetValue(IsQuietProperty, value);
    }

    public double HeaderHeight
    {
        get => GetValue(HeaderHeightProperty);
        set => SetValue(HeaderHeightProperty, value);
    }

    public double LinkY
    {
        get => GetValue(LinkYProperty);
        set => SetValue(LinkYProperty, value);
    }

    public double LevelHeight
    {
        get => GetValue(LevelHeightProperty);
        set => SetValue(LevelHeightProperty, value);
    }

    /// <summary>Where the level and its lamp sit, top to bottom (Settings ▸ Display).</summary>
    public JunctionIndicatorPosition IndicatorPosition
    {
        get => GetValue(IndicatorPositionProperty);
        set => SetValue(IndicatorPositionProperty, value);
    }

    /// <summary>The device panels' footer height: the body the halves are measured in ends above it.</summary>
    public double FooterHeight
    {
        get => GetValue(FooterHeightProperty);
        set => SetValue(FooterHeightProperty, value);
    }

    public double LevelBarWidth
    {
        get => GetValue(LevelBarWidthProperty);
        set => SetValue(LevelBarWidthProperty, value);
    }

    /// <summary>The height of one of the level's segments.</summary>
    public double LevelSegmentHeight
    {
        get => GetValue(LevelSegmentHeightProperty);
        set => SetValue(LevelSegmentHeightProperty, value);
    }

    /// <summary>The dark space between the level's segments.</summary>
    public double LevelSegmentGap
    {
        get => GetValue(LevelSegmentGapProperty);
        set => SetValue(LevelSegmentGapProperty, value);
    }

    public TimeSpan LampRelease
    {
        get => GetValue(LampReleaseProperty);
        set => SetValue(LampReleaseProperty, value);
    }

    /// <summary>What the seam carries now, in words, for its tooltip and screen readers.</summary>
    internal string Describe()
    {
        if (Signal is not { } signal)
        {
            return string.Empty;
        }

        var parts = new List<string> { signal.Label };
        if (signal.CanCarryNotes)
        {
            parts.Add(signal.NotesHeld ? "notes held" : "no notes");
        }

        if (signal.CanCarryAudio)
        {
            var levels = signal.Levels;
            var peak = Math.Max(levels.PeakLeft, levels.PeakRight);
            parts.Add(!levels.IsActive || peak <= MeterLevels.FloorDecibels ? "silent"
                : string.Create(CultureInfo.InvariantCulture, $"peak {peak:0.0} dB{(peak > 0 ? ", clipping" : string.Empty)}"));
        }

        if (signal.IsRouted)
        {
            parts.Add($"also sent to {signal.Routes}");
        }

        return string.Join(" · ", parts);
    }

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    protected override AutomationPeer OnCreateAutomationPeer() => new SeamPeer(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AccentColorProperty)
        {
            _accentBrush = null;
            _accentPen = null;
            _markerBrush = null;
        }

        if (change.Property == LampColorProperty || change.Property == LevelLowColorProperty || change.Property == LevelMidColorProperty || change.Property == LevelHotColorProperty || change.Property == LevelClipColorProperty)
        {
            _haloBrush = null;
            _lampBrush = null;
            _segments = null;
        }

        if (change.Property == SignalProperty)
        {
            // Without a junction (the trailing wire of an empty chain) the seam is only drawing: not a tab stop, no menu.
            Focusable = Signal is not null;
            ContextMenu!.IsEnabled = Signal is not null;
            Subscribe(VisualRoot is not null ? Signal : null);
            UpdateName();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(Signal);
        UpdateName();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Subscribe(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        ToolTip.SetTip(this, Describe());
        _hovered = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var over = PartAt(e.GetPosition(this));
        if (over != _over)
        {
            _over = over;
            Cursor = over == Part.None ? Cursor.Default : _hand ??= new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(this, over switch
            {
                Part.Plus => "Insert a device here: opens the Browser aimed at this gap",
                Part.Routed => $"Also sent to {Signal?.Routes}. Click to open the connections from here.",
                _ => Describe(),
            });
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = false;
        _over = Part.None;
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        switch (PartAt(e.GetPosition(this)))
        {
            case Part.Plus:
                Run(InsertCommand);
                e.Handled = true;
                break;
            case Part.Routed:
                Run(RouteCommand);
                e.Handled = true;
                break;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Enter or Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            Run(InsertCommand);
            e.Handled = true;
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    private void Run(ICommand? command)
    {
        if (command?.CanExecute(Signal) == true)
        {
            command.Execute(Signal);
        }
    }

    // What is under the pointer: the + (only while it shows), the routed mark, or neither. Generous targets: a few pixels
    // round each, within the seam.
    private Part PartAt(Point point)
    {
        if (Signal is not { } signal)
        {
            return Part.None;
        }

        var center = Math.Round(Bounds.Width / 2);
        if (signal.IsRouted && RoutedRect(center).Inflate(4).Contains(point))
        {
            return Part.Routed;
        }

        return ShowsPlus && PlusRect(center).Inflate(3).Contains(point) ? Part.Plus : Part.None;
    }

    /// <summary>Where the + is (drawn only while hovered or focused) and where the routed mark is, in the seam's coordinates.</summary>
    internal (Rect Plus, Rect Routed) Targets => (PlusRect(Math.Round(Bounds.Width / 2)), RoutedRect(Math.Round(Bounds.Width / 2)));

    internal bool ShowsPlus => InsertCommand is not null && (_hovered || IsKeyboardFocusWithin);

    // The span the level and its note take, top to bottom; empty when the seam shows neither.
    private (double Top, double Bottom) GroupSpan(double center)
    {
        if (Signal is not { } signal || (!signal.CanCarryAudio && !signal.CanCarryNotes))
        {
            return (0, 0);
        }

        var (top, bottom, _, _) = LevelFrame(center);
        var lamp = LampHeight + LampGap;
        return LampPlacement switch
        {
            JunctionLampPlacement.AboveLevel => (top - lamp, bottom),
            JunctionLampPlacement.BelowLevel => (top, bottom + lamp),
            _ => (top, bottom),
        };
    }

    // The routed mark sits on the hairline, where the signal leaves; if the indicators are there, just under them.
    private Rect RoutedRect(double center)
    {
        var y = Free(LinkY + 0.5, RoutedSize, GroupSpan(center), (0, 0));
        return new Rect(center - (RoutedSize / 2), y - (RoutedSize / 2), RoutedSize, RoutedSize);
    }

    // The + sits just above the hairline, or below it, wherever the indicators and the routed mark leave room.
    private Rect PlusRect(double center)
    {
        var routed = Signal is { IsRouted: true } ? RoutedRect(center) : default;
        var y = Free(LinkY - 20, PlusSize, GroupSpan(center), (routed.Top, routed.Bottom));
        return new Rect(center - (PlusSize / 2), Math.Round(y - (PlusSize / 2)), PlusSize, PlusSize);
    }

    // A centre near preferred for something size tall that keeps 3 px clear of two spans, inside the panel's body.
    private double Free(double preferred, double size, (double Top, double Bottom) first, (double Top, double Bottom) second)
    {
        var half = size / 2;
        ReadOnlySpan<double> candidates = [preferred, LinkY + 20, first.Top - 3 - half, first.Bottom + 3 + half, second.Bottom + 3 + half, second.Top - 3 - half];
        foreach (var y in candidates)
        {
            if (y - half >= HeaderHeight && y + half <= Bounds.Height - PanelBorder && Clear(y, first) && Clear(y, second))
            {
                return y;
            }
        }

        return preferred;

        bool Clear(double y, (double Top, double Bottom) span) => span.Bottom <= span.Top || y + half + 3 <= span.Top || y - half - 3 >= span.Bottom;
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var center = Math.Round(width / 2);
        if (Signal is { IsInsertionPoint: true })
        {
            // The gap the Browser's next insert goes into, marked as a drop's insertion marker marks it.
            _markerBrush ??= new SolidColorBrush(AccentColor, 0.85);
            context.DrawRectangle(_markerBrush, null, new Rect(center - 1.5, 0, 3, Bounds.Height), 1.5, 1.5);
        }

        context.FillRectangle(IsQuiet ? QuietLink : Link, new Rect(0, LinkY, width, 1));
        if (Signal is not { } signal)
        {
            return;
        }

        if (signal.CanCarryNotes)
        {
            DrawLamp(context, center);
        }

        if (signal.CanCarryAudio)
        {
            DrawLevel(context, center, signal.Levels);
        }

        _accentBrush ??= new SolidColorBrush(AccentColor);
        if (signal.IsRouted)
        {
            // A tap on the wire: signal is also taken from here.
            var mark = RoutedRect(center);
            context.DrawEllipse(RoutedHole, null, mark.Center, (mark.Width / 2) + 1.5, (mark.Height / 2) + 1.5);
            context.DrawEllipse(_accentBrush, null, mark.Center, mark.Width / 2, mark.Height / 2);
            if (_over == Part.Routed)
            {
                _accentPen ??= new Pen(_accentBrush, 1);
                context.DrawEllipse(null, _accentPen, mark.Center, (mark.Width / 2) + 2.5, (mark.Height / 2) + 2.5);
            }
        }

        if (ShowsPlus)
        {
            var plus = PlusRect(center);
            var hot = _over == Part.Plus;
            context.DrawEllipse(hot ? _accentBrush : PlusFill, hot ? null : PlusRim, plus.Center, plus.Width / 2, plus.Height / 2);
            var arm = 3.5;
            var pen = hot ? PlusMarkHot : PlusMark;
            context.DrawLine(pen, new Point(plus.Center.X - arm, plus.Center.Y), new Point(plus.Center.X + arm, plus.Center.Y));
            context.DrawLine(pen, new Point(plus.Center.X, plus.Center.Y - arm), new Point(plus.Center.X, plus.Center.Y + arm));
        }

        var levels = signal.Levels;
        _drawn = (levels.IsActive, levels.PeakLeft, levels.PeakRight, levels.RmsLeft, levels.RmsRight, levels.HoldLeft, levels.HoldRight, _glow);
    }

    // The level's frame: its bars' top and bottom, and the left edge of the left bar. The level and a lamp coupled with it
    // are placed together, as one group, where IndicatorPosition says.
    private (double Top, double Bottom, double Left, double Width) LevelFrame(double center)
    {
        var bar = LevelBarWidth;
        var lamp = LampPlacement == JunctionLampPlacement.Header ? 0 : LampHeight + LampGap;
        var group = LevelSpan().Height + lamp;
        var bodyTop = HeaderHeight;
        var body = Math.Max(0, Bounds.Height - FooterHeight - bodyTop);
        var groupTop = IndicatorPosition switch
        {
            JunctionIndicatorPosition.Top => PanelBorder,
            JunctionIndicatorPosition.UpperHalf => bodyTop + (body / 4) - (group / 2),
            JunctionIndicatorPosition.Bottom => Bounds.Height - PanelBorder - group,
            _ => bodyTop + (body * 3 / 4) - (group / 2),
        };
        var top = Math.Round(LampPlacement == JunctionLampPlacement.AboveLevel ? groupTop + lamp : groupTop);
        return (top, top + LevelSpan().Height, center - bar - 0.5, (2 * bar) + 1);
    }

    // How many segments fit in LevelHeight, and the height they take: the top one is the level's top edge.
    private (int Count, double Height) LevelSpan()
    {
        var segment = Math.Max(1, LevelSegmentHeight);
        var gap = Math.Max(0, LevelSegmentGap);
        var count = Math.Max(1, (int)Math.Floor((LevelHeight + gap) / (segment + gap)));
        return (count, (count * segment) + ((count - 1) * gap));
    }

    private void DrawLamp(DrawingContext context, double center)
    {
        // A small MIDI connector: at header height, or coupled with the level, over or under its bars.
        var height = LampHeight;
        var width = height * NoteWidth / NoteHeight;
        double top;
        if (LampPlacement == JunctionLampPlacement.Header)
        {
            top = Math.Round((HeaderHeight - height) / 2);
        }
        else
        {
            var (levelTop, levelBottom, _, _) = LevelFrame(center);
            top = LampPlacement == JunctionLampPlacement.AboveLevel ? levelTop - LampGap - height : levelBottom + LampGap;
        }

        var box = new Rect(center - (width / 2), top, width, height);
        var scale = height / NoteHeight;
        var color = LampColor;
        if (_glow > 0)
        {
            // Lit, it glows: a soft halo behind it, and the note itself fading back to unlit as the notes let go.
            _haloBrush ??= new RadialGradientBrush
            {
                GradientStops = [new GradientStop(Color.FromArgb(120, color.R, color.G, color.B), 0), new GradientStop(Color.FromArgb(40, color.R, color.G, color.B), 0.45), new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)],
            };
            using (context.PushOpacity(_glow))
            {
                context.DrawEllipse(_haloBrush, null, box.Center, height * 0.75, height * 0.75);
            }

            _lampBrush ??= new SolidColorBrush(color);
            _lampBrush.Color = Palette.Mix(NoteOff, Palette.Lighten(color, 0.12), _glow);
        }

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(box.X, box.Y)))
        {
            context.DrawGeometry(_glow > 0 ? _lampBrush : NoteOffBrush, null, NoteGlyph);
        }
    }

    private void DrawLevel(DrawingContext context, double center, MeterLevels levels)
    {
        var bar = LevelBarWidth;
        var (top, bottom, left, _) = LevelFrame(center);
        var segment = Math.Max(1, LevelSegmentHeight);
        var gap = Math.Max(0, LevelSegmentGap);
        var count = LevelSpan().Count;
        var segments = Segments(count);
        var silent = !levels.IsActive || Math.Max(levels.PeakLeft, levels.PeakRight) <= MeterLevels.FloorDecibels;

        // The average mode holds the peak for a moment (MeterLevels' hold), so it stays up through a quiet reading.
        var holds = levels.IsActive && LevelStyle == JunctionLevelStyle.RmsAndPeak && Math.Max(levels.HoldLeft, levels.HoldRight) > MeterLevels.FloorDecibels;

        // Like a hardware LED meter: every segment is there, unlit ones only a dim tint of their colour; idle, dimmer still.
        using (context.PushOpacity(silent ? 0.55 : 1))
        {
            for (var i = 0; i < count; i++)
            {
                var y = SegmentTop(i);
                context.DrawRectangle(segments.Dim[i], null, new Rect(left, y, bar, segment), 0.5, 0.5);
                context.DrawRectangle(segments.Dim[i], null, new Rect(left + bar + 1, y, bar, segment), 0.5, 0.5);
            }
        }

        if (silent && !holds)
        {
            return;
        }

        Side(levels.RmsLeft, levels.PeakLeft, levels.HoldLeft, left);
        Side(levels.RmsRight, levels.PeakRight, levels.HoldRight, left + bar + 1);

        double SegmentTop(int index) => bottom - ((index + 1) * segment) - (index * gap);

        void Side(double rms, double peak, double hold, double x)
        {
            // Lit up to the RMS with the held peak as one segment above it (or lit to the peak), and the top segment while
            // the peak is over full scale. An indicator shows what is happening now: unlike the mixer's clip light, the
            // over does not latch.
            var clipped = peak > 0;
            var peakOnly = LevelStyle == JunctionLevelStyle.Peak;
            var lit = silent ? 0 : (int)Math.Round(MeterLevels.Fraction(peakOnly ? peak : rms) * count);
            var held = peakOnly || hold <= MeterLevels.FloorDecibels ? -1 : Math.Min(count, (int)Math.Ceiling(MeterLevels.Fraction(hold) * count)) - 1;
            for (var i = 0; i < count; i++)
            {
                if (i < lit || i == held || (clipped && i == count - 1))
                {
                    var cell = new Rect(x, SegmentTop(i), bar, segment);
                    context.DrawRectangle(segments.Halo[i], null, cell.Inflate(new Thickness(1, 0.4)), 1, 1);
                    context.DrawRectangle(segments.Lit[i], null, cell, 0.5, 0.5);
                }
            }
        }
    }

    // A pin of the connector, at an angle round its centre (0 to the right, 90 straight down).
    private static EllipseGeometry Pin(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var center = new Point(6 + (3.1 * Math.Cos(radians)), 6.4 + (3.1 * Math.Sin(radians)) - 0.9);
        return new EllipseGeometry(new Rect(center.X - 0.85, center.Y - 0.85, 1.7, 1.7));
    }

    // A colour for each segment from the bottom up: low, through mid and hot (about -6 dB), to clip at 0 dB and over.
    private (SolidColorBrush[] Lit, SolidColorBrush[] Halo, SolidColorBrush[] Dim) Segments(int count)
    {
        if (_segments is { } cached && cached.Lit.Length == count)
        {
            return cached;
        }

        var lit = new SolidColorBrush[count];
        var halo = new SolidColorBrush[count];
        var dim = new SolidColorBrush[count];
        for (var i = 0; i < count; i++)
        {
            var color = SegmentColor((i + 0.5) / count);
            lit[i] = new SolidColorBrush(Palette.Lighten(color, 0.12));
            halo[i] = new SolidColorBrush(Color.FromArgb(95, color.R, color.G, color.B));
            dim[i] = new SolidColorBrush(Palette.Mix(Palette.MeterTrack, color, 0.3));
        }

        _segments = (lit, halo, dim);
        return _segments.Value;
    }

    private Color SegmentColor(double fraction)
    {
        var low = LevelLowColor;
        var mid = LevelMidColor;
        var hot = LevelHotColor;
        var clip = LevelClipColor;
        return fraction switch
        {
            < LowFraction => low,
            < MidFraction => Palette.Mix(low, mid, (fraction - LowFraction) / (MidFraction - LowFraction)),
            _ when fraction < HotFraction => Palette.Mix(mid, hot, (fraction - MidFraction) / (HotFraction - MidFraction)),
            _ when fraction < ZeroFraction => Palette.Mix(hot, clip, (fraction - HotFraction) / (ZeroFraction - HotFraction)),
            _ => clip,
        };
    }

    // Each frame: the lamp follows the notes (on at once, off over LampRelease), and the seam redraws only if what it
    // shows has changed, so idle seams cost nothing.
    private void OnFrame()
    {
        if (Signal is not { } signal)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = _lastFrame == 0 ? 0 : Stopwatch.GetElapsedTime(_lastFrame, now).TotalSeconds;
        _lastFrame = now;
        var release = Math.Max(LampRelease.TotalSeconds, 0.001);
        _glow = signal.NotesHeld ? 1 : Math.Max(0, _glow - (elapsed / release));
        var levels = signal.Levels;
        if (_drawn != (levels.IsActive, levels.PeakLeft, levels.PeakRight, levels.RmsLeft, levels.RmsRight, levels.HoldLeft, levels.HoldRight, _glow))
        {
            InvalidateVisual();
        }
    }

    private void Subscribe(JunctionSignal? signal)
    {
        if (ReferenceEquals(signal, _subscribed))
        {
            return;
        }

        if (_subscribed is { } old)
        {
            old.Levels.Updated -= _updated;
            old.PropertyChanged -= _changed;
        }

        _subscribed = signal;
        _glow = 0;
        _lastFrame = 0;
        if (signal is not null)
        {
            signal.Levels.Updated += _updated;
            signal.PropertyChanged += _changed;
        }
    }

    private void UpdateName() => AutomationProperties.SetName(this, Signal?.Label ?? "Signal");

    // For screen readers: a button named after the junction ("After Arpeggiator"), whose help text says what is there now,
    // and whose invoke aims the Browser at its gap, as Enter and the + do.
    private sealed class SeamPeer(JunctionSeam owner) : ControlAutomationPeer(owner), IInvokeProvider
    {
        public void Invoke() => owner.Run(owner.InsertCommand);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string? GetNameCore() => owner.Signal?.Label ?? base.GetNameCore();

        protected override string? GetHelpTextCore() => owner.Describe();

        protected override bool IsContentElementCore() => owner.Signal is not null;

        protected override bool IsControlElementCore() => owner.Signal is not null;
    }
}
