using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// One colour edit the picker runs: what it starts from, how it is titled, and what it does with the colour. The caller
/// starts any preview before <see cref="ColourPickerPopup.Show"/>; exactly one of <see cref="Apply"/> or
/// <see cref="Cancel"/> runs when the picker closes, however it closes.
/// </summary>
public sealed record ColourEdit(
    Color Original,
    string Context,
    IReadOnlyList<string> Swatches,
    Action<Color> Preview,
    Action<Color> Apply,
    Action Cancel);

/// <summary>Which side of its chip the picker opened on.</summary>
public enum ColourPickerSide
{
    Below,
    Above,
    Right,
}

/// <summary>
/// The colour picker as a popup anchored to the chip it edits, with a notch pointing back at the chip. It opens below the
/// chip, else above it (clear of the page header), else beside it, always inside the window. A click away or Esc cancels
/// (the original comes back); Apply keeps the colour. Focus goes to the wheel on open and back to the chip on close.
/// </summary>
public sealed class ColourPickerPopup : Popup
{
    /// <summary>Space round the picker for its shadow and notch.</summary>
    public const double Shadow = 16;

    private const double Gap = 9;
    private const double Edge = 8;

    private readonly Frame _frame;
    private ColourEdit? _edit;
    private Control? _anchor;

    public ColourPickerPopup()
    {
        Picker = new ColourPicker();
        _frame = new Frame(Picker);
        Child = _frame;
        IsLightDismissEnabled = true;
        Placement = PlacementMode.AnchorAndGravity;
        PlacementAnchor = PopupAnchor.TopLeft;
        PlacementGravity = PopupGravity.BottomRight;
        PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.None;
        Picker.ColourChanged += (_, colour) => _edit?.Preview(colour);
        Picker.Applied += (_, colour) => Finish(colour);
        Picker.Cancelled += (_, _) => Finish(null);
        Closed += (_, _) => Finish(null);
    }

    /// <summary>The picker inside, for tests and for reading the colour as it stands.</summary>
    public ColourPicker Picker { get; }

    /// <summary>The chip the open picker belongs to, or null while it is closed.</summary>
    public Control? Anchor => _anchor;

    /// <summary>Where the picker sits, in its window's coordinates, while it is open.</summary>
    public Rect PickerBounds { get; private set; }

    /// <summary>Which side of the chip it opened on.</summary>
    public ColourPickerSide Side { get; private set; }

    /// <summary>
    /// Where a picker of <paramref name="picker"/> size goes for a chip at <paramref name="anchor"/> in a window of
    /// <paramref name="window"/> size whose top <paramref name="reserved"/> (the header) it must stay clear of.
    /// </summary>
    public static (Rect Bounds, ColourPickerSide Side) Place(Rect anchor, Size picker, Size window, double reserved)
    {
        var top = reserved + Edge;
        var x = Math.Clamp(anchor.Left - 12, Edge, Math.Max(Edge, window.Width - picker.Width - Edge));
        if (anchor.Bottom + Gap + picker.Height <= window.Height - Edge)
        {
            return (new Rect(x, anchor.Bottom + Gap, picker.Width, picker.Height), ColourPickerSide.Below);
        }

        if (anchor.Top - Gap - picker.Height >= top)
        {
            return (new Rect(x, anchor.Top - Gap - picker.Height, picker.Width, picker.Height), ColourPickerSide.Above);
        }

        var y = Math.Clamp(anchor.Top - 40, top, Math.Max(top, window.Height - picker.Height - Edge));
        var right = Math.Min(anchor.Right + Gap, Math.Max(Edge, window.Width - picker.Width - Edge));
        return (new Rect(right, y, picker.Width, picker.Height), ColourPickerSide.Right);
    }

    /// <summary>Opens the picker on <paramref name="anchor"/> for <paramref name="edit"/>; an edit already open is cancelled first.</summary>
    public void Show(Control anchor, ColourEdit edit)
    {
        if (_edit is not null)
        {
            Finish(null);
        }

        if (TopLevel.GetTopLevel(anchor) is not { } top || anchor.TranslatePoint(default, top) is not { } origin)
        {
            edit.Cancel();
            return;
        }

        _edit = edit;
        _anchor = anchor;
        Picker.Load(edit.Original, edit.Context, edit.Swatches);
        var anchorRect = new Rect(origin, anchor.Bounds.Size);
        var reserved = anchor.FindAncestorOfType<Window>() is null ? 0 : ReservedTop(top);

        // Open first, where the chip is, so the picker is styled and its true size known; then put it in its place.
        PlacementTarget = top;
        PlacementRect = new Rect(anchorRect.X, anchorRect.Bottom, 1, 1);
        anchor.Classes.Add("open");
        IsOpen = true;
        (TopLevel.GetTopLevel(Picker) ?? top).UpdateLayout();
        Picker.Measure(Size.Infinity);
        var (bounds, side) = Place(anchorRect, Picker.DesiredSize, top.ClientSize, reserved);
        PickerBounds = bounds;
        Side = side;
        _frame.Point(side, anchorRect.Center - bounds.Position);
        PlacementRect = new Rect(bounds.X - Shadow, bounds.Y - Shadow, 1, 1);

        // The wheel takes focus, showing its focus ring only when the chip was reached from the keyboard.
        var method = anchor.Classes.Contains(":focus-visible") ? NavigationMethod.Tab : NavigationMethod.Pointer;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Picker.FocusWheel(method), Avalonia.Threading.DispatcherPriority.Input);
    }

    /// <summary>Closes the picker, cancelling an edit that was not applied.</summary>
    public void Dismiss()
    {
        Finish(null);
    }

    private static double ReservedTop(TopLevel top) =>
        top.TryFindResource("Height.Header", out var height) && height is double h ? h : 30;

    private void Finish(Color? applied)
    {
        if (_edit is not { } edit)
        {
            return;
        }

        var anchor = _anchor;
        _edit = null;
        _anchor = null;
        try
        {
            if (applied is { } colour)
            {
                edit.Apply(colour);
            }
            else
            {
                edit.Cancel();
            }
        }
        finally
        {
            IsOpen = false;
            if (anchor is not null)
            {
                anchor.Classes.Remove("open");
                if (anchor.IsEffectivelyVisible && anchor.IsAttachedToVisualTree())
                {
                    anchor.Focus(NavigationMethod.Tab);
                }
            }
        }
    }

    /// <summary>The picker, with room round it for its shadow and the notch that points at the chip.</summary>
    private sealed class Frame : Panel
    {
        private readonly ColourPicker _picker;
        private readonly Notch _notch = new();

        public Frame(ColourPicker picker)
        {
            _picker = picker;
            Children.Add(picker);
            Children.Add(_notch);
            Background = Brushes.Transparent;
            ClipToBounds = false;
        }

        // The chip's centre, relative to the picker's top left.
        private Point _target;

        public void Point(ColourPickerSide side, Point target)
        {
            _notch.Side = side;
            _target = target;
            InvalidateArrange();
            _notch.InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _picker.Measure(Size.Infinity);
            _notch.Measure(Size.Infinity);
            return _picker.DesiredSize.Inflate(new Thickness(Shadow));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var p = new Rect(new Point(Shadow, Shadow), _picker.DesiredSize);
            _picker.Arrange(p);
            const double Half = 8;
            switch (_notch.Side)
            {
                case ColourPickerSide.Below:
                    _notch.Arrange(new Rect(Clamp(p.X + _target.X - Half, p.Left + 6, p.Right - 22), p.Top - 7, 16, 8));
                    break;
                case ColourPickerSide.Above:
                    _notch.Arrange(new Rect(Clamp(p.X + _target.X - Half, p.Left + 6, p.Right - 22), p.Bottom - 1, 16, 8));
                    break;
                default:
                    _notch.Arrange(new Rect(p.Left - 7, Clamp(p.Y + _target.Y - Half, p.Top + 6, p.Bottom - 22), 8, 16));
                    break;
            }

            return finalSize;
        }

        private static double Clamp(double v, double min, double max) => Math.Clamp(v, min, Math.Max(min, max));
    }

    private sealed class Notch : Control
    {
        private static readonly IImmutableSolidColorBrush Fill = new ImmutableSolidColorBrush(Palette.Raised);
        private static readonly ImmutablePen Outline = new(new ImmutableSolidColorBrush(Palette.BorderStrong), 1);
        private static readonly ImmutablePen Cover = new(new ImmutableSolidColorBrush(Palette.Raised), 2);

        public ColourPickerSide Side { get; set; }

        public override void Render(DrawingContext context)
        {
            var w = Bounds.Width;
            var h = Bounds.Height;
            var (s0, s1, tip) = Side switch
            {
                ColourPickerSide.Below => (new Point(0, h), new Point(w, h), new Point(w / 2, 0.5)),
                ColourPickerSide.Above => (new Point(0, 0), new Point(w, 0), new Point(w / 2, h - 0.5)),
                _ => (new Point(w, 0), new Point(w, h), new Point(0.5, h / 2)),
            };
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(s0, true);
                gc.LineTo(tip);
                gc.LineTo(s1);
                gc.EndFigure(false);
            }

            context.DrawGeometry(Fill, Outline, g);

            // Cover the picker's border where the notch joins it, so the two read as one shape.
            var inset = Side == ColourPickerSide.Right ? new Vector(0, 1.2) : new Vector(1.2, 0);
            context.DrawLine(Cover, s0 + inset, s1 - inset);
        }
    }
}
