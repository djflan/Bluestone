using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A recessed gutter (transport, toolbars, tool groups): a long dark strip below the bar around it, with a 1 px inner
/// shadow and a lit lower lip. Glyph buttons sit inside with no chrome of their own. Holds one child.
/// </summary>
public class GutterPanel : Decorator
{
    public static readonly StyledProperty<double> RadiusProperty = AvaloniaProperty.Register<GutterPanel, double>(nameof(Radius), 4);

    static GutterPanel() => AffectsRender<GutterPanel>(RadiusProperty);

    public double Radius { get => GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    public override void Render(DrawingContext context) => ConsolePaint.Gutter(context, new Rect(Bounds.Size), Radius);
}
