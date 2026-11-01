using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Views;

/// <summary>
/// Shown while Bluestone starts: a borderless, see-through window with the artwork over a slightly translucent card,
/// the product name, and what start-up is doing right now. A failed start shows the error and a Quit button instead
/// of leaving the splash up for ever.
/// </summary>
internal sealed class SplashWindow : Window
{
    private readonly TextBlock _status;
    private readonly TextBlock _detail;
    private readonly ProgressBar _progress;
    private readonly Button _quit;

    public SplashWindow()
    {
        Title = "Bluestone";
        Width = 640;
        Height = 320;
        CanResize = false;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;

        var version = typeof(SplashWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "development build";

        _status = new TextBlock { Text = "Starting…", Foreground = new SolidColorBrush(Palette.Text), FontSize = 13 };
        _detail = new TextBlock
        {
            Foreground = new SolidColorBrush(Palette.TextMuted),
            FontSize = 11,
            TextTrimming = TextTrimming.PrefixCharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        _progress = new ProgressBar { IsIndeterminate = true, Height = 3, MinHeight = 3, Minimum = 0, Maximum = 1 };
        _quit = new Button { Content = "Quit", IsVisible = false, HorizontalAlignment = HorizontalAlignment.Right };
        _quit.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        var title = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
            Children =
            {
                Wordmark(),
                new TextBlock { Text = "PRE-RELEASE VERSION", FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 3, Foreground = new SolidColorBrush(Palette.Accent), Margin = new Thickness(3, 0, 0, 0) },
            },
        };

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            RowSpacing = 6,
        };
        footer.Children.Add(_status);
        Grid.SetRow(_detail, 1);
        footer.Children.Add(_detail);
        var versionText = new TextBlock { Text = version, FontSize = 11, Foreground = new SolidColorBrush(Palette.TextMuted), VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(versionText, 1);
        footer.Children.Add(versionText);
        Grid.SetColumn(_quit, 1);
        Grid.SetRow(_quit, 1);
        footer.Children.Add(_quit);
        Grid.SetRow(_progress, 2);
        Grid.SetColumnSpan(_progress, 2);
        footer.Children.Add(_progress);

        // The card sits below the top edge so the artwork can break out of it.
        var card = new Border
        {
            Margin = new Thickness(16, 56, 16, 16),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Palette.Base, 0.95),
            BorderBrush = new SolidColorBrush(Colors.White, 0.08),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 12 40 0 #A0000000"),
            Padding = new Thickness(232, 22, 28, 18),
            Child = new DockPanel
            {
                Children = { WithDock(footer, Dock.Bottom), title },
            },
        };

        var art = Artwork();
        art.Margin = new Thickness(20, 0, 0, 0);
        art.HorizontalAlignment = HorizontalAlignment.Left;
        art.VerticalAlignment = VerticalAlignment.Top;

        Content = new Panel { Children = { card, art } };
    }

    /// <summary>Raised when the user asks to quit after start-up failed.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>Shows what start-up is doing. <paramref name="fraction"/> is 0–1, or null when how long it takes is unknown.</summary>
    public void Report(string status, string? detail = null, double? fraction = null)
    {
        _status.Text = status;
        _detail.Text = detail ?? string.Empty;
        _progress.IsIndeterminate = fraction is null;
        _progress.Value = fraction ?? 0;
    }

    /// <summary>Start-up failed: says why and offers to quit.</summary>
    public void ShowFailure(string message)
    {
        _status.Text = "Bluestone could not start";
        _status.Foreground = new SolidColorBrush(Palette.Record);
        _detail.Text = message;
        _detail.TextWrapping = TextWrapping.Wrap;
        _progress.IsVisible = false;
        _quit.IsVisible = true;
    }

    /// <summary>Completes once the window has drawn a frame showing the latest <see cref="Report"/>, so long steps run after it is visible.</summary>
    public async Task RenderedAsync()
    {
        var frame = new TaskCompletionSource();
        RequestAnimationFrame(_ => frame.TrySetResult());
        await Task.WhenAny(frame.Task, Task.Delay(100));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    /// <summary>
    /// The splash artwork, Assets/splash.png: a transparent PNG drawn at 2× (420×520 px) for this 210×260 area. Replace
    /// the file to change the image; it may break out above the card, which starts 56 px down.
    /// </summary>
    private static Image Artwork() => new()
    {
        Width = 210,
        Height = 260,
        Stretch = Stretch.Uniform,
        Source = new Bitmap(AssetLoader.Open(new Uri("avares://Bluestone.Desktop/Assets/splash.png"))),
    };

    /// <summary>The product name: bold capitals with an ice-to-blue sheen.</summary>
    private static TextBlock Wordmark() => new()
    {
        Text = "BLUESTONE",
        FontSize = 40,
        FontWeight = FontWeight.Bold,
        Foreground = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#F2F8FF"), 0),
                new GradientStop(Color.Parse("#A9D2FF"), 0.45),
                new GradientStop(Palette.Accent, 1),
            },
        },
    };

    private static Control WithDock(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
