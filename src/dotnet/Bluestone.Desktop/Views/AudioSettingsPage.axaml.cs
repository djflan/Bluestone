using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>
/// The Audio settings page. Its meters are read ten times a second while it is in the visual tree, which is only while
/// Settings shows it (Settings drops its pages when closed).
/// </summary>
public partial class AudioSettingsPage : UserControl
{
    private readonly DispatcherTimer _meters = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public AudioSettingsPage()
    {
        InitializeComponent();
        _meters.Tick += (_, _) =>
        {
            if (IsEffectivelyVisible)
            {
                (DataContext as AudioViewModel)?.RefreshMeters();
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _meters.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _meters.Stop();
    }
}
