using System.ComponentModel;
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
using Bluestone.Audio;
using Bluestone.Desktop.Theme;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The R6 junction between two devices (ADR 0037): the MIDI port glyph at the top, lit aqua with a soft bloom while notes
/// are held and dim otherwise, a "MIDI" caption under it, and the signal there as a stereo <see cref="LedLadderMeter"/> in
/// its dark well down the rest of the seam, with a flow chevron under it. Hovered or focused, the chevron becomes a +
/// that aims the Browser at this gap (Enter, Space or the menu do too); a point also routed elsewhere shows an aqua tap
/// between the caption and the ladder, which opens its connections. The lamp follows the notes (on at once, fading out
/// over <see cref="LampRelease"/> on <see cref="LedMotion"/>); the seam and its meter redraw only when what they show changes.
/// </summary>
public sealed class ChainJunction : Control, ICustomHitTest
{
    public static readonly StyledProperty<JunctionSignal?> SignalProperty = AvaloniaProperty.Register<ChainJunction, JunctionSignal?>(nameof(Signal));

    /// <summary>The device after this point is bypassed: the seam is drawn quieter.</summary>
    public static readonly StyledProperty<bool> IsQuietProperty = AvaloniaProperty.Register<ChainJunction, bool>(nameof(IsQuiet));

    public static readonly StyledProperty<ICommand?> InsertCommandProperty = AvaloniaProperty.Register<ChainJunction, ICommand?>(nameof(InsertCommand));

    public static readonly StyledProperty<ICommand?> RouteCommandProperty = AvaloniaProperty.Register<ChainJunction, ICommand?>(nameof(RouteCommand));

    /// <summary>A caption under the ladder in place of the chevron ("In" at the chain's start).</summary>
    public static readonly StyledProperty<string?> CaptionProperty = AvaloniaProperty.Register<ChainJunction, string?>(nameof(Caption));

    /// <summary>A bare gap (before the Out card): no port, caption or ladder, only the insertion + and the routed tap.</summary>
    public static readonly StyledProperty<bool> IsCompactProperty = AvaloniaProperty.Register<ChainJunction, bool>(nameof(IsCompact));

    public static readonly StyledProperty<TimeSpan> LampReleaseProperty = AvaloniaProperty.Register<ChainJunction, TimeSpan>(nameof(LampRelease), TimeSpan.FromMilliseconds(140));

    /// <summary>What the ladder shows (Settings ▸ Display): the average level with the peak held, or the peak alone.</summary>
    public static readonly StyledProperty<JunctionLevelStyle> LevelStyleProperty = AvaloniaProperty.Register<ChainJunction, JunctionLevelStyle>(nameof(LevelStyle));

    internal const double IconY = 14;
    internal const double IconSize = 15;
    internal const double LadderTop = 44;
    internal const double LadderWidth = 14;
    internal const double FootHeight = 18;
    private const double PlusSize = 14;
    private const double RoutedSize = 7;

    private static readonly StreamGeometry MidiPort = StreamGeometry.Parse("M8,2 A6,6 0 1 1 7.99,2 Z M4.8,8.3 H4.9 M5.8,5.6 H5.9 M8,4.6 H8.1 M10.2,5.6 H10.3 M11.2,8.3 H11.3 M7,12 H9");

    private readonly LedLadderMeter _ladder = new() { Mode = MixerMeterMode.PeakRms, MeterWidth = LadderWidth, ColumnWidth = 4, GradientTarget = MeterGradientTarget.Chain, IsHitTestVisible = false };
    private readonly EventHandler _updated;
    private readonly PropertyChangedEventHandler _changed;
    private JunctionSignal? _subscribed;
    private readonly LedLevels _midi;
    private (bool, double, double, double, double, double, double, bool, bool) _described;
    private FormattedText? _midiCaption;
    private FormattedText? _midiCaptionLit;
    private FormattedText? _caption;
    private Cursor? _hand;
    private bool _hovered;
    private Part _over;

    static ChainJunction()
    {
        AffectsRender<ChainJunction>(SignalProperty, IsQuietProperty, InsertCommandProperty, CaptionProperty, IsCompactProperty);
        AffectsMeasure<ChainJunction>(IsCompactProperty);
        FocusableProperty.OverrideDefaultValue<ChainJunction>(false);
        ClipToBoundsProperty.OverrideDefaultValue<ChainJunction>(false);
    }

    public ChainJunction()
    {
        _updated = (_, _) => OnFrame();
        _midi = new LedLevels(this, LampRelease.TotalMilliseconds, count: 1);
        _changed = (_, e) =>
        {
            if (e.PropertyName is nameof(JunctionSignal.Label) or nameof(JunctionSignal.CanCarryNotes) or nameof(JunctionSignal.CanCarryAudio)
                or nameof(JunctionSignal.Routes) or nameof(JunctionSignal.IsInsertionPoint) or nameof(JunctionSignal.NotesHeld))
            {
                UpdateName();
                OnFrame();
                InvalidateVisual();
            }
        };
        LogicalChildren.Add(_ladder);
        VisualChildren.Add(_ladder);
        var insert = new MenuItem { Header = "Insert Device Here" };
        insert.Click += (_, _) => Run(InsertCommand);
        var route = new MenuItem { Header = "Route from Here…" };
        route.Click += (_, _) => Run(RouteCommand);
        var clear = new MenuItem { Header = "Clear Clip", InputGesture = new KeyGesture(Key.Delete) };
        clear.Click += (_, _) => ClearClip();
        ContextMenu = new ContextMenu { Items = { insert, route, new Separator(), clear } };
        ContextMenu.Opening += (_, _) =>
        {
            insert.IsEnabled = InsertCommand?.CanExecute(Signal) == true;
            route.IsEnabled = RouteCommand?.CanExecute(Signal) == true;
            clear.IsEnabled = Signal?.Levels.Clipped == true;
        };
    }

    private enum Part
    {
        None,
        Plus,
        Routed,
        Clip,
    }

    public JunctionSignal? Signal { get => GetValue(SignalProperty); set => SetValue(SignalProperty, value); }

    public bool IsQuiet { get => GetValue(IsQuietProperty); set => SetValue(IsQuietProperty, value); }

    public ICommand? InsertCommand { get => GetValue(InsertCommandProperty); set => SetValue(InsertCommandProperty, value); }

    public ICommand? RouteCommand { get => GetValue(RouteCommandProperty); set => SetValue(RouteCommandProperty, value); }

    public string? Caption { get => GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    public bool IsCompact { get => GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    public TimeSpan LampRelease { get => GetValue(LampReleaseProperty); set => SetValue(LampReleaseProperty, value); }

    public JunctionLevelStyle LevelStyle { get => GetValue(LevelStyleProperty); set => SetValue(LevelStyleProperty, value); }

    /// <summary>The ladder showing the signal here.</summary>
    internal LedLadderMeter Ladder => _ladder;

    /// <summary>How lit the MIDI glyph is now: 1 while notes are held, falling to 0 over <see cref="LampRelease"/>.</summary>
    internal double LampGlow => _midi.Level(0);

    /// <summary>Where the + is (drawn only while hovered or focused) and where the routed tap is, in the seam's coordinates.</summary>
    internal (Rect Plus, Rect Routed) Targets => (PlusRect, RoutedRect);

    internal bool ShowsPlus => InsertCommand is not null && Signal is not null && (_hovered || IsKeyboardFocusWithin);

    /// <summary>What the seam carries now, in words, for its tooltip and screen readers.</summary>
    internal string Describe() => Signal is not { } signal ? string.Empty
        : SignalDescription.Describe(signal.Label, signal.CanCarryNotes, signal.NotesHeld, signal.CanCarryAudio, signal.Levels, signal.IsRouted ? signal.Routes : null)
            + (signal.CanCarryAudio && signal.Levels.Clipped ? "; clip latched (Delete clears it)" : string.Empty);

    /// <summary>Clears a latched clip; true if there was one.</summary>
    internal bool ClearClip()
    {
        if (Signal is not { } signal || !signal.Levels.Clipped)
        {
            return false;
        }

        signal.Levels.ResetClip();
        OnFrame();
        return true;
    }

    private double Center => Math.Round(Bounds.Width / 2);

    private Rect LadderRect => new(Center - (LadderWidth / 2), LadderTop, LadderWidth, Math.Max(0, Bounds.Height - LadderTop - FootHeight));

    private Rect PlusRect => IsCompact
        ? new(Center - (PlusSize / 2), Math.Round((Bounds.Height / 2) - (PlusSize / 2)), PlusSize, PlusSize)
        : new(Center - (PlusSize / 2), Math.Round(LadderRect.Bottom + (FootHeight / 2) - (PlusSize / 2)), PlusSize, PlusSize);

    private Rect RoutedRect => IsCompact
        ? new(Center - (RoutedSize / 2), Math.Round((Bounds.Height / 2) - PlusSize - RoutedSize), RoutedSize, RoutedSize)
        : new(Center - (RoutedSize / 2), LadderTop - RoutedSize - 1, RoutedSize, RoutedSize);

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    protected override AutomationPeer OnCreateAutomationPeer() => new JunctionPeer(this);

    protected override Size MeasureOverride(Size availableSize)
    {
        _ladder.Measure(Size.Infinity);
        return new Size(IsCompact ? 16 : 30, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The meter insets its well by MeterInset top and bottom; give it that much more so the well fills the ladder span.
        var ladder = new Rect(Math.Round((finalSize.Width - LadderWidth) / 2), LadderTop - ConsoleRail.MeterInset, LadderWidth,
            Math.Max(0, finalSize.Height - LadderTop - FootHeight + (2 * ConsoleRail.MeterInset)));
        _ladder.Arrange(ladder);
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SignalProperty)
        {
            // Without a junction (the trailing wire of an empty chain) the seam is only drawing: not a tab stop, no menu.
            Focusable = Signal is not null;
            ContextMenu!.IsEnabled = Signal is not null;
            Subscribe(VisualRoot is not null ? Signal : null);
            UpdateName();
            OnFrame();
        }
        else if (change.Property == IsCompactProperty)
        {
            _ladder.IsVisible = !IsCompact;
        }
        else if (change.Property == IsQuietProperty)
        {
            _ladder.Opacity = IsQuiet ? 0.55 : 1;
        }
        else if (change.Property == LevelStyleProperty)
        {
            OnFrame();
        }
        else if (change.Property == LampReleaseProperty)
        {
            _midi.FadeMs = LampRelease.TotalMilliseconds;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LedColours.Changed += OnLedColoursChanged;
        Glow.Changed += OnLedColoursChanged;
        OnLedColoursChanged(this, EventArgs.Empty);
        Subscribe(Signal);
        UpdateName();
        OnFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Subscribe(null);
        LedColours.Changed -= OnLedColoursChanged;
        Glow.Changed -= OnLedColoursChanged;
        base.OnDetachedFromVisualTree(e);
    }

    // The MIDI LED's lit caption is built from its colour once (its bloom comes from Glow); a new colour or glow redraws.
    private void OnLedColoursChanged(object? sender, EventArgs e)
    {
        _midiCaptionLit = null;
        InvalidateVisual();
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
                Part.Clip => "Clipped: click to clear",
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
            case Part.Clip:
                ClearClip();
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
        else if (e.Key is Key.Delete or Key.Back && e.KeyModifiers == KeyModifiers.None && ClearClip())
        {
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

    private Part PartAt(Point point)
    {
        if (Signal is not { } signal)
        {
            return Part.None;
        }

        if (signal.IsRouted && RoutedRect.Inflate(4).Contains(point))
        {
            return Part.Routed;
        }

        if (ShowsPlus && PlusRect.Inflate(3).Contains(point))
        {
            return Part.Plus;
        }

        // A latched clip clears with a click on the ladder, as on a console's peak lamp.
        return signal.Levels.Clipped && LadderRect.Inflate(3).Contains(point) ? Part.Clip : Part.None;
    }

    public override void Render(DrawingContext context)
    {
        var center = Center;
        var signal = Signal;
        if (IsFocused && Classes.Contains(":focus-visible"))
        {
            // Keyboard focus: a solid outline around the junction, as on the square keys; never a glow.
            context.DrawRectangle(null, ConsolePaint.Pen(Palette.Focus, 1.5), new RoundedRect(new Rect(Bounds.Size).Deflate(1), 4));
        }

        if (signal is { IsInsertionPoint: true })
        {
            // The gap the Browser's next insert goes into, marked as a drop's insertion marker marks it.
            context.DrawRectangle(ConsolePaint.Brush(ConsolePaint.Alpha(Palette.Accent, 0.85)), null, new Rect(center - 1.5, 0, 3, Bounds.Height), 1.5, 1.5);
        }

        if (signal is null)
        {
            return;
        }

        if (IsCompact)
        {
            DrawTapAndPlus(context, signal);
            return;
        }

        // The MIDI port: lit with a soft bloom while notes are held, dim when idle, faint where notes cannot pass.
        var icon = new Point(center, IconY);
        var step = signal.CanCarryNotes ? _midi.Step(0) : 0;
        var glow = step / (double)LedLevels.Steps;
        if (glow > 0.01)
        {
            if (Glow.Bloom(LedColours.Midi, Palette.GlowAlpha * 1.1) is { } bloom)
            {
                using (context.PushOpacity(glow))
                {
                    context.DrawEllipse(bloom, null, icon, IconSize * 1.1, IconSize * 1.1);
                }
            }
        }

        var pen = glow > 0.01
            ? LedColours.MidiPen(step)
            : ConsolePaint.Pen(ConsolePaint.Alpha(Palette.TextMuted, signal.CanCarryNotes ? 0.75 : 0.35), 1.3, PenLineCap.Round);
        var k = IconSize / 16;
        using (context.PushTransform(Matrix.CreateScale(k, k) * Matrix.CreateTranslation(icon.X - (8 * k), icon.Y - (7.5 * k))))
        {
            context.DrawGeometry(null, pen, MidiPort);
        }

        var lit = glow > 0.5;
        var caption = lit
            ? _midiCaptionLit ??= Text("MIDI", LedColours.MidiCaptionBrush, 7.5)
            : _midiCaption ??= Text("MIDI", Palette.TextMuted, 7.5);
        context.DrawText(caption, new Point(center - Math.Round(caption.Width / 2), IconY + 10));

        DrawTapAndPlus(context, signal);
        var ladder = LadderRect;
        if (ShowsPlus)
        {
            // The + (drawn with the tap) takes the chevron's place.
            return;
        }

        if (Caption is { Length: > 0 } text)
        {
            _caption ??= Text(text.ToUpperInvariant(), Palette.TextMuted, 8);
            context.DrawText(_caption, new Point(center - Math.Round(_caption.Width / 2), ladder.Bottom + 5));
        }
        else
        {
            // The flow chevron: the signal goes on to the right.
            var cy = ladder.Bottom + 9;
            var chevron = ConsolePaint.Pen(Palette.TextMuted, 1.2, PenLineCap.Round);
            context.DrawLine(chevron, new Point(center - 3, cy - 4), new Point(center + 2, cy));
            context.DrawLine(chevron, new Point(center + 2, cy), new Point(center - 3, cy + 4));
        }
    }

    private void DrawTapAndPlus(DrawingContext context, JunctionSignal signal)
    {
        // The routed tap: the signal is also taken from here.
        if (signal.IsRouted)
        {
            var mark = RoutedRect;
            context.DrawEllipse(ConsolePaint.Brush(Palette.Well), null, mark.Center, (mark.Width / 2) + 1.5, (mark.Height / 2) + 1.5);
            context.DrawEllipse(ConsolePaint.Brush(Palette.Accent), null, mark.Center, mark.Width / 2, mark.Height / 2);
            if (_over == Part.Routed)
            {
                context.DrawEllipse(null, ConsolePaint.Pen(Palette.Accent), mark.Center, (mark.Width / 2) + 2.5, (mark.Height / 2) + 2.5);
            }
        }

        if (ShowsPlus)
        {
            var plus = PlusRect;
            var hot = _over == Part.Plus;
            context.DrawEllipse(ConsolePaint.Brush(hot ? Palette.Accent : Palette.Key), hot ? null : ConsolePaint.Pen(ConsolePaint.Alpha(ConsolePaint.Black, 0.55)), plus.Center, plus.Width / 2, plus.Height / 2);
            var pen = ConsolePaint.Pen(hot ? Palette.Well : Palette.Text, 1.25, PenLineCap.Round);
            context.DrawLine(pen, new Point(plus.Center.X - 3.5, plus.Center.Y), new Point(plus.Center.X + 3.5, plus.Center.Y));
            context.DrawLine(pen, new Point(plus.Center.X, plus.Center.Y - 3.5), new Point(plus.Center.X, plus.Center.Y + 3.5));
        }
    }

    private static FormattedText Text(string text, Color color, double size) => Text(text, ConsolePaint.Brush(color), size);

    private static FormattedText Text(string text, IBrush brush, double size) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ConsolePaint.Typeface("Font.Ui", FontWeight.SemiBold), size, brush);

    // Each frame: the lamp follows the notes (on at once, fading over LampRelease on LedMotion's clock) and the ladder takes the levels. Nothing is
    // invalidated unless what is shown has changed, so idle seams cost nothing.
    private void OnFrame()
    {
        if (Signal is not { } signal)
        {
            _ladder.IsReady = false;
            return;
        }

        var drawn = _midi.Step(0);
        _midi.Set(0, signal.NotesHeld, LedMotion.Now);
        if (_midi.Step(0) != drawn)
        {
            InvalidateVisual();
        }

        var levels = signal.Levels;
        var ready = signal.CanCarryAudio && levels.IsActive;
        _ladder.IsReady = ready;
        if (ready)
        {
            LadderLevels.Show(_ladder, levels, LevelStyle);
        }

        _ladder.IsClipped = signal.CanCarryAudio && levels.Clipped;

        // Under the pointer the description follows what is there, but is rebuilt only when that changes (as on main), so
        // a steady signal costs nothing while hovered.
        if (_hovered && _over == Part.None)
        {
            var shown = (ready, levels.PeakLeft, levels.PeakRight, levels.RmsLeft, levels.RmsRight, levels.HoldLeft, levels.HoldRight, levels.Clipped, signal.NotesHeld);
            if (shown != _described)
            {
                _described = shown;
                ToolTip.SetTip(this, Describe());
            }
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
        _midi.Set(0, false, 0, instant: true);
        if (signal is not null)
        {
            signal.Levels.Updated += _updated;
            signal.PropertyChanged += _changed;
        }
    }

    private void UpdateName() => AutomationProperties.SetName(this, Signal?.Label ?? "Signal");

    // For screen readers: a button named after the junction ("After Arpeggiator"), whose help text says what is there now,
    // and whose invoke aims the Browser at its gap, as Enter and the + do.
    private sealed class JunctionPeer(ChainJunction owner) : ControlAutomationPeer(owner), IInvokeProvider
    {
        public void Invoke() => owner.Run(owner.InsertCommand);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string? GetNameCore() => owner.Signal?.Label ?? base.GetNameCore();

        protected override string? GetHelpTextCore() => owner.Describe();

        protected override bool IsContentElementCore() => owner.Signal is not null;

        protected override bool IsControlElementCore() => owner.Signal is not null;
    }
}
