using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Threading;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>Audio output settings: device, sample rate, buffer, render-ahead queue, on/off, test tone, master level.</summary>
internal sealed class AudioWindow : Window
{
    private readonly DispatcherTimer _meters = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public AudioWindow(AudioViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        Title = "Audio";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DataContext = viewModel;

        var start = new Button { Content = "Start audio", Command = viewModel.StartCommand };
        start.Classes.Add("primary");
        var stop = new Button { Content = "Stop audio", Command = viewModel.StopCommand };
        var close = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 10,
            Children =
            {
                Row("Device", Choice(nameof(AudioViewModel.Devices), nameof(AudioViewModel.Device), "Audio device", new Button { Content = "Refresh", Command = viewModel.RefreshDevicesCommand })),
                Row("Sample rate", Choice(nameof(AudioViewModel.SampleRates), nameof(AudioViewModel.SampleRate), "Sample rate")),
                Row("Buffer (frames)", Choice(nameof(AudioViewModel.BufferSizes), nameof(AudioViewModel.BufferFrames), "Buffer size")),
                Row("Render-ahead (frames)", Choice(nameof(AudioViewModel.QueueSizes), nameof(AudioViewModel.QueuedFrames), "Render-ahead queue")),
                Row("Live render-ahead", Choice(nameof(AudioViewModel.LiveQueueSizes), nameof(AudioViewModel.LiveQueuedFrames), "Live render-ahead queue")),
                new CheckBox { Content = "Low-latency monitoring (no delay compensation for the instrument played live)", [!ToggleButton.IsCheckedProperty] = new Binding(nameof(AudioViewModel.LowLatencyMonitoring)) },
                new TextBlock
                {
                    Text = "Render-ahead absorbs .NET garbage-collection pauses at the cost of latency; 0 renders in the device callback. While you play a plugin instrument the live render-ahead is used (0 keeps the other).",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Classes = { "muted" },
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { start, stop, new CheckBox { Content = "Test tone", [!ToggleButton.IsCheckedProperty] = new Binding(nameof(AudioViewModel.TestTone)) } } },
                new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(AudioViewModel.Status)), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(AudioViewModel.MasterLevel)), Classes = { "secondary" } },
                new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(AudioViewModel.Latency)), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "secondary" } },
                new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(AudioViewModel.Problems)), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" } },
                close,
            },
        };

        _meters.Tick += (_, _) => viewModel.RefreshMeters();
        Opened += (_, _) => _meters.Start();
        Closed += (_, _) => _meters.Stop();
    }

    private static DockPanel Row(string label, Control control) => new()
    {
        Children =
        {
            new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Left },
            control,
        },
    };

    private static Control Choice(string items, string selected, string name, Control? beside = null)
    {
        var box = new ComboBox
        {
            [!ItemsControl.ItemsSourceProperty] = new Binding(items),
            [!SelectingItemsControl.SelectedItemProperty] = new Binding(selected) { Mode = BindingMode.TwoWay },
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Avalonia.Automation.AutomationProperties.SetName(box, name);
        if (beside is null)
        {
            return box;
        }

        beside.Margin = new Avalonia.Thickness(6, 0, 0, 0);
        return new DockPanel { Children = { new ContentControl { Content = beside, [DockPanel.DockProperty] = Dock.Right }, box } };
    }
}
