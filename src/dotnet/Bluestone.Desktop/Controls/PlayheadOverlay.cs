using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>The playhead line, drawn above the lanes so moving it never redraws the notes. Red while recording.</summary>
public sealed class PlayheadOverlay : Control
{
    public static readonly StyledProperty<double> XProperty = AvaloniaProperty.Register<PlayheadOverlay, double>(nameof(X));
    public static readonly StyledProperty<bool> IsRecordingProperty = AvaloniaProperty.Register<PlayheadOverlay, bool>(nameof(IsRecording));

    private static readonly IBrush Line = new SolidColorBrush(Palette.Playhead, 0.9);
    private static readonly IBrush RecordLine = new SolidColorBrush(Palette.Record);

    static PlayheadOverlay()
    {
        AffectsRender<PlayheadOverlay>(XProperty, IsRecordingProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<PlayheadOverlay>(false);
    }

    public double X
    {
        get => GetValue(XProperty);
        set => SetValue(XProperty, value);
    }

    public bool IsRecording
    {
        get => GetValue(IsRecordingProperty);
        set => SetValue(IsRecordingProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var x = Math.Round(X);
        context.FillRectangle(IsRecording ? RecordLine : Line, new Rect(x, 0, 1, Bounds.Height));
    }
}
