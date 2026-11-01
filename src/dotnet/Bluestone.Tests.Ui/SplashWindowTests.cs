using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Bluestone.Desktop.Controls;
using Avalonia.LogicalTree;
using Bluestone.Desktop.Views;

namespace Bluestone.Tests.Ui;

/// <summary>The start-up splash says what start-up is doing, and a failed start offers a way out instead of hanging.</summary>
public sealed class SplashWindowTests
{
    [Fact]
    public Task Report_ShowsTheStepAndItsProgress() => HeadlessApp.RunAsync(async () =>
    {
        var splash = new SplashWindow();
        splash.Show();
        splash.Report("Scanning plugins (2 of 4)…", "Surge XT.vst3", 0.25);
        await splash.RenderedAsync();

        var texts = splash.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("BLUESTONE", texts);
        Assert.Contains("PRE-RELEASE VERSION", texts);
        Assert.Contains("Scanning plugins (2 of 4)…", texts);
        Assert.Contains("Surge XT.vst3", texts);
        var progress = splash.GetLogicalDescendants().OfType<ProgressBar>().Single();
        Assert.False(progress.IsIndeterminate);
        Assert.Equal(0.25, progress.Value);

        splash.Report("Loading device profiles…");
        Assert.True(progress.IsIndeterminate);
        splash.Close();
    });

    [Fact]
    public Task ShowFailure_ExplainsAndQuitRaisesQuitRequested() => HeadlessApp.RunAsync(() =>
    {
        var splash = new SplashWindow();
        var quit = false;
        splash.QuitRequested += (_, _) => quit = true;
        splash.Show();
        splash.ShowFailure("The profiles folder could not be read.");

        Assert.Contains("The profiles folder could not be read.", splash.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.False(splash.GetLogicalDescendants().OfType<ProgressBar>().Single().IsVisible);
        var button = splash.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Quit"));
        Assert.True(button.IsVisible);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(quit);
        splash.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public void MarbleShader_Compiles()
    {
        using var effect = SkiaSharp.SKRuntimeEffect.CreateShader(MarbleSlab.Source, out var errors);
        Assert.True(effect is not null, errors);
    }

    [Fact]
    public Task Marble_IsDarkStoneWithBlueVeins() => HeadlessApp.RunAsync(() =>
    {
        var splash = new SplashWindow();
        splash.Show();
        splash.UpdateLayout();
        using var frame = splash.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        var directory = Path.Combine(AppContext.BaseDirectory, "frames");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, "splash.png"), new PngBitmapEncoderOptions());

        var pixels = Pixels(frame);
        var width = frame.PixelSize.Width;
        var height = frame.PixelSize.Height;

        // Sample the marble only (inside the shadow margin and away from the text).
        int dark = 0, blue = 0, total = 0;
        for (var y = height / 6; y < height * 5 / 6; y += 2)
        {
            for (var x = width / 12; x < width * 11 / 12; x += 2)
            {
                var i = ((y * width) + x) * 4;
                var (b, g, r) = (pixels[i], pixels[i + 1], pixels[i + 2]);
                total++;
                if (r < 40 && g < 40 && b < 50)
                {
                    dark++;
                }

                if (b > 120 && b > r + 50)
                {
                    blue++;
                }
            }
        }

        splash.Close();
        Assert.True(dark > total / 2, $"Expected mostly dark stone; {dark} of {total} samples were dark.");
        Assert.True(blue > total / 400, $"Expected some glowing blue veins; {blue} of {total} samples were blue.");
        return Task.CompletedTask;
    });

    private static byte[] Pixels(Bitmap frame)
    {
        var size = frame.PixelSize;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        using var copy = new WriteableBitmap(size, frame.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buffer = copy.Lock())
        {
            frame.CopyPixels(new PixelRect(size), buffer.Address, pixels.Length, stride);
            System.Runtime.InteropServices.Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);
        }

        // Frames from the headless renderer may be RGBA; normalise to BGRA so the checks read the right channels.
        if (frame.Format == PixelFormat.Rgba8888)
        {
            for (var i = 0; i < pixels.Length; i += 4)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            }
        }

        return pixels;
    }
}
