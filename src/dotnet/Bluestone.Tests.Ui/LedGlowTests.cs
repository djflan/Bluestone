using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Desktop.Theme;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The LED glow setting (Settings ▸ Display ▸ LEDs) through <see cref="Glow.Use"/>: off, or at strength 0, nothing that
/// draws light builds any (no baked blur, no layers, no bloom, no transport shadow); a change redraws the lamps and
/// rebuilds what depended on the old strength; and the light a lit meter throws really reaches past its well.
/// </summary>
public sealed class LedGlowTests
{
    [Fact]
    public Task Off_OrAtStrengthZero_BuildsNoLightAtAll() => HeadlessApp.RunAsync(() =>
    {
        try
        {
            foreach (var (on, strength) in new (bool, double)[] { (false, 1), (true, 0) })
            {
                Glow.Use(on, strength);
                Assert.False(Glow.IsOn);
                Assert.Null(LedLight.For(Palette.Clip, new Size(4, 4), 0.5, ellipse: false, 1));
                Assert.Null(MeterGradient.Mixer.Light(Colors.Teal, new Size(4, 3), 0.5, ellipse: false, 1));
                Assert.Null(Glow.Bloom(Palette.Accent, Palette.GlowAlpha));
                Assert.Empty(Glow.LayersFor(1));
                Assert.Null(GlowEffects.Current.Accent);
                Assert.Null(GlowEffects.Current.Record);
            }

            Glow.Use(true, 1);
            Assert.NotNull(LedLight.For(Palette.Clip, new Size(4, 4), 0.5, ellipse: false, 1));
            Assert.NotNull(Glow.Bloom(Palette.Accent, Palette.GlowAlpha));
            Assert.NotEmpty(Glow.LayersFor(1));
            Assert.NotNull(GlowEffects.Current.Accent);
        }
        finally
        {
            Glow.Use(true, 1);
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task AChange_RedrawsOnce_AndRebuildsTheLightForTheNewStrength() => HeadlessApp.RunAsync(() =>
    {
        var raised = 0;
        void Count(object? sender, EventArgs e) => raised++;
        Glow.Changed += Count;
        try
        {
            var before = LedLight.For(Palette.Clip, new Size(4, 4), 0.5, ellipse: false, 1);
            var version = Glow.Version;
            Glow.Use(true, 1);
            Assert.Equal(0, raised);

            Glow.Use(true, 1.6);
            Assert.Equal(1, raised);
            Assert.Equal(version + 1, Glow.Version);
            var stronger = LedLight.For(Palette.Clip, new Size(4, 4), 0.5, ellipse: false, 1);
            Assert.NotSame(before, stronger);
            Assert.True(stronger!.Pad > before!.Pad, "A stronger glow reaches further.");

            Glow.Use(true, 9);
            Assert.Equal(2, Glow.Strength);
        }
        finally
        {
            Glow.Changed -= Count;
            Glow.Use(true, 1);
        }

        return Task.CompletedTask;
    });

    // A loud meter's light spills past its columns onto the well beside them; with the glow off the well stays dark.
    [Fact]
    public Task ALoudMeter_LightsTheWellBesideItsColumns_OnlyWhileTheGlowIsOn() => HeadlessApp.RunAsync(() =>
    {
        var ladder = new LedLadderMeter { Mode = MixerMeterMode.PeakRms, IsReady = true, Width = 40, Height = 220, MeterWidth = 14 };
        var window = new Window { Width = 80, Height = 240, Content = ladder };
        window.Show();
        try
        {
            ladder.Show(0, 0, double.NaN, double.NaN);
            var lit = Brightness(ladder);
            Glow.Use(false, 1);
            var dark = Brightness(ladder);
            Assert.True(lit > dark + 8, $"The well beside a lit column should be lit by it ({lit} against {dark} with the glow off).");
        }
        finally
        {
            Glow.Use(true, 1);
            window.Close();
        }

        return Task.CompletedTask;
    });

    // The brightest channel of the pixel just outside the well, beside its left column, half way up.
    private static int Brightness(LedLadderMeter ladder)
    {
        var size = new PixelSize((int)ladder.Bounds.Width, (int)ladder.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size);
        using (var context = bitmap.CreateDrawingContext())
        {
            ladder.Render(context);
        }

        var well = ladder.WellRect;
        var x = (int)Math.Round(well.X) - 1;
        var y = (int)(well.Y + (well.Height / 2));
        var pixels = new byte[4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), pixels.Length, 4);
        }
        finally
        {
            handle.Free();
        }

        return Math.Max(pixels[0], Math.Max(pixels[1], pixels[2]));
    }
}
