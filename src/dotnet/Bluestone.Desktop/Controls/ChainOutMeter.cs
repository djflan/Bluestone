using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Bluestone.Audio;
using Bluestone.Desktop.Theme;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The Out card's body: what leaves the chain, as a wide stereo <see cref="LedLadderMeter"/> with its dB scale beside it
/// and the held peak in a recessed well under both. It follows a <see cref="JunctionSignal"/>'s levels and redraws only
/// when they change.
/// </summary>
public sealed class ChainOutMeter : Control
{
    public static readonly StyledProperty<JunctionSignal?> SignalProperty = AvaloniaProperty.Register<ChainOutMeter, JunctionSignal?>(nameof(Signal));

    /// <summary>What the ladder shows (Settings ▸ Display): the average level with the peak held, or the peak alone.</summary>
    public static readonly StyledProperty<JunctionLevelStyle> LevelStyleProperty = AvaloniaProperty.Register<ChainOutMeter, JunctionLevelStyle>(nameof(LevelStyle), JunctionLevelStyle.Peak);

    private const double LadderWidth = 22;
    private const double WellHeight = 22;
    private const double WellGap = 10;

    private readonly LedLadderMeter _ladder = new() { Mode = MixerMeterMode.PeakRms, MeterWidth = LadderWidth, ColumnWidth = 7, GradientTarget = MeterGradientTarget.Chain, IsHitTestVisible = false };
    private readonly ConsoleScale _scale = new() { Mode = MixerMeterMode.PeakRms, IsHitTestVisible = false };
    private readonly EventHandler _updated;
    private JunctionSignal? _subscribed;
    private FormattedText? _readout;
    private string _readoutText = string.Empty;
    private long _readoutTenths = long.MaxValue;

    static ChainOutMeter() => AffectsRender<ChainOutMeter>(SignalProperty);

    public ChainOutMeter()
    {
        _updated = (_, _) => OnFrame();
        foreach (var child in new Control[] { _ladder, _scale })
        {
            LogicalChildren.Add(child);
            VisualChildren.Add(child);
        }
    }

    public JunctionSignal? Signal { get => GetValue(SignalProperty); set => SetValue(SignalProperty, value); }

    public JunctionLevelStyle LevelStyle { get => GetValue(LevelStyleProperty); set => SetValue(LevelStyleProperty, value); }

    /// <summary>The held peak shown in the well, in dBFS ("−∞" while silent).</summary>
    internal string ReadoutText => _readoutText;

    internal LedLadderMeter Ladder => _ladder;

    protected override Size MeasureOverride(Size availableSize)
    {
        _ladder.Measure(Size.Infinity);
        _scale.Measure(Size.Infinity);
        return new Size(LadderWidth + 30, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var height = Math.Max(0, finalSize.Height - WellHeight - WellGap);
        _ladder.Arrange(new Rect(0, 0, LadderWidth, height));
        _scale.Arrange(new Rect(LadderWidth, 0, Math.Max(0, finalSize.Width - LadderWidth), height));
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SignalProperty)
        {
            Subscribe(VisualRoot is not null ? Signal : null);
            OnFrame();
        }
        else if (change.Property == LevelStyleProperty)
        {
            OnFrame();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(Signal);
        OnFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Subscribe(null);
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        var well = new Rect(0, Bounds.Height - WellHeight, Bounds.Width, WellHeight);
        ConsolePaint.Well(context, well, 3);
        _readout ??= new FormattedText(_readoutText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ConsolePaint.Typeface("Font.Mono", FontWeight.SemiBold), 10, ConsolePaint.Brush(Palette.Text));
        context.DrawText(_readout, new Point(well.X + Math.Round((well.Width - _readout.Width) / 2), well.Y + Math.Round((well.Height - _readout.Height) / 2)));
    }

    private void OnFrame()
    {
        var levels = Signal is { CanCarryAudio: true } signal ? signal.Levels : null;
        var ready = levels is { IsActive: true };
        _ladder.IsReady = ready;
        if (ready)
        {
            LadderLevels.Show(_ladder, levels!, LevelStyle);
        }

        _ladder.IsClipped = levels?.Clipped == true;
        var hold = ready ? Math.Max(levels!.HoldLeft, levels.HoldRight) : double.NegativeInfinity;
        // The readout changes only in tenths of a dB, so it is formatted (and allocated) only when they change; long.MinValue is −∞.
        var silent = hold <= MeterLevels.FloorDecibels || double.IsNaN(hold);
        var tenths = silent ? long.MinValue : (long)Math.Round(hold * 10, MidpointRounding.AwayFromZero);
        if (tenths != _readoutTenths)
        {
            _readoutTenths = tenths;
            _readoutText = silent
                ? "−∞"
                : (tenths / 10.0).ToString("0.0", CultureInfo.InvariantCulture).Replace("-", "−", StringComparison.Ordinal);
            _readout = null;
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
        }

        _subscribed = signal;
        if (signal is not null)
        {
            signal.Levels.Updated += _updated;
        }
    }
}
