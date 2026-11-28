using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A recessed gutter (transport, toolbars, tool groups): a long dark strip below the bar around it, with a 1 px inner
/// shadow and a lit lower lip. Glyph buttons sit inside with no chrome of their own. Holds one child. A style can turn the
/// well off (<see cref="ShowWell"/>) and keep the gutter's layout and every style that targets it.
/// </summary>
public class GutterPanel : Decorator
{
    public static readonly StyledProperty<double> RadiusProperty = AvaloniaProperty.Register<GutterPanel, double>(nameof(Radius), 4);

    /// <summary>Whether the recessed well is painted; false leaves only the child, on the surface around it.</summary>
    public static readonly StyledProperty<bool> ShowWellProperty = AvaloniaProperty.Register<GutterPanel, bool>(nameof(ShowWell), true);

    static GutterPanel() => AffectsRender<GutterPanel>(RadiusProperty, ShowWellProperty);

    public double Radius { get => GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    public bool ShowWell { get => GetValue(ShowWellProperty); set => SetValue(ShowWellProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (ShowWell)
        {
            ConsolePaint.Gutter(context, new Rect(Bounds.Size), Radius);
        }
    }
}
