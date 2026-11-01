using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Bluestone.Desktop.Controls;
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
        Assert.Contains("PRE-RELEASE", texts);
        Assert.Contains(Bluestone.Desktop.AppVersion.Release, texts);
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
    public Task About_ShowsTheNoticeInsteadOfProgress_AndOpensOnce() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        app.Window.ShowAbout();
        app.Window.ShowAbout();

        var about = Assert.Single(app.Window.OwnedWindows.OfType<SplashWindow>());
        Assert.True(about.IsAbout);
        var texts = about.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("BLUESTONE", texts);
        Assert.Contains(Bluestone.Desktop.AppVersion.Release, texts);
        Assert.Contains(SplashWindow.NonAffiliationNotice, texts);
        Assert.Contains(SplashWindow.Summary, texts);
        Assert.Empty(about.GetLogicalDescendants().OfType<ProgressBar>());
        app.SaveFrame("about", about);

        about.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Empty(app.Window.OwnedWindows.OfType<SplashWindow>());

        app.Window.ShowAbout();
        Assert.Single(app.Window.OwnedWindows.OfType<SplashWindow>()).Close();
    });

    [Theory]
    [InlineData("0.0.1+f3d6f8f2013231bb148f33b23729e7e137edbc94", "v0.0.1 f3d6f8f")]
    [InlineData("0.0.1+abc", "v0.0.1 abc")]
    [InlineData("0.0.1", "v0.0.1")]
    [InlineData("0.0.1+", "v0.0.1")]
    [InlineData(null, "development build")]
    public void AppVersion_ShowsSemverAndShortCommit(string? informationalVersion, string expected) =>
        Assert.Equal(expected, Bluestone.Desktop.AppVersion.Format(informationalVersion));

    /// <summary>The build takes its version from the latest vX.Y.Z tag (0.0.0 without one) and records its commit.</summary>
    [Fact]
    public void BuiltVersion_IsSemverWithItsCommit() =>
        Assert.Matches(@"^v\d+\.\d+\.\d+( [0-9a-f]{7})?$", Bluestone.Desktop.AppVersion.Display);

    [Fact]
    public void MarbleShader_Compiles()
    {
        using var effect = SkiaSharp.SKRuntimeEffect.CreateShader(MarbleSlab.Source, out var errors);
        Assert.True(effect is not null, errors);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(7.3, 21.9)]
    [InlineData(18.4, 3.1)]
    [InlineData(29.7, 33.2)]
    [InlineData(39.9, 12.6)]
    public Task Marble_IsDarkStoneWithBlueVeins_WhateverItsSeed(double x, double y) => HeadlessApp.RunAsync(() =>
    {
        var splash = new SplashWindow();
        splash.GetLogicalDescendants().OfType<MarbleSlab>().Single().Seed = new Vector(x, y);
        splash.Show();
        splash.UpdateLayout();
        using var frame = splash.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        var directory = Path.Combine(AppContext.BaseDirectory, "frames");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"splash-{x}-{y}.png")), new PngBitmapEncoderOptions());
        var (dark, blue, total) = Sample(frame);

        splash.Close();
        Assert.True(dark > total / 2, $"Expected mostly dark stone; {dark} of {total} samples were dark.");
        Assert.True(blue > total / 400, $"Expected some glowing blue veins; {blue} of {total} samples were blue.");
        return Task.CompletedTask;
    });

    // Controls are made on the UI thread: making one on a test thread starts Avalonia there and breaks later tests.
    [Fact]
    public Task EachSlab_ShowsADifferentPartOfTheMarble() => HeadlessApp.RunAsync(() =>
    {
        Assert.NotEqual(new MarbleSlab().Seed, new MarbleSlab().Seed);
        return Task.CompletedTask;
    });

    /// <summary>Counts dark and blue pixels in the marble, inside the shadow margin.</summary>
    private static (int Dark, int Blue, int Total) Sample(Bitmap frame)
    {
        var pixels = Pixels(frame);
        var width = frame.PixelSize.Width;
        var height = frame.PixelSize.Height;
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

        return (dark, blue, total);
    }

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
