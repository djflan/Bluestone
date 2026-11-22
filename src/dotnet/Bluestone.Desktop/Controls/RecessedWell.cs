using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A recessed well (readouts, insert lists, meter beds): the darkest console surface with a dark rim and a faint lower
/// lip, as though cut into the panel. Holds one child.
/// </summary>
public class RecessedWell : Decorator
{
    public static readonly StyledProperty<double> RadiusProperty = AvaloniaProperty.Register<RecessedWell, double>(nameof(Radius), 3);

    static RecessedWell() => AffectsRender<RecessedWell>(RadiusProperty);

    public double Radius { get => GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    public override void Render(DrawingContext context) => ConsolePaint.Well(context, new Rect(Bounds.Size), Radius);
}
