using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Bluestone.Desktop.Theme;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A signal indicator on its own: a segmented stereo level and a MIDI lamp, at the junction's Compact size, or Medium (2 dB
/// segments and a 0 dB tick) or Large (1 dB segments, a dB scale, a held-peak readout, and a clip light that stays on until
/// clicked or cleared with Enter). It sizes itself to its variant, draws with <see cref="SignalIndicatorRenderer"/>, and
/// redraws only when what it shows changes. Its metrics and colours come from the theme (Signal.* tokens).
/// </summary>
internal sealed class SignalIndicator : Control
{
    public static readonly StyledProperty<SignalIndicatorSize> VariantProperty = AvaloniaProperty.Register<SignalIndicator, SignalIndicatorSize>(nameof(Variant));

    public static readonly StyledProperty<MeterLevels?> LevelsProperty = AvaloniaProperty.Register<SignalIndicator, MeterLevels?>(nameof(Levels));

    public static readonly StyledProperty<bool> NotesHeldProperty = AvaloniaProperty.Register<SignalIndicator, bool>(nameof(NotesHeld));

    public static readonly StyledProperty<bool> CarriesNotesProperty = AvaloniaProperty.Register<SignalIndicator, bool>(nameof(CarriesNotes));

    public static readonly StyledProperty<bool> CarriesAudioProperty = AvaloniaProperty.Register<SignalIndicator, bool>(nameof(CarriesAudio), true);

    public static readonly StyledProperty<JunctionLevelStyle> LevelStyleProperty = AvaloniaProperty.Register<SignalIndicator, JunctionLevelStyle>(nameof(LevelStyle));

    public static readonly StyledProperty<JunctionLampPlacement> LampPlacementProperty = AvaloniaProperty.Register<SignalIndicator, JunctionLampPlacement>(nameof(LampPlacement), JunctionLampPlacement.BelowLevel);

    public static readonly StyledProperty<LedShape> LedShapeProperty = AvaloniaProperty.Register<SignalIndicator, LedShape>(nameof(LedShape));

    public static readonly StyledProperty<TimeSpan> LampReleaseProperty = AvaloniaProperty.Register<SignalIndicator, TimeSpan>(nameof(LampRelease), TimeSpan.FromMilliseconds(220));

    public static readonly StyledProperty<Color> LampColorProperty = AvaloniaProperty.Register<SignalIndicator, Color>(nameof(LampColor), Palette.JunctionLamp);

    public static readonly StyledProperty<Color> LevelLowColorProperty = AvaloniaProperty.Register<SignalIndicator, Color>(nameof(LevelLowColor), Palette.MeterLow);

    public static readonly StyledProperty<Color> LevelMidColorProperty = AvaloniaProperty.Register<SignalIndicator, Color>(nameof(LevelMidColor), Palette.MeterMid);

    public static readonly StyledProperty<Color> LevelHotColorProperty = AvaloniaProperty.Register<SignalIndicator, Color>(nameof(LevelHotColor), Palette.MeterHot);

    public static readonly StyledProperty<Color> LevelClipColorProperty = AvaloniaProperty.Register<SignalIndicator, Color>(nameof(LevelClipColor), Palette.MeterClip);

    public static readonly StyledProperty<Color> TextColorProperty = AvaloniaProperty.Register<SignalIndicator, Color>(nameof(TextColor), Palette.TextSecondary);

    public static readonly StyledProperty<double> LevelHeightProperty = AvaloniaProperty.Register<SignalIndicator, double>(nameof(LevelHeight), 52);

    public static readonly StyledProperty<double> BarWidthProperty = AvaloniaProperty.Register<SignalIndicator, double>(nameof(BarWidth), 5);

    public static readonly StyledProperty<double> BarGapProperty = AvaloniaProperty.Register<SignalIndicator, double>(nameof(BarGap), 1);

    public static readonly StyledProperty<double> SegmentHeightProperty = AvaloniaProperty.Register<SignalIndicator, double>(nameof(SegmentHeight), 1);

    public static readonly StyledProperty<double> SegmentGapProperty = AvaloniaProperty.Register<SignalIndicator, double>(nameof(SegmentGap), 1);

    public static readonly StyledProperty<double> LampHeightProperty = AvaloniaProperty.Register<SignalIndicator, double>(nameof(LampHeight), 11);

    public static readonly StyledProperty<double> LampGapProperty = AvaloniaProperty.Register<SignalIndicator, double>(nameof(LampGap), 7);

    public static readonly StyledProperty<double> TickLengthProperty = AvaloniaProperty.Register<SignalIndicator, double>(nameof(TickLength));

    public static readonly StyledProperty<double> ScaleGapProperty = AvaloniaProperty.Register<SignalIndicator, double>(nameof(ScaleGap));

    public static readonly StyledProperty<double> ScaleWidthProperty = AvaloniaProperty.Register<SignalIndicator, double>(nameof(ScaleWidth));

    public static readonly StyledProperty<double> ClipLatchHeightProperty = AvaloniaProperty.Register<SignalIndicator, double>(nameof(ClipLatchHeight));

    public static readonly StyledProperty<double> ReadoutGapProperty = AvaloniaProperty.Register<SignalIndicator, double>(nameof(ReadoutGap));

    public static readonly StyledProperty<IBrush?> FocusRingBrushProperty = AvaloniaProperty.Register<SignalIndicator, IBrush?>(nameof(FocusRingBrush));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner<SignalIndicator>();

    public static readonly StyledProperty<double> FontSizeProperty = TextElement.FontSizeProperty.AddOwner<SignalIndicator>();

    /// <summary>How often the readout may change: often enough to follow, slow enough to read.</summary>
    private static readonly TimeSpan ReadoutInterval = TimeSpan.FromMilliseconds(250);

    // What an indicator without levels shows: nothing playing.
    private static readonly MeterLevels Idle = new();

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    // The focus ring sits this far outside what the indicator draws, with these rounded corners.
    private const double FocusRingOffset = 2;
    private const double FocusRingRadius = 3;

    private readonly SignalIndicatorRenderer _renderer = new();
    private readonly EventHandler _updated;
    private MeterLevels? _subscribed;
    private double _glow;
    private long _lastFrame;
    private int _readout = SignalIndicatorRenderer.NoReadout;
    private long _readoutAt;
    private SignalDisplayState? _drawn;
    private bool _hovered;
    private TipKey? _tip;
    private Pen? _focusPen;

    static SignalIndicator()
    {
        AffectsMeasure<SignalIndicator>(VariantProperty, LampPlacementProperty, LedShapeProperty, LevelHeightProperty, BarWidthProperty, BarGapProperty, SegmentHeightProperty, SegmentGapProperty,
            LampHeightProperty, LampGapProperty, TickLengthProperty, ScaleGapProperty, ScaleWidthProperty, ClipLatchHeightProperty, ReadoutGapProperty, FontFamilyProperty, FontSizeProperty);
        AffectsRender<SignalIndicator>(LevelsProperty, CarriesNotesProperty, CarriesAudioProperty, LevelStyleProperty, LampColorProperty, LevelLowColorProperty, LevelMidColorProperty,
            LevelHotColorProperty, LevelClipColorProperty, TextColorProperty, FocusRingBrushProperty);
        FocusableProperty.OverrideDefaultValue<SignalIndicator>(false);
    }

    public SignalIndicator()
    {
        _updated = (_, _) => OnFrame();
    }

    /// <summary>Compact (the junction's), Medium, or Large.</summary>
    public SignalIndicatorSize Variant
    {
        get => GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    /// <summary>The levels shown; without them the level rests, as when nothing is playing.</summary>
    public MeterLevels? Levels
    {
        get => GetValue(LevelsProperty);
        set => SetValue(LevelsProperty, value);
    }

    /// <summary>Notes are held now: the lamp glows, and lets go over <see cref="LampRelease"/> after they stop.</summary>
    public bool NotesHeld
    {
        get => GetValue(NotesHeldProperty);
        set => SetValue(NotesHeldProperty, value);
    }

    /// <summary>Notes can reach this point: the MIDI lamp is shown.</summary>
    public bool CarriesNotes
    {
        get => GetValue(CarriesNotesProperty);
        set => SetValue(CarriesNotesProperty, value);
    }

    /// <summary>Audio can be present here: the level is shown.</summary>
    public bool CarriesAudio
    {
        get => GetValue(CarriesAudioProperty);
        set => SetValue(CarriesAudioProperty, value);
    }

    /// <summary>What the level shows: the average with the peak held above it, or the peak.</summary>
    public JunctionLevelStyle LevelStyle
    {
        get => GetValue(LevelStyleProperty);
        set => SetValue(LevelStyleProperty, value);
    }

    /// <summary>Whether the lamp sits over the level or under it (Header is taken as over: there is no header here).</summary>
    public JunctionLampPlacement LampPlacement
    {
        get => GetValue(LampPlacementProperty);
        set => SetValue(LampPlacementProperty, value);
    }

    /// <summary>Segments or round dots, at Medium and Large; Compact always has segments.</summary>
    public LedShape LedShape
    {
        get => GetValue(LedShapeProperty);
        set => SetValue(LedShapeProperty, value);
    }

    public TimeSpan LampRelease
    {
        get => GetValue(LampReleaseProperty);
        set => SetValue(LampReleaseProperty, value);
    }

    public Color LampColor
    {
        get => GetValue(LampColorProperty);
        set => SetValue(LampColorProperty, value);
    }

    public Color LevelLowColor
    {
        get => GetValue(LevelLowColorProperty);
        set => SetValue(LevelLowColorProperty, value);
    }

    public Color LevelMidColor
    {
        get => GetValue(LevelMidColorProperty);
        set => SetValue(LevelMidColorProperty, value);
    }

    public Color LevelHotColor
    {
        get => GetValue(LevelHotColorProperty);
        set => SetValue(LevelHotColorProperty, value);
    }

    public Color LevelClipColor
    {
        get => GetValue(LevelClipColorProperty);
        set => SetValue(LevelClipColorProperty, value);
    }

    /// <summary>The colour of the scale's ticks and labels and of the readout below full scale.</summary>
    public Color TextColor
    {
        get => GetValue(TextColorProperty);
        set => SetValue(TextColorProperty, value);
    }

    /// <summary>The height the level's segments are fitted into.</summary>
    public double LevelHeight
    {
        get => GetValue(LevelHeightProperty);
        set => SetValue(LevelHeightProperty, value);
    }

    /// <summary>The width of each column (a dot's diameter, for round LEDs).</summary>
    public double BarWidth
    {
        get => GetValue(BarWidthProperty);
        set => SetValue(BarWidthProperty, value);
    }

    /// <summary>The space between the left and right columns.</summary>
    public double BarGap
    {
        get => GetValue(BarGapProperty);
        set => SetValue(BarGapProperty, value);
    }

    /// <summary>The height of a segment (a dot's diameter, for round LEDs).</summary>
    public double SegmentHeight
    {
        get => GetValue(SegmentHeightProperty);
        set => SetValue(SegmentHeightProperty, value);
    }

    /// <summary>The space between segments.</summary>
    public double SegmentGap
    {
        get => GetValue(SegmentGapProperty);
        set => SetValue(SegmentGapProperty, value);
    }

    public double LampHeight
    {
        get => GetValue(LampHeightProperty);
        set => SetValue(LampHeightProperty, value);
    }

    /// <summary>The space between the lamp and the level.</summary>
    public double LampGap
    {
        get => GetValue(LampGapProperty);
        set => SetValue(LampGapProperty, value);
    }

    /// <summary>The length of a scale tick (Medium and Large).</summary>
    public double TickLength
    {
        get => GetValue(TickLengthProperty);
        set => SetValue(TickLengthProperty, value);
    }

    /// <summary>The space between the level and its ticks, and between the ticks and their labels.</summary>
    public double ScaleGap
    {
        get => GetValue(ScaleGapProperty);
        set => SetValue(ScaleGapProperty, value);
    }

    /// <summary>The width kept for the scale's labels (Large).</summary>
    public double ScaleWidth
    {
        get => GetValue(ScaleWidthProperty);
        set => SetValue(ScaleWidthProperty, value);
    }

    /// <summary>The clip light's height (Large); it is as wide as the level.</summary>
    public double ClipLatchHeight
    {
        get => GetValue(ClipLatchHeightProperty);
        set => SetValue(ClipLatchHeightProperty, value);
    }

    /// <summary>The space under the readout, and under the clip light (Large).</summary>
    public double ReadoutGap
    {
        get => GetValue(ReadoutGapProperty);
        set => SetValue(ReadoutGapProperty, value);
    }

    /// <summary>The ring drawn round it while it has keyboard focus (set by the theme on :focus-visible); none without it.</summary>
    public IBrush? FocusRingBrush
    {
        get => GetValue(FocusRingBrushProperty);
        set => SetValue(FocusRingBrushProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <summary>The level's shape as this variant draws it: Compact keeps segments whatever <see cref="LedShape"/> says.</summary>
    internal SignalLevelMetrics LevelMetrics => new(LevelHeight, BarWidth, BarGap, SegmentHeight, SegmentGap);

    internal LedShape EffectiveShape => Variant == SignalIndicatorSize.Compact ? LedShape.Bar : LedShape;

    /// <summary>What it shows now, quantised to what it draws.</summary>
    internal SignalDisplayState DisplayState
    {
        get
        {
            var large = Variant == SignalIndicatorSize.Large && CarriesAudio;
            var levels = CarriesAudio ? Levels ?? Idle : null;
            return SignalIndicatorRenderer.State(levels, LevelMetrics.Count, LevelStyle, CarriesNotes ? _glow : 0, large && levels is { Clipped: true }, large ? _readout : 0);
        }
    }

    /// <summary>What it carries now, in words, for its tooltip and screen readers.</summary>
    internal string Describe() => SignalIndicatorRenderer.Describe(
        null, CarriesNotes, NotesHeld, CarriesAudio, Levels, null, Actionable && Levels is { Clipped: true });

    /// <summary>Whether it has something to do: only a Large level has a clip light to clear.</summary>
    internal bool Actionable => Variant == SignalIndicatorSize.Large && CarriesAudio;

    /// <summary>Turns off the clip light (a Large level's): from a click, Enter, or Space. Whether there was one to turn off.</summary>
    internal bool ClearClip()
    {
        if (!Actionable || Levels is not { Clipped: true } levels)
        {
            return false;
        }

        levels.ResetClip();
        Refresh();
        return true;
    }

    public override void Render(DrawingContext context)
    {
        var layout = Arrange();
        var origin = new Point(Math.Round((Bounds.Width - layout.Size.Width) / 2), Math.Round((Bounds.Height - layout.Size.Height) / 2));
        var state = DisplayState;
        _drawn = state;
        _renderer.Colors = new SignalIndicatorColors(LevelLowColor, LevelMidColor, LevelHotColor, LevelClipColor, LampColor, TextColor);
        var metrics = LevelMetrics;
        var barsLeft = origin.X + layout.BarsLeft;
        var barsTop = origin.Y + layout.BarsTop;
        var bottom = barsTop + metrics.Height;

        if (CarriesNotes)
        {
            _renderer.DrawLamp(context, SignalIndicatorRenderer.LampBox(origin.X + layout.Center, origin.Y + layout.LampTop, LampHeight), state.Glow);
        }

        if (FocusRingBrush is { } ring)
        {
            // A crisp 1 px ring just outside what it draws, on the pixel grid; it does not change its size or blur the level.
            if (_focusPen is null || !ReferenceEquals(_focusPen.Brush, ring))
            {
                _focusPen = new Pen(ring, 1);
            }

            var box = new Rect(origin, layout.Size).Inflate(FocusRingOffset + 0.5);
            context.DrawRectangle(null, _focusPen, box, FocusRingRadius, FocusRingRadius);
        }

        // The scale belongs to the level: without audio there is neither.
        if (Variant != SignalIndicatorSize.Compact && CarriesAudio)
        {
            _renderer.DrawTick(context, barsLeft + metrics.Width + ScaleGap, bottom, metrics, 0, TickLength);
        }

        if (Variant == SignalIndicatorSize.Large && CarriesAudio)
        {
            for (var i = 1; i < SignalIndicatorRenderer.ScaleMarks.Length; i++)
            {
                _renderer.DrawTick(context, barsLeft + metrics.Width + ScaleGap, bottom, metrics, SignalIndicatorRenderer.ScaleMarks[i], TickLength);
            }

            _renderer.DrawScaleLabels(context, barsLeft + metrics.Width + ScaleGap + TickLength + ScaleGap, bottom, metrics, FontFamily, FontSize);
        }

        if (!CarriesAudio)
        {
            return;
        }

        if (Variant == SignalIndicatorSize.Compact)
        {
            _renderer.DrawCompactLevel(context, barsLeft, bottom, metrics, Levels ?? Idle, LevelStyle);
            return;
        }

        // Cached columns at the screen's scale, and at least twice the size so a scaled render (a 2x capture) stays sharp.
        _renderer.DrawLevel(context, new Point(barsLeft, barsTop), metrics, EffectiveShape, state, Math.Max(2, Math.Ceiling(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1)));
        if (Variant == SignalIndicatorSize.Large)
        {
            _renderer.DrawClipLatch(context, new Rect(barsLeft, origin.Y + layout.LatchTop, metrics.Width, ClipLatchHeight), state.ClipLatched);
            _renderer.DrawReadout(context, origin.X + layout.Center, origin.Y + layout.ReadoutTop, state.Readout, FontFamily, FontSize);
        }
    }

    protected override Size MeasureOverride(Size availableSize) => Arrange().Size;

    protected override AutomationPeer OnCreateAutomationPeer() => new IndicatorPeer(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == VariantProperty || change.Property == CarriesAudioProperty)
        {
            // Only a Large level has something to do (clearing its clip light), so only it is a tab stop.
            var actionable = Actionable;
            SetCurrentValue(FocusableProperty, actionable);
            SetCurrentValue(CursorProperty, actionable ? HandCursor : null);
            if (!actionable)
            {
                ToolTip.SetIsOpen(this, false);
            }

            // A Compact level, or no level at all, draws no cached columns: let them go (they are rebuilt if needed).
            if (Variant == SignalIndicatorSize.Compact || !CarriesAudio)
            {
                _renderer.ReleaseLevel();
            }
        }
        else if (change.Property == LevelsProperty)
        {
            Subscribe(VisualRoot is not null ? Levels : null);
            Resync();
        }
        else if (change.Property == NotesHeldProperty)
        {
            // The lamp lights at once; it lets go over LampRelease as the levels update, or at once without them.
            if (NotesHeld || _subscribed is null)
            {
                _glow = NotesHeld ? 1 : 0;
            }

            Refresh();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(Levels);
        Resync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Subscribe(null);

        // Out of a window it draws nothing, so its cached columns go; they are rebuilt when it is drawn again.
        _renderer.ReleaseLevel();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);

        // From the keyboard there is no hover, so the tooltip opens with focus: what it shows, and how to clear the light.
        if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional)
        {
            UpdateTip(force: true);
            ToolTip.SetIsOpen(this, true);
        }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        ToolTip.SetIsOpen(this, false);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hovered = true;
        UpdateTip(force: true);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = false;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && ClearClip())
        {
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Enter or Key.Space && e.KeyModifiers == KeyModifiers.None && ClearClip())
        {
            e.Handled = true;
        }
    }

    // Where everything sits, from the top-left of the indicator's natural size. The lamp, the level, and at Large the clip
    // light and the readout share one centre line; the level's slot is as wide as the widest of them, widened by whole
    // pixels on both sides so the level stays on the pixel grid. At Medium and Large the scale hangs off the level's right.
    private Layout Arrange()
    {
        var metrics = LevelMetrics;
        var large = Variant == SignalIndicatorSize.Large;
        var lampHeight = Math.Max(0, LampHeight);
        var widest = Math.Max(metrics.Width, lampHeight);
        if (large)
        {
            widest = Math.Max(widest, _renderer.ReadoutWidth(FontFamily, FontSize));
        }

        var slot = metrics.Width + (2 * Math.Ceiling((widest - metrics.Width) / 2));
        var barsLeft = (slot - metrics.Width) / 2;
        var width = slot;
        if (Variant != SignalIndicatorSize.Compact)
        {
            var scale = barsLeft + metrics.Width + ScaleGap + TickLength;
            if (large)
            {
                scale += ScaleGap + ScaleWidth;
            }

            width = Math.Max(width, scale);
        }

        var above = LampPlacement != JunctionLampPlacement.BelowLevel;
        var y = 0.0;
        var lampTop = 0.0;
        if (above)
        {
            y += lampHeight + LampGap;
        }

        var readoutTop = y;
        if (large)
        {
            y += _renderer.TextHeight(FontFamily, FontSize) + ReadoutGap;
        }

        var latchTop = y;
        if (large)
        {
            y += ClipLatchHeight + ReadoutGap;
        }

        var barsTop = y;
        y += metrics.Height;
        if (!above)
        {
            lampTop = y + LampGap;
            y = lampTop + lampHeight;
        }

        return new Layout(new Size(Math.Ceiling(width), Math.Ceiling(y)), barsLeft, barsTop, slot / 2, lampTop, latchTop, readoutTop);
    }

    // Each update: the lamp follows the notes (on at once, off over LampRelease), the readout follows the held peak (at
    // most four times a second), and the indicator redraws only if what it shows has changed, so an idle one costs nothing.
    private void OnFrame()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = _lastFrame == 0 ? 0 : Stopwatch.GetElapsedTime(_lastFrame, now).TotalSeconds;
        _lastFrame = now;
        var release = Math.Max(LampRelease.TotalSeconds, 0.001);
        _glow = NotesHeld ? 1 : Math.Max(0, _glow - (elapsed / release));
        var readout = SignalIndicatorRenderer.ReadoutValue(Levels);
        if (readout != _readout && (readout == SignalIndicatorRenderer.NoReadout || _readout == SignalIndicatorRenderer.NoReadout || Stopwatch.GetElapsedTime(_readoutAt, now) >= ReadoutInterval))
        {
            _readout = readout;
            _readoutAt = now;
        }

        Refresh();
    }

    private void Refresh()
    {
        // Under the pointer or opened from the keyboard, the description follows what is there now. Only then, and only
        // when its words would change, is it rebuilt, so an indicator nobody is reading costs nothing.
        if (_hovered || ToolTip.GetIsOpen(this))
        {
            UpdateTip(force: false);
        }

        if (_drawn != DisplayState)
        {
            InvalidateVisual();
        }
    }

    // What follows from the levels and the notes, taken afresh: the lamp (lit if notes are held, otherwise out, as there may
    // be no more updates to let it go by), and the readout. On new levels, and on coming back into a window.
    private void Resync()
    {
        _glow = NotesHeld ? 1 : 0;
        _readout = SignalIndicatorRenderer.ReadoutValue(Levels);
        Refresh();
    }

    private void UpdateTip(bool force)
    {
        var key = TipKey.Of(this);
        if (force || _tip != key)
        {
            _tip = key;
            ToolTip.SetTip(this, Describe());
        }
    }

    private void Subscribe(MeterLevels? levels)
    {
        if (ReferenceEquals(levels, _subscribed))
        {
            return;
        }

        if (_subscribed is { } old)
        {
            old.Updated -= _updated;
        }

        _subscribed = levels;
        _lastFrame = 0;
        if (levels is not null)
        {
            levels.Updated += _updated;
        }
    }

    // What the description's words depend on, to the tenth of a dB it shows, so it is rebuilt only when they change.
    private readonly record struct TipKey(bool CarriesNotes, bool NotesHeld, bool CarriesAudio, bool Active, long PeakTenths, bool Over, bool Latched)
    {
        public static TipKey Of(SignalIndicator owner)
        {
            var levels = owner.Levels;
            var peak = levels is null ? MeterLevels.FloorDecibels : Math.Max(levels.PeakLeft, levels.PeakRight);
            var active = levels is { IsActive: true } && peak > MeterLevels.FloorDecibels;
            return new TipKey(owner.CarriesNotes, owner.NotesHeld, owner.CarriesAudio, active, active ? (long)Math.Round(peak * 10, MidpointRounding.AwayFromZero) : 0, active && peak > 0, owner.Actionable && levels is { Clipped: true });
        }
    }

    private readonly record struct Layout(Size Size, double BarsLeft, double BarsTop, double Center, double LampTop, double LatchTop, double ReadoutTop);

    // For screen readers: a read-only group named "Signal level" (or its AutomationProperties.Name), whose help text says
    // what is there now. Nothing is announced as it changes; it is read when asked for.
    private sealed class IndicatorPeer(SignalIndicator owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string? GetNameCore() => AutomationProperties.GetName(owner) is { Length: > 0 } name ? name : "Signal level";

        protected override string? GetHelpTextCore() => owner.Describe();
    }
}
