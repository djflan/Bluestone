using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Bluestone.Desktop.Controls;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The hooks a later style needs to quiet the workspace without subclassing or overriding a local value: a gutter
/// without its well, a chain junction without its painted captions (and, by default, captions at the type floor), and
/// device footer keys whose targets meet the hit-target floor while their visible plates stay small.
/// </summary>
public sealed class MinimalUiFoundationTests
{
    [Fact]
    public Task AGutter_WithoutItsWell_PaintsNothingOfItsOwn() => HeadlessApp.RunAsync(() =>
    {
        var gutter = new GutterPanel { Width = 120, Height = 30 };
        var window = new Window { Width = 160, Height = 60, Background = new SolidColorBrush(Color.FromRgb(0x40, 0x10, 0x60)), Content = gutter };
        window.Show();
        try
        {
            Assert.NotEqual(PixelAt(window, new Point(2, 2)), PixelAt(window, Center(window, gutter)));

            gutter.ShowWell = false;
            Assert.Equal(PixelAt(window, new Point(2, 2)), PixelAt(window, Center(window, gutter)));
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task AJunctionsCaption_MeetsTheTypeFloor_AndAStyleCanHideIt() => HeadlessApp.RunAsync(() =>
    {
        var junction = new ChainJunction { Signal = Notes(), Width = 30, Height = 220 };
        var window = new Window { Width = 80, Height = 260, Content = junction };
        window.Show();
        try
        {
            Render(window);
            Assert.True(junction.CaptionFontSize >= 10, $"The caption is {junction.CaptionFontSize} pt.");
            Assert.Same(Avalonia.Application.Current!.FindResource("Text.Secondary"), junction.CaptionForeground);
            Assert.NotNull(junction.DrawnMidiCaption);
            Assert.True(junction.DrawnMidiCaption!.Width <= junction.Bounds.Width, "The caption fits across the junction.");

            junction.ShowCaptions = false;
            Render(window);
            Assert.Null(junction.DrawnMidiCaption);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task DeviceFooterKeys_HaveFullHitTargets_InAFooterNoTaller_WithTheirPlatesTheSameSize() => HeadlessApp.RunAsync(() =>
    {
        var pin = new ToggleButton { Classes = { "tool", "tiny" }, Content = new Border { Classes = { "hitPlate" }, Child = new PathIcon() } };
        var editor = new Button { Classes = { "tool", "tiny" }, Content = new Border { Classes = { "hitPlate" }, Child = new PathIcon() } };
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Children = { pin, editor } };
        Grid.SetColumn(keys, 1);
        var footer = new Grid { Classes = { "deviceFooter" }, ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { keys } };
        var window = new Window { Width = 200, Height = 80, Content = new StackPanel { Margin = new Thickness(0, 20), Children = { footer } } };
        window.Show();
        try
        {
            Render(window);
            Assert.Equal((double)Avalonia.Application.Current!.FindResource("DevicePanel.FooterHeight")!, footer.Bounds.Height);
            foreach (var key in new Control[] { pin, editor })
            {
                Assert.True(key.Bounds.Width >= 24 && key.Bounds.Height >= 24, $"A footer key is {key.Bounds.Size}.");
                var plate = key.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("hitPlate"));
                Assert.Equal(new Size(18, 18), plate.Bounds.Size);

                // Its whole target is hit, top and bottom edge, not just the plate.
                foreach (var y in new[] { 0.5, key.Bounds.Height - 0.5 })
                {
                    var at = key.TranslatePoint(new Point(key.Bounds.Width / 2, y), window)!.Value;
                    var hit = window.InputHitTest(at) as Visual;
                    Assert.True(hit is not null && (hit == key || hit.GetVisualAncestors().Contains(key)), $"{at} hit {hit?.GetType().Name ?? "nothing"}.");
                }
            }
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    private static JunctionSignal Notes()
    {
        var signal = new JunctionSignal();
        (typeof(JunctionSignal).GetProperty(nameof(JunctionSignal.CanCarryNotes)) ?? throw new InvalidOperationException("JunctionSignal.CanCarryNotes was not found."))
            .SetValue(signal, true);
        return signal;
    }

    private static void Render(Window window)
    {
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame();
    }

    private static Point Center(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static uint PixelAt(Window window, Point point)
    {
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        var scale = frame.Size.Width / window.Bounds.Width;
        var pixels = new byte[4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            frame.CopyPixels(new PixelRect((int)(point.X * scale), (int)(point.Y * scale), 1, 1), handle.AddrOfPinnedObject(), pixels.Length, 4);
        }
        finally
        {
            handle.Free();
        }

        return BitConverter.ToUInt32(pixels);
    }
}
