using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Bluestone.Desktop.Theme;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A stereo level meter drawn from <see cref="MeterLevels"/>: for each side a bar to the peak, a brighter bar to the RMS,
/// a line at the held peak, and a clip light at the top (click to clear). It redraws when its levels update; drawing
/// allocates nothing. The scale runs from <see cref="MeterLevels.FloorDecibels"/> to <see cref="MeterLevels.CeilingDecibels"/>.
/// </summary>
internal sealed class LevelMeter : Control
{
    public static readonly StyledProperty<MeterLevels?> LevelsProperty = AvaloniaProperty.Register<LevelMeter, MeterLevels?>(nameof(Levels));

    private const double ClipLight = 4;
    private static readonly IBrush Track = new SolidColorBrush(Palette.Divider);
    private static readonly IBrush Background = new SolidColorBrush(Palette.Panel);
    private static readonly IBrush Peak = new SolidColorBrush(Color.Parse("#2F7A4A"));
    private static readonly IBrush Rms = new SolidColorBrush(Color.Parse("#4CC27A"));
    private static readonly IBrush Hot = new SolidColorBrush(Color.Parse("#D9A43A"));
    private static readonly IBrush ClipOn = new SolidColorBrush(Palette.Record);
    private static readonly IBrush ClipOff = new SolidColorBrush(Palette.Raised);
    private static readonly IPen Hold = new Pen(new SolidColorBrush(Palette.Text), 1);
    private static readonly IPen Zero = new Pen(new SolidColorBrush(Palette.TextMuted), 1);
    private readonly EventHandler _updated;
    private MeterLevels? _subscribed;
    private bool _attached;

    static LevelMeter() => AffectsRender<LevelMeter>(LevelsProperty);

    public LevelMeter()
    {
        _updated = (_, _) => InvalidateVisual();
        Width = 14;
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        ToolTip.SetTip(this, "Peak and RMS level, with the held peak. The light at the top shows clipping; click to clear it.");
    }

    public MeterLevels? Levels
    {
        get => GetValue(LevelsProperty);
        set => SetValue(LevelsProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var levels = Levels;
        var height = Bounds.Height - ClipLight - 2;
        var width = (Bounds.Width - 1) / 2;

        // The whole control is filled (the gap between the sides too), so a click anywhere on it reaches it.
        context.FillRectangle(Background, new Rect(Bounds.Size));
        context.FillRectangle(levels is { Clipped: true } ? ClipOn : ClipOff, new Rect(0, 0, Bounds.Width, ClipLight));
        for (var side = 0; side < 2; side++)
        {
            var x = side * (width + 1);
            var column = new Rect(x, ClipLight + 2, width, height);
            context.FillRectangle(Track, column);
            if (levels is null || !levels.IsActive)
            {
                continue;
            }

            var peak = side == 0 ? levels.PeakLeft : levels.PeakRight;
            var rms = side == 0 ? levels.RmsLeft : levels.RmsRight;
            var hold = side == 0 ? levels.HoldLeft : levels.HoldRight;
            Bar(context, column, peak, peak > -6 ? Hot : Peak);
            Bar(context, column, rms, Rms);
            if (hold > MeterLevels.FloorDecibels)
            {
                var y = Y(column, hold);
                context.DrawLine(Hold, new Point(x, y), new Point(x + width, y));
            }
        }

        var zero = Y(new Rect(0, ClipLight + 2, Bounds.Width, height), 0);
        context.DrawLine(Zero, new Point(0, zero), new Point(Bounds.Width, zero));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LevelsProperty)
        {
            Resubscribe();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Resubscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        if (_subscribed is not null)
        {
            _subscribed.Updated -= _updated;
            _subscribed = null;
        }
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Levels?.ResetClip();
        InvalidateVisual();
    }

    private void Resubscribe()
    {
        if (_subscribed is not null)
        {
            _subscribed.Updated -= _updated;
        }

        _subscribed = _attached ? Levels : null;
        if (_subscribed is not null)
        {
            _subscribed.Updated += _updated;
        }
    }

    private static void Bar(DrawingContext context, Rect column, double decibels, IBrush brush)
    {
        var top = Y(column, decibels);
        if (top < column.Bottom)
        {
            context.FillRectangle(brush, new Rect(column.X, top, column.Width, column.Bottom - top));
        }
    }

    private static double Y(Rect column, double decibels) => column.Bottom - (MeterLevels.Fraction(decibels) * column.Height);
}
