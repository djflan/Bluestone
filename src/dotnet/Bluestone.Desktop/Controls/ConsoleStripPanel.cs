using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The face of a console strip or device panel: a slate panel lit from above by a faint sheen, edged in a dark rim. A
/// selected panel lifts slightly toward the accent and takes a thin accent outline; nothing else changes.
/// </summary>
public class ConsoleStripPanel : Decorator
{
    public static readonly StyledProperty<bool> IsSelectedProperty = AvaloniaProperty.Register<ConsoleStripPanel, bool>(nameof(IsSelected));

    /// <summary>How far down the sheen fades out.</summary>
    public static readonly StyledProperty<double> SheenHeightProperty = AvaloniaProperty.Register<ConsoleStripPanel, double>(nameof(SheenHeight), 260);

    private const double Radius = 4;

    static ConsoleStripPanel() => AffectsRender<ConsoleStripPanel>(IsSelectedProperty, SheenHeightProperty);

    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }

    public double SheenHeight { get => GetValue(SheenHeightProperty); set => SetValue(SheenHeightProperty, value); }

    public override void Render(DrawingContext context)
    {
        var r = ConsolePaint.Snap(new Rect(Bounds.Size));
        var rounded = new RoundedRect(r, Radius);
        var selected = IsSelected;
        context.DrawRectangle(ConsolePaint.Brush(selected ? ConsolePaint.Mix(Palette.Panel, Palette.Accent, 0.04) : Palette.Panel), null, rounded);
        using (context.PushClip(rounded))
        {
            ConsolePaint.Sheen(context, r, SheenHeight);
        }

        context.DrawRectangle(null, ConsolePaint.Pen(selected ? ConsolePaint.Alpha(Palette.Accent, 0.9) : ConsolePaint.Alpha(ConsolePaint.Black, 0.55)), new RoundedRect(r.Deflate(0.5), Radius));
    }
}
