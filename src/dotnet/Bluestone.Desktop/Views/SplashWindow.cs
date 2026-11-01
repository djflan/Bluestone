using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Views;

/// <summary>
/// A borderless window that is a slab of black marble with glowing blue veins and the product name. While Bluestone
/// starts it shows what start-up is doing right now; a failed start shows the error and a Quit button instead of
/// leaving the splash up for ever. As About Bluestone (<see cref="CreateAbout"/>) it shows the licence and the
/// non-affiliation notice instead, and closes on Esc or when another window is clicked.
/// </summary>
internal sealed class SplashWindow : Window
{
    public const string NonAffiliationNotice =
        "Bluestone is an independent open-source project and is not affiliated with, authorized, sponsored, or endorsed by Yamaha Corporation " +
        "or by the owner of any other product, format, or standard it names. Yamaha, XG, QY100, and other product names and trademarks " +
        "belong to their respective owners and are used only to identify products and describe compatibility. " +
        "VST is a registered trademark of Steinberg Media Technologies GmbH.";

    public const string Summary = "A hardware-oriented DAW built around MIDI and real instruments. MIT licensed.";

    private readonly TextBlock _status;
    private readonly TextBlock _detail;
    private readonly ProgressBar _progress;
    private readonly Button _quit;

    private bool _closing;

    /// <summary>The start-up splash.</summary>
    public SplashWindow()
        : this(about: false)
    {
    }

    private SplashWindow(bool about)
    {
        IsAbout = about;
        Title = about ? "About Bluestone" : "Bluestone";
        Width = 620;
        Height = about ? 340 : 300;
        CanResize = false;
        ShowInTaskbar = !about;
        WindowStartupLocation = about ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;

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
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(3, 0, 0, 0),
                    Children =
                    {
                        new TextBlock { Text = AppVersion.Release, FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 1, Foreground = new SolidColorBrush(Palette.Text) },
                        new TextBlock { Text = AppVersion.Commit, FontSize = 11, LetterSpacing = 1, Foreground = new SolidColorBrush(Palette.TextMuted), IsVisible = AppVersion.Commit.Length > 0 },
                        new TextBlock { Text = "PRE-RELEASE", FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 3, Foreground = new SolidColorBrush(Palette.Accent) },
                    },
                },
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
        Grid.SetColumn(_quit, 1);
        Grid.SetRow(_quit, 1);
        footer.Children.Add(_quit);
        Grid.SetRow(_progress, 2);
        Grid.SetColumnSpan(_progress, 2);
        footer.Children.Add(_progress);

        // A margin round the marble leaves room for its shadow in the see-through window.
        var shadow = new Border
        {
            Margin = new Thickness(Inset),
            CornerRadius = new CornerRadius(Controls.MarbleSlab.CornerRadius),
            Background = Brushes.Black,
            BoxShadow = BoxShadows.Parse("0 10 30 0 #B0000000"),
        };
        var content = new Border
        {
            Margin = new Thickness(Inset),
            Padding = new Thickness(40, 30, 32, 24),
            Child = new DockPanel
            {
                Children = { WithDock(about ? AboutFooter() : footer, Dock.Bottom), title },
            },
        };

        var layers = new Panel { Children = { shadow, new Controls.MarbleSlab { Margin = new Thickness(Inset) } } };
        if (about)
        {
            // The small print sits on a darker fade so the veins do not run through it.
            layers.Children.Add(new Border
            {
                Margin = new Thickness(Inset),
                Height = 170,
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(0, 0, Controls.MarbleSlab.CornerRadius, Controls.MarbleSlab.CornerRadius),
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#00040508"), 0), new GradientStop(Color.Parse("#D0040508"), 0.45), new GradientStop(Color.Parse("#E8040508"), 1) },
                },
            });
        }

        layers.Children.Add(content);
        Content = layers;
    }

    /// <summary>The space round the marble for its shadow.</summary>
    private const double Inset = 16;

    /// <summary>Whether this is About Bluestone rather than the start-up splash.</summary>
    public bool IsAbout { get; }

    /// <summary>About Bluestone: the splash with the licence and notice in place of start-up progress. Show it with an owner.</summary>
    public static SplashWindow CreateAbout() => new(about: true);

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (IsAbout && e.Key == Avalonia.Input.Key.Escape)
        {
            e.Handled = true;
            CloseOnce();
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (IsAbout)
        {
            // Closes when the user clicks another window (or switches app), like a popover.
            Deactivated += (_, _) => CloseOnce();
            Activate();
        }
    }

    private void CloseOnce()
    {
        if (!_closing)
        {
            _closing = true;
            Close();
        }
    }

    private static StackPanel AboutFooter() => new()
    {
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = Summary, FontSize = 12, Foreground = new SolidColorBrush(Palette.Text), TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = NonAffiliationNotice, FontSize = 10, Foreground = new SolidColorBrush(Palette.TextSecondary), TextWrapping = TextWrapping.Wrap, LineHeight = 14 },
        },
    };

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

    /// <summary>The product name: bold white capitals over two blurred azure copies, a wide halo and a tight bright glow.</summary>
    private static Panel Wordmark()
    {
        static TextBlock Layer(IBrush brush, double blur, double opacity) => new()
        {
            Text = "BLUESTONE",
            FontSize = 40,
            FontWeight = FontWeight.Bold,
            Foreground = brush,
            Opacity = opacity,
            Effect = blur > 0 ? new BlurEffect { Radius = blur } : null,
            IsHitTestVisible = false,
        };

        return new Panel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                Layer(new SolidColorBrush(Color.Parse("#2F7BFF")), blur: 18, opacity: 0.7),
                Layer(new SolidColorBrush(Color.Parse("#7DB8FF")), blur: 5, opacity: 0.7),
                Layer(Brushes.White, blur: 0, opacity: 1),
            },
        };
    }

    private static Control WithDock(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
