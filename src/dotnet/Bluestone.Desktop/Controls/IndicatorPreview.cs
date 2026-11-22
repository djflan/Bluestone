using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bluestone.Audio;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A still sample of one family of LED indicators, drawn by the real controls in their own gradient: mixer strip meters,
/// device-chain seams (with their MIDI light), or gain LED scales. Display settings stages these so a gradient or color
/// change shows as the app will draw it.
/// </summary>
public sealed class IndicatorPreview : Panel
{
    public static readonly StyledProperty<MeterGradientTarget> FamilyProperty =
        AvaloniaProperty.Register<IndicatorPreview, MeterGradientTarget>(nameof(Family));

    /// <summary>How many indicators it shows side by side.</summary>
    public static readonly StyledProperty<int> CountProperty = AvaloniaProperty.Register<IndicatorPreview, int>(nameof(Count), 4);

    // Levels in dB (left, right, peak hold, clipped): a quiet-to-hot spread so most of a gradient shows, one strip clipping.
    private static readonly (double Left, double Right, double Hold, bool Clip)[] Levels =
        [(-5, -7, -2, false), (-14, -12, -9, false), (0.4, -0.8, 1.2, true), (-24, -26, -20, false)];

    private static readonly double[] Gains = [0, -6, 3, -18];

    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

    static IndicatorPreview()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<IndicatorPreview>(false);
    }

    public IndicatorPreview()
    {
        Children.Add(_row);
        Build();
    }

    public MeterGradientTarget Family { get => GetValue(FamilyProperty); set => SetValue(FamilyProperty, value); }

    public int Count { get => GetValue(CountProperty); set => SetValue(CountProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FamilyProperty || change.Property == CountProperty)
        {
            Build();
        }
    }

    private void Build()
    {
        _row.Children.Clear();
        var count = Math.Clamp(Count, 1, Levels.Length);
        _row.Spacing = Family switch { MeterGradientTarget.Chain => 2, MeterGradientTarget.Gain => 8, _ => 4 };
        for (var i = 0; i < count; i++)
        {
            var (left, right, hold, clip) = Levels[i];
            _row.Children.Add(Family switch
            {
                MeterGradientTarget.Chain => new ChainSeamPreview(left, right, clip, notesHeld: i != 1),
                MeterGradientTarget.Gain => new GainLedGutter { Value = Gains[i] },
                _ => new LedLadderMeter
                {
                    Mode = MixerMeterMode.PeakRms,
                    LeftValue = left,
                    RightValue = right,
                    LeftPeakHold = hold,
                    RightPeakHold = hold - 1.5,
                    IsReady = true,
                    IsClipped = clip,
                    Width = 16,
                },
            });
        }
    }
}

/// <summary>
/// A device-chain seam as <see cref="ChainJunction"/> draws one, still: the MIDI light (lit in the MIDI color while notes
/// are held), the level ladder in the chain gradient, and the flow chevron under it.
/// </summary>
internal sealed class ChainSeamPreview : Control
{
    private const double IconY = 14;
    private const double IconSize = 15;
    private const double LadderTop = 44;
    private const double LadderWidth = 14;
    private const double FootHeight = 18;

    private static readonly StreamGeometry MidiPort = StreamGeometry.Parse("M8,2 A6,6 0 1 1 7.99,2 Z M4.8,8.3 H4.9 M5.8,5.6 H5.9 M8,4.6 H8.1 M10.2,5.6 H10.3 M11.2,8.3 H11.3 M7,12 H9");

    private readonly LedLadderMeter _ladder;
    private readonly bool _notesHeld;

    public ChainSeamPreview(double left, double right, bool clipped, bool notesHeld)
    {
        _notesHeld = notesHeld;
        _ladder = new LedLadderMeter
        {
            Mode = MixerMeterMode.PeakRms,
            MeterWidth = LadderWidth,
            ColumnWidth = 4,
            GradientTarget = MeterGradientTarget.Chain,
            LeftValue = left,
            RightValue = right,
            LeftPeakHold = left + 3,
            RightPeakHold = right + 3,
            IsReady = true,
            IsClipped = clipped,
        };
        VisualChildren.Add(_ladder);
        LogicalChildren.Add(_ladder);
        Width = 30;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LedColours.Changed += OnMidiChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LedColours.Changed -= OnMidiChanged;
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _ladder.Measure(Size.Infinity);
        return new Size(30, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _ladder.Arrange(new Rect(Math.Round((finalSize.Width - LadderWidth) / 2), LadderTop - ConsoleRail.MeterInset, LadderWidth,
            Math.Max(0, finalSize.Height - LadderTop - FootHeight + (2 * ConsoleRail.MeterInset))));
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        var center = Bounds.Width / 2;
        var icon = new Point(center, IconY);
        var midi = LedColours.Midi;
        if (_notesHeld)
        {
            var bloom = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(ConsolePaint.Alpha(midi, Palette.GlowAlpha * 1.1), 0),
                    new GradientStop(ConsolePaint.Alpha(midi, Palette.GlowAlpha * 1.1 * 0.35), 0.45),
                    new GradientStop(ConsolePaint.Alpha(midi, 0), 1),
                },
            }.ToImmutable();
            context.DrawEllipse(bloom, null, icon, IconSize * 1.1, IconSize * 1.1);
        }

        var pen = _notesHeld
            ? LedColours.MidiPen(LedLevels.Steps)
            : ConsolePaint.Pen(ConsolePaint.Alpha(Palette.TextMuted, 0.75), 1.3, PenLineCap.Round);
        var k = IconSize / 16;
        using (context.PushTransform(Matrix.CreateScale(k, k) * Matrix.CreateTranslation(icon.X - (8 * k), icon.Y - (7.5 * k))))
        {
            context.DrawGeometry(null, pen, MidiPort);
        }

        var caption = new FormattedText("MIDI", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            ConsolePaint.Typeface("Font.Ui", FontWeight.SemiBold), 7.5,
            _notesHeld ? LedColours.MidiCaptionBrush : ConsolePaint.Brush(Palette.TextMuted));
        context.DrawText(caption, new Point(center - Math.Round(caption.Width / 2), IconY + 10));

        var cy = Bounds.Height - FootHeight + 9;
        var chevron = ConsolePaint.Pen(Palette.TextMuted, 1.2, PenLineCap.Round);
        context.DrawLine(chevron, new Point(center - 3, cy - 4), new Point(center + 2, cy));
        context.DrawLine(chevron, new Point(center + 2, cy), new Point(center - 3, cy + 4));
    }

    private void OnMidiChanged(object? sender, EventArgs e) => InvalidateVisual();
}
