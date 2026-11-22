using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The Machined device card: a slate panel with a soft drop shadow, a 32 DIP header row lit from above, and a dark rim.
/// The selected card is lit (Lit Desk): an aqua rim with a wide soft halo, a pool of light on the desk under it, an
/// aqua-tinted header, and light falling from the header onto the face. Content sits on top; the card draws nothing over it.
/// </summary>
public class DeviceCardPanel : Decorator, ICustomHitTest
{
    public static readonly StyledProperty<bool> IsSelectedProperty = AvaloniaProperty.Register<DeviceCardPanel, bool>(nameof(IsSelected));

    public static readonly StyledProperty<double> HeaderHeightProperty = AvaloniaProperty.Register<DeviceCardPanel, double>(nameof(HeaderHeight), 32);

    /// <summary>Where the card sits on the desk under it; set false where the card is a plain panel (Out).</summary>
    public static readonly StyledProperty<bool> HasHeaderProperty = AvaloniaProperty.Register<DeviceCardPanel, bool>(nameof(HasHeader), true);

    /// <summary>The device could not load: the face takes a faint record-red cast.</summary>
    public static readonly StyledProperty<bool> IsUnavailableProperty = AvaloniaProperty.Register<DeviceCardPanel, bool>(nameof(IsUnavailable));

    private const double Radius = 5;
    private const double CardHalo = 0.20;
    private const double HaloReach = 10;
    private const double DeskPool = 0.10;
    private const double PoolReach = 30;

    private static readonly BoxShadows Shadow = new(new BoxShadow { OffsetY = 2, Blur = 6, Color = ConsolePaint.Alpha(ConsolePaint.Black, 0.45) });
    private static readonly IImmutableBrush Pool = new RadialGradientBrush
    {
        Center = new RelativePoint(0.5, 0.9, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(0.5, 0.9, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.6, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.7, RelativeUnit.Relative),
        GradientStops = { new GradientStop(ConsolePaint.Alpha(Palette.Accent, DeskPool), 0), new GradientStop(ConsolePaint.Alpha(Palette.Accent, 0), 1) },
    }.ToImmutable();

    static DeviceCardPanel()
    {
        AffectsRender<DeviceCardPanel>(IsSelectedProperty, HeaderHeightProperty, HasHeaderProperty, IsUnavailableProperty);
        ClipToBoundsProperty.OverrideDefaultValue<DeviceCardPanel>(false);
    }

    /// <summary>The halo and the pool of light are drawn outside the card; only the card itself takes the pointer.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }

    public double HeaderHeight { get => GetValue(HeaderHeightProperty); set => SetValue(HeaderHeightProperty, value); }

    public bool HasHeader { get => GetValue(HasHeaderProperty); set => SetValue(HasHeaderProperty, value); }

    public bool IsUnavailable { get => GetValue(IsUnavailableProperty); set => SetValue(IsUnavailableProperty, value); }

    public override void Render(DrawingContext context)
    {
        var r = ConsolePaint.Snap(new Rect(Bounds.Size));
        if (r.Width < 4 || r.Height < 4)
        {
            return;
        }

        var selected = IsSelected;
        var rounded = new RoundedRect(r, Radius);
        if (selected)
        {
            // The pool stays on the desk either side: it reaches only as far as the gap, never over the neighbouring cards.
            context.FillRectangle(Pool, new Rect(r.X - PoolReach, r.Y - 20, r.Width + (2 * PoolReach), r.Height + 36));
            Glow.Halo(context, r, Radius, Palette.Accent, CardHalo, HaloReach);
        }

        context.DrawRectangle(ConsolePaint.Brush(IsUnavailable ? ConsolePaint.Mix(Palette.Panel, Palette.Record, 0.12) : Palette.Panel), null, rounded, Shadow);
        using (context.PushClip(rounded))
        {
            if (HasHeader)
            {
                var header = new Rect(r.X, r.Y, r.Width, HeaderHeight);
                context.FillRectangle(
                    selected
                        ? ConsolePaint.Vertical(ConsolePaint.Mix(Palette.PanelHeader, Palette.Accent, 0.16 + (DeskPool * 0.6)), ConsolePaint.Mix(Palette.PanelHeader, Palette.Accent, 0.05))
                        : ConsolePaint.Vertical(ConsolePaint.Lighter(Palette.PanelHeader, 0.02), Palette.PanelHeader),
                    header);
                ConsolePaint.Sheen(context, r, 160);
                ConsolePaint.HLine(context, ConsolePaint.Alpha(ConsolePaint.Black, 0.55), r.X, r.Right, header.Bottom - 1);
                if (selected)
                {
                    // Light falling from the lit header onto the face.
                    context.FillRectangle(
                        ConsolePaint.Vertical(ConsolePaint.Alpha(Palette.Accent, 0.05 + (DeskPool * 0.3)), ConsolePaint.Alpha(Palette.Accent, 0)),
                        new Rect(r.X, header.Bottom, r.Width, 70));
                }
            }
            else
            {
                ConsolePaint.Sheen(context, r, 160);
            }

            ConsolePaint.HLine(context, ConsolePaint.Alpha(ConsolePaint.White, selected ? 0.10 : 0.05), r.X, r.Right, r.Y);
        }

        context.DrawRectangle(null, ConsolePaint.Pen(selected ? ConsolePaint.Alpha(Palette.Accent, 0.85) : ConsolePaint.Alpha(ConsolePaint.Black, 0.6)), new RoundedRect(r.Deflate(0.5), Radius));
    }
}
