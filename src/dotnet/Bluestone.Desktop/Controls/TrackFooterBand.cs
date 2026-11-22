using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The track-colour name plate at the foot of a strip: the panel header tinted toward the track colour (a little more
/// when selected) under a 2 px track-colour top line. Holds the kind, name and routing lines as its child.
/// </summary>
public class TrackFooterBand : Decorator
{
    public static readonly StyledProperty<Color> TrackColorProperty = AvaloniaProperty.Register<TrackFooterBand, Color>(nameof(TrackColor), Palette.TextMuted);

    public static readonly StyledProperty<bool> IsSelectedProperty = AvaloniaProperty.Register<TrackFooterBand, bool>(nameof(IsSelected));

    /// <summary>Corner radius of the strip the band sits in; the band follows it at the bottom corners.</summary>
    public static readonly StyledProperty<double> RadiusProperty = AvaloniaProperty.Register<TrackFooterBand, double>(nameof(Radius), 3);

    static TrackFooterBand() => AffectsRender<TrackFooterBand>(TrackColorProperty, IsSelectedProperty, RadiusProperty);

    public Color TrackColor { get => GetValue(TrackColorProperty); set => SetValue(TrackColorProperty, value); }

    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }

    public double Radius { get => GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    public override void Render(DrawingContext context)
    {
        var r = new Rect(Bounds.Size);
        var radius = Radius;
        using (context.PushClip(new RoundedRect(r, new CornerRadius(0, 0, radius, radius))))
        {
            context.FillRectangle(ConsolePaint.Brush(ConsolePaint.Mix(Palette.PanelHeader, TrackColor, IsSelected ? 0.24 : 0.16)), r);
            context.FillRectangle(ConsolePaint.Brush(TrackColor), new Rect(r.X, r.Y, r.Width, 2));
        }
    }
}
