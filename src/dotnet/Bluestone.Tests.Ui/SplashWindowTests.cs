using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
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
        splash.GetLogicalDescendants().OfType<MarbleSlab>().Single().Seed = new Vector(7.3, 21.9);
        splash.Show();
        splash.Report("Scanning plugins (2 of 4)…", "Surge XT.vst3", 0.25);
        await splash.RenderedAsync();

        var texts = splash.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("BLUESTONE", texts);
        Assert.Contains("PRE-RELEASE", texts);
        Assert.Contains(Bluestone.Desktop.AppVersion.Release, texts);
        Assert.Contains("Scanning plugins (2 of 4)…", texts);
        Assert.Contains("Surge XT.vst3", texts);
        Assert.Equal(LogoVariant.Splash, splash.GetLogicalDescendants().OfType<StoneMark>().Single().Variant);
        var progress = splash.GetLogicalDescendants().OfType<ProgressBar>().Single();
        Assert.False(progress.IsIndeterminate);
        Assert.Equal(0.25, progress.Value);

        splash.UpdateLayout();
        using (var frame = splash.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered."))
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "frames");
            Directory.CreateDirectory(directory);
            frame.Save(Path.Combine(directory, "splash-logo.png"), new PngBitmapEncoderOptions());
        }

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

    /// <summary>Windows and Linux have no native menu bar, so About is in the window's own Help menu.</summary>
    [Fact]
    public Task About_OpensFromTheInWindowHelpMenu() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        app.ChooseFromMenu("Help", "About Bluestone");

        var about = Assert.Single(app.Window.OwnedWindows.OfType<SplashWindow>());
        Assert.True(about.IsAbout);
        about.Close();
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

    [Fact]
    public Task StoneMark_DockImage_IsPaddedPng() => HeadlessApp.RunAsync(() =>
    {
        using var png = new MemoryStream(StoneMark.CreateDockImage(100));
        using var image = new Bitmap(png);
        Assert.Equal(new PixelSize(100, 100), image.PixelSize);

        var pixels = Pixels(image);
        Assert.All(Enumerable.Range(0, image.PixelSize.Width * 8), pixel => Assert.Equal((byte)0, pixels[(pixel * 4) + 3]));
        Assert.Contains(pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(0, 512, 40)]
    [InlineData(1, 256, 0)]
    [InlineData(2, 32, 0)]
    public Task StoneMark_ExportsTheCyanBlueAndOrangeHexGemstone(int variantValue, int pixels, double inset) => HeadlessApp.RunAsync(() =>
    {
        var variant = (LogoVariant)variantValue;
        var png = StoneMark.CreatePng(pixels, variant, inset);
        var directory = Path.Combine(AppContext.BaseDirectory, "frames");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, $"stone-mark-{variant.ToString().ToLowerInvariant()}.png"), png);

        if (variant == LogoVariant.Splash)
        {
            var preview = new Border
            {
                Width = pixels,
                Height = pixels,
                Padding = new Thickness(inset),
                Background = new SolidColorBrush(Color.Parse("#030D24")),
                Child = new StoneMark(),
            };
            preview.Measure(new Size(pixels, pixels));
            preview.Arrange(new Rect(0, 0, pixels, pixels));
            using var frame = new RenderTargetBitmap(new PixelSize(pixels, pixels));
            frame.Render(preview);
            frame.Save(Path.Combine(directory, "stone-mark-preview.png"), new PngBitmapEncoderOptions());
        }

        using var stream = new MemoryStream(png);
        using var image = new Bitmap(stream);
        Assert.Equal(new PixelSize(pixels, pixels), image.PixelSize);

        var rgba = Pixels(image);
        int cyan = 0, blue = 0, orange = 0;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < 180)
            {
                continue;
            }

            var (b, g, r) = (rgba[i], rgba[i + 1], rgba[i + 2]);
            if (g > r + 35 && b > r + 35)
            {
                cyan++;
            }

            if (b > r + 40)
            {
                blue++;
            }

            if (r > b + 40 && g > b + 20)
            {
                orange++;
            }
        }

        Assert.True(cyan > 1, $"Expected cyan facets in the {variant} logo.");
        Assert.True(blue > 1, $"Expected blue facets in the {variant} logo.");
        Assert.True(orange > 1, $"Expected orange facets in the {variant} logo.");
        return Task.CompletedTask;
    });

    /// <summary>The splash bloom spills a few design units beyond the shell: cyan on the left, amber on the right. Icons have none.</summary>
    [Fact]
    public Task StoneMark_SplashBloom_IsCyanAndAmberOutsideTheShell() => HeadlessApp.RunAsync(() =>
    {
        using var splashStream = new MemoryStream(StoneMark.CreatePng(256, LogoVariant.Splash, inset: 24));
        using var iconStream = new MemoryStream(StoneMark.CreatePng(256, LogoVariant.ApplicationIcon, inset: 24));
        using var splash = new Bitmap(splashStream);
        using var icon = new Bitmap(iconStream);
        var glowing = Pixels(splash);
        var plain = Pixels(icon);

        // The mark spans 208 px from x = 24; the shell's flat sides are at design x ≈ 10 and 90, so these mirrored pixels are 4 units outside.
        var left = ((128 * 256) + 36) * 4;
        var right = ((128 * 256) + 219) * 4;

        Assert.Equal((byte)0, plain[left + 3]);
        Assert.Equal((byte)0, plain[right + 3]);
        Assert.True(glowing[left + 3] > 40, $"Expected bloom beyond the cyan shell edge; alpha was {glowing[left + 3]}.");
        Assert.True(glowing[right + 3] > 40, $"Expected bloom beyond the amber shell edge; alpha was {glowing[right + 3]}.");
        Assert.True(glowing[left] > glowing[left + 2] + 3 && glowing[left + 1] > glowing[left + 2] + 3);
        Assert.True(glowing[right + 2] > glowing[right] + 3);
        return Task.CompletedTask;
    });

    /// <summary>As in the reference, the spoke below the centre divides deep blue on the left from refracted amber on the right.</summary>
    [Fact]
    public Task StoneMark_LowerHalf_IsDeepBlueLeftAndAmberRightOfTheBottomSpoke() => HeadlessApp.RunAsync(() =>
    {
        using var stream = new MemoryStream(StoneMark.CreatePng(256, LogoVariant.ApplicationIcon));
        using var image = new Bitmap(stream);
        var pixels = Pixels(image);
        var leftOfSpoke = ((185 * 256) + 114) * 4;
        var rightOfSpoke = ((185 * 256) + 142) * 4;

        Assert.True(pixels[leftOfSpoke] > pixels[leftOfSpoke + 2] + 60, "Expected deep blue left of the bottom spoke.");
        Assert.True(pixels[rightOfSpoke + 2] > pixels[rightOfSpoke] + 60, "Expected amber right of the bottom spoke.");
        return Task.CompletedTask;
    });

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
        // The glowing stone mark is not marble; leave it out of the count.
        splash.GetLogicalDescendants().OfType<StoneMark>().Single().IsVisible = false;
        splash.Show();
        splash.UpdateLayout();
        using var frame = splash.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        var directory = Path.Combine(AppContext.BaseDirectory, "frames");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"splash-{x}-{y}.png")), new PngBitmapEncoderOptions());
        var (dark, blue, gold, total) = Sample(frame);

        splash.Close();
        Assert.True(dark > total / 2, $"Expected mostly dark stone; {dark} of {total} samples were dark.");
        Assert.True(blue > total / 400, $"Expected some glowing blue veins; {blue} of {total} samples were blue.");
        Assert.True(gold > 0, "Expected some gold flecks beside the veins.");
        return Task.CompletedTask;
    });

    // Controls are made on the UI thread: making one on a test thread starts Avalonia there and breaks later tests.
    [Fact]
    public Task EachSlab_ShowsADifferentPartOfTheMarble() => HeadlessApp.RunAsync(() =>
    {
        Assert.NotEqual(new MarbleSlab().Seed, new MarbleSlab().Seed);
        return Task.CompletedTask;
    });

    /// <summary>Counts dark, blue, and gold pixels in the marble, inside the shadow margin. Gold flecks are a pixel or two, so every pixel is checked for them.</summary>
    private static (int Dark, int Blue, int Gold, int Total) Sample(Bitmap frame)
    {
        var pixels = Pixels(frame);
        var width = frame.PixelSize.Width;
        var height = frame.PixelSize.Height;
        int dark = 0, blue = 0, gold = 0, total = 0;
        for (var y = height / 6; y < height * 5 / 6; y++)
        {
            for (var x = width / 12; x < width * 11 / 12; x++)
            {
                var i = ((y * width) + x) * 4;
                var (b, g, r) = (pixels[i], pixels[i + 1], pixels[i + 2]);
                if (r > 110 && r > b + 30 && g > b)
                {
                    gold++;
                }

                if ((x | y) % 2 != 0)
                {
                    continue;
                }

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

        return (dark, blue, gold, total);
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
