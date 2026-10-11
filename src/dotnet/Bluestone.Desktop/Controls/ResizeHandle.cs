using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;

namespace Bluestone.Desktop.Controls;

/// <summary>Which side of the handle the region it resizes is on.</summary>
public enum ResizeEdge
{
    /// <summary>The region is to the left: dragging right grows it.</summary>
    Left,

    /// <summary>The region is to the right: dragging left grows it.</summary>
    Right,

    /// <summary>The region is below: dragging up grows it.</summary>
    Below,
}

/// <summary>
/// The edge between a workspace region and the rest of the window. Dragging it changes the region's extent (a bound view
/// model value, so the size is saved with the layout) rather than a grid's definitions directly.
/// </summary>
public sealed class ResizeHandle : Thumb
{
    public static readonly StyledProperty<double> ExtentProperty =
        AvaloniaProperty.Register<ResizeHandle, double>(nameof(Extent), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<ResizeEdge> EdgeProperty = AvaloniaProperty.Register<ResizeHandle, ResizeEdge>(nameof(Edge));

    /// <summary>The largest the region may grow to, so the rest of the window keeps some space.</summary>
    public static readonly StyledProperty<double> MaximumExtentProperty =
        AvaloniaProperty.Register<ResizeHandle, double>(nameof(MaximumExtent), double.PositiveInfinity);

    private double _startExtent;
    private double _travel;

    public double Extent
    {
        get => GetValue(ExtentProperty);
        set => SetValue(ExtentProperty, value);
    }

    public ResizeEdge Edge
    {
        get => GetValue(EdgeProperty);
        set => SetValue(EdgeProperty, value);
    }

    public double MaximumExtent
    {
        get => GetValue(MaximumExtentProperty);
        set => SetValue(MaximumExtentProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(ResizeHandle);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EdgeProperty)
        {
            var vertical = Edge == ResizeEdge.Below;
            Cursor = new Cursor(vertical ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast);
            Classes.Set("vertical", vertical);
        }
    }

    protected override void OnDragStarted(VectorEventArgs e)
    {
        base.OnDragStarted(e);
        _startExtent = Extent;
        _travel = 0;
    }

    protected override void OnDragDelta(VectorEventArgs e)
    {
        base.OnDragDelta(e);
        _travel += Edge switch
        {
            ResizeEdge.Left => e.Vector.X,
            ResizeEdge.Right => -e.Vector.X,
            _ => -e.Vector.Y,
        };
        Extent = Math.Min(MaximumExtent, Math.Max(0, _startExtent + _travel));
    }

    static ResizeHandle()
    {
        HorizontalAlignmentProperty.OverrideDefaultValue<ResizeHandle>(HorizontalAlignment.Stretch);
        VerticalAlignmentProperty.OverrideDefaultValue<ResizeHandle>(VerticalAlignment.Stretch);
    }
}
