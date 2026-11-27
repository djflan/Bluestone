using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Desktop.Theme;

namespace Bluestone.Tests.Ui;

/// <summary>Rendered stereo Peak + RMS cells, including missing readings and the independently falling peak.</summary>
public sealed class LedLadderMeterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CombinedMeter_HasABrightRmsBodyAndDimPeakExtension_PerChannel(bool glowOn) => HeadlessApp.RunAsync(() =>
    {
        var gradient = MeterGradient.Mixer.Gradient;
        var glow = Glow.IsOn;
        var strength = Glow.Strength;
        var reduce = LedMotion.ReduceMotion;
        Glow.Use(glowOn, strength);
        LedMotion.ReduceMotion = true;
        MeterGradient.Mixer.Use([(0.0, Colors.DodgerBlue), (1.0, Colors.DodgerBlue)]);
        var ladder = new LedLadderMeter
        {
            Mode = MixerMeterMode.PeakRms,
            IsReady = true,
            Width = 40,
            Height = 220,
            LeftValue = -6,
            LeftRmsValue = -30,
            RightValue = -18,
            RightRmsValue = -9,
        };
        var window = Show(ladder);
        try
        {
            var frame = Frame(ladder);
            var rmsTop = ladder.Cells(-30, double.NaN, ladder.CellCount).TopLit;
            var peakTop = ladder.Cells(-6, double.NaN, ladder.CellCount).TopLit;
            var middle = (rmsTop + peakTop) / 2;
            var dark = Math.Min(ladder.CellCount - 1, peakTop + 3);
            var leftDim = Brightness(ladder, frame, 0, middle);
            var rightBright = Brightness(ladder, frame, 1, middle);

            Assert.True(leftDim > Brightness(ladder, frame, 0, dark) + 35, "The peak extension must be visibly above unlit.");
            Assert.True(rightBright > leftDim + 80, $"The same cell is bright on the right ({rightBright}) and dim on the left ({leftDim}).");
            Assert.Equal(Brightness(ladder, frame, 0, 2), Brightness(ladder, frame, 1, 2));

            MeterGradient.Mixer.Use([(0.0, Colors.Orange), (1.0, Colors.Orange)]);
            var recoloured = Frame(ladder);
            Assert.NotEqual(frame, recoloured);
            Assert.True(Brightness(ladder, recoloured, 1, middle) > Brightness(ladder, recoloured, 0, middle) + 60);
        }
        finally
        {
            window.Close();
            MeterGradient.Mixer.Use(gradient);
            Glow.Use(glow, strength);
            LedMotion.ReduceMotion = reduce;
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task RmsBoundary_UsesTheExistingWarmupAndFadeTowardTheDimPeak() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var gradient = MeterGradient.Mixer.Gradient;
        var glow = Glow.IsOn;
        var strength = Glow.Strength;
        Glow.Use(false, strength);
        MeterGradient.Mixer.Use([(0.0, Colors.DodgerBlue), (1.0, Colors.DodgerBlue)]);
        var ladder = new LedLadderMeter
        {
            Mode = MixerMeterMode.PeakRms,
            IsReady = true,
            Width = 40,
            Height = 220,
            LeftValue = -6,
            LeftRmsValue = -36,
        };
        var window = Show(ladder);
        try
        {
            var index = ladder.Cells(-18, double.NaN, ladder.CellCount).TopLit;
            var dim = Brightness(ladder, Frame(ladder), 0, index);
            clock.Time = 1000;
            ladder.LeftRmsValue = -6;
            _ = Frame(ladder);
            clock.Frame(1010);
            var warming = Brightness(ladder, Frame(ladder), 0, index);
            clock.Frame(1020);
            var bright = Brightness(ladder, Frame(ladder), 0, index);
            Assert.True(dim < warming && warming < bright, $"Warm-up: dim {dim}, halfway {warming}, bright {bright}.");

            clock.Time = 2000;
            ladder.LeftRmsValue = -36;
            _ = Frame(ladder);
            clock.Frame(2020);
            var fading = Brightness(ladder, Frame(ladder), 0, index);
            clock.Frame(2090);
            Assert.True(dim < fading && fading < bright, $"Fade: bright {bright}, part-way {fading}, dim {dim}.");
            Assert.Equal(dim, Brightness(ladder, Frame(ladder), 0, index));
        }
        finally
        {
            window.Close();
            MeterGradient.Mixer.Use(gradient);
            Glow.Use(glow, strength);
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task RmsAboveFallingPeak_StaysBright_AndHoldStaysAtItsActualCell() => HeadlessApp.RunAsync(() =>
    {
        var gradient = MeterGradient.Mixer.Gradient;
        var glow = Glow.IsOn;
        var strength = Glow.Strength;
        var reduce = LedMotion.ReduceMotion;
        Glow.Use(false, strength);
        LedMotion.ReduceMotion = true;
        MeterGradient.Mixer.Use([(0.0, Colors.DodgerBlue), (1.0, Colors.DodgerBlue)]);
        var ladder = new LedLadderMeter
        {
            Mode = MixerMeterMode.PeakRms,
            IsReady = true,
            Width = 40,
            Height = 220,
            LeftValue = -28,
            LeftRmsValue = -10,
            RightValue = -28,
            RightRmsValue = -10,
        };
        var window = Show(ladder);
        try
        {
            var withoutHold = Frame(ladder);
            var rmsTop = ladder.Cells(-10, double.NaN, ladder.CellCount).TopLit;
            var held = ladder.Cells(-28, -24, ladder.CellCount).Held;
            Assert.True(held >= 0 && held < rmsTop - 3);

            ladder.LeftPeakHold = -24;
            var withHold = Frame(ladder);
            Assert.True(Brightness(ladder, withHold, 0, held) > Brightness(ladder, withoutHold, 0, held) + 30,
                "The held peak is visible inside the bright RMS body, not hidden or relocated.");
            Assert.Equal(Brightness(ladder, withoutHold, 0, held + 1), Brightness(ladder, withHold, 0, held + 1));
            Assert.Equal(Brightness(ladder, withoutHold, 1, held), Brightness(ladder, withHold, 1, held));

            ladder.LeftValue = -36;
            var falling = Frame(ladder);
            Assert.Equal(Brightness(ladder, withHold, 0, held), Brightness(ladder, falling, 0, held));
            Assert.Equal(Brightness(ladder, withHold, 0, rmsTop - 1), Brightness(ladder, falling, 0, rmsTop - 1));
        }
        finally
        {
            window.Close();
            MeterGradient.Mixer.Use(gradient);
            Glow.Use(glow, strength);
            LedMotion.ReduceMotion = reduce;
        }

        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CombinedHold_StaysVisibleOnTheRmsCrest(bool glowOn) => HeadlessApp.RunAsync(() =>
    {
        var gradient = MeterGradient.Mixer.Gradient;
        var glow = Glow.IsOn;
        var strength = Glow.Strength;
        var reduce = LedMotion.ReduceMotion;
        Glow.Use(glowOn, strength);
        LedMotion.ReduceMotion = true;
        MeterGradient.Mixer.Use([(0.0, Colors.DodgerBlue), (1.0, Colors.DodgerBlue)]);
        var ladder = new LedLadderMeter
        {
            Mode = MixerMeterMode.PeakRms,
            IsReady = true,
            Width = 40,
            Height = 220,
            LeftValue = -10,
            LeftRmsValue = -10,
            RightValue = -10,
            RightRmsValue = -10,
        };
        var window = Show(ladder);
        try
        {
            var crest = ladder.Cells(-10, double.NaN, ladder.CellCount).TopLit;
            Assert.Equal(crest, ladder.Cells(-10, -10, ladder.CellCount).Held);
            var withoutHold = Frame(ladder);
            ladder.LeftPeakHold = -10;
            var withHold = Frame(ladder);

            var normal = Brightness(ladder, withoutHold, 0, crest);
            var held = Brightness(ladder, withHold, 0, crest);
            Assert.True(held > normal + 45, $"The held crest ({held}) must be visibly brighter than the unheld crest ({normal}).");
            Assert.True(Brightness(ladder, withHold, 0, crest, 1) >= Brightness(ladder, withoutHold, 0, crest, 1),
                "The held cell must not extinguish the crest's glow core.");
            Assert.Equal(Brightness(ladder, withoutHold, 0, crest - 1), Brightness(ladder, withHold, 0, crest - 1));
            Assert.Equal(Brightness(ladder, withoutHold, 1, crest), Brightness(ladder, withHold, 1, crest));
        }
        finally
        {
            window.Close();
            MeterGradient.Mixer.Use(gradient);
            Glow.Use(glow, strength);
            LedMotion.ReduceMotion = reduce;
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task CombinedHold_FadesSmoothlyOffTheRmsCrest() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var gradient = MeterGradient.Mixer.Gradient;
        var glow = Glow.IsOn;
        var strength = Glow.Strength;
        Glow.Use(false, strength);
        MeterGradient.Mixer.Use([(0.0, Colors.DodgerBlue), (1.0, Colors.DodgerBlue)]);
        var ladder = new LedLadderMeter
        {
            Mode = MixerMeterMode.PeakRms,
            IsReady = true,
            Width = 40,
            Height = 220,
            LeftValue = -10,
            LeftRmsValue = -10,
            LeftPeakHold = -10,
        };
        var window = Show(ladder);
        try
        {
            var crest = ladder.Cells(-10, double.NaN, ladder.CellCount).TopLit;
            var held = Brightness(ladder, Frame(ladder), 0, crest);
            clock.Time = 1000;
            ladder.LeftPeakHold = double.NaN;
            _ = Frame(ladder);
            clock.Frame(1045);
            var fading = Brightness(ladder, Frame(ladder), 0, crest);
            clock.Frame(1090);
            var unheld = Brightness(ladder, Frame(ladder), 0, crest);
            Assert.True(unheld + 20 < fading && fading < held,
                $"The held crest should fade toward the unheld crest: held {held}, fading {fading}, unheld {unheld}.");
        }
        finally
        {
            window.Close();
            MeterGradient.Mixer.Use(gradient);
            Glow.Use(glow, strength);
        }

        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CombinedHoldOnCrest_DrawsWithoutAllocatingDuringFade(bool glowOn) => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var glow = Glow.IsOn;
        var strength = Glow.Strength;
        Glow.Use(glowOn, strength);
        var ladder = new LedLadderMeter
        {
            Mode = MixerMeterMode.PeakRms,
            IsReady = true,
            Width = 40,
            Height = 220,
            LeftValue = -10,
            LeftRmsValue = -10,
            LeftPeakHold = -10,
        };
        var window = Show(ladder);
        try
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)ladder.Bounds.Width, (int)ladder.Bounds.Height));
            using var context = bitmap.CreateDrawingContext();
            ladder.Render(context);
            var cached = MeterGradient.Mixer.CachedBrushCount;
            clock.Time = 1000;
            ladder.LeftPeakHold = double.NaN;
            ladder.Render(context);
            for (var i = 0; i < 3; i++)
            {
                clock.Frame(i switch { 0 => 1020, 1 => 1045, _ => 1080 });
                var bytes = ChainMeterCostTests.InSteadyState(() => ladder.Render(context), 100);
                Assert.Equal(0, bytes);
                Assert.Equal(cached, MeterGradient.Mixer.CachedBrushCount);
            }
        }
        finally
        {
            window.Close();
            Glow.Use(glow, strength);
        }

        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task PeakBelowRms_MovesIndependentlyOfRmsAndHeldPeak(bool glowOn, bool reduceMotion) => HeadlessApp.RunAsync(() =>
    {
        var gradient = MeterGradient.Mixer.Gradient;
        var glow = Glow.IsOn;
        var strength = Glow.Strength;
        var reduce = LedMotion.ReduceMotion;
        Glow.Use(glowOn, strength);
        LedMotion.ReduceMotion = reduceMotion;
        MeterGradient.Mixer.Use([(0.0, Colors.DodgerBlue), (1.0, Colors.DodgerBlue)]);
        var ladder = new LedLadderMeter
        {
            Mode = MixerMeterMode.PeakRms,
            IsReady = true,
            Width = 40,
            Height = 220,
            LeftValue = -28,
            LeftRmsValue = -10,
            LeftPeakHold = -24,
            RightValue = -28,
            RightRmsValue = -10,
        };
        var window = Show(ladder);
        try
        {
            var oldPeak = ladder.Cells(-28, double.NaN, ladder.CellCount).TopLit;
            var newPeak = ladder.Cells(-40, double.NaN, ladder.CellCount).TopLit;
            var rms = ladder.Cells(-10, double.NaN, ladder.CellCount).TopLit;
            var held = ladder.Cells(-28, -24, ladder.CellCount).Held;
            Assert.True(newPeak >= 0 && newPeak < oldPeak && oldPeak < held && held < rms - 3);

            var before = Frame(ladder);
            ladder.LeftValue = -40;
            var after = Frame(ladder);

            Assert.True(Brightness(ladder, before, 0, oldPeak, 1) + 60 < Brightness(ladder, after, 0, oldPeak, 1),
                "The old peak's notch disappears when the current peak falls.");
            Assert.True(Brightness(ladder, after, 0, newPeak, 1) + 60 < Brightness(ladder, before, 0, newPeak, 1),
                "The new peak's notch appears at its actual cell.");
            Assert.Equal(Brightness(ladder, before, 0, oldPeak), Brightness(ladder, after, 0, oldPeak));
            Assert.Equal(Brightness(ladder, before, 0, newPeak), Brightness(ladder, after, 0, newPeak));
            Assert.Equal(Brightness(ladder, before, 0, held), Brightness(ladder, after, 0, held));
            Assert.True(Brightness(ladder, before, 0, held) > Brightness(ladder, before, 1, held) + 30);
            Assert.Equal(Brightness(ladder, before, 0, rms - 1), Brightness(ladder, after, 0, rms - 1));
            Assert.Equal(Brightness(ladder, before, 1, oldPeak, 1), Brightness(ladder, after, 1, oldPeak, 1));
        }
        finally
        {
            window.Close();
            MeterGradient.Mixer.Use(gradient);
            Glow.Use(glow, strength);
            LedMotion.ReduceMotion = reduce;
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task PeakNotch_IsDistinctFromHold_AndOnlyAppearsForARealCrossedPeak() => HeadlessApp.RunAsync(() =>
    {
        var gradient = MeterGradient.Mixer.Gradient;
        var glow = Glow.IsOn;
        var strength = Glow.Strength;
        var reduce = LedMotion.ReduceMotion;
        Glow.Use(false, strength);
        LedMotion.ReduceMotion = true;
        MeterGradient.Mixer.Use([(0.0, Colors.DodgerBlue), (1.0, Colors.DodgerBlue)]);
        var ladder = new LedLadderMeter
        {
            Mode = MixerMeterMode.PeakRms,
            IsReady = true,
            Width = 40,
            Height = 220,
            LeftValue = -28,
            LeftRmsValue = -10,
            LeftPeakHold = -29,
            RightValue = -10,
            RightRmsValue = -10,
            RightPeakHold = -29,
        };
        var window = Show(ladder);
        try
        {
            var peak = ladder.Cells(-28, double.NaN, ladder.CellCount).TopLit;
            Assert.Equal(peak, ladder.Cells(-28, -29, ladder.CellCount).Held);
            var coincident = Frame(ladder);
            Assert.Equal(Brightness(ladder, coincident, 0, peak), Brightness(ladder, coincident, 1, peak));
            Assert.True(Brightness(ladder, coincident, 0, peak, 1) + 60 < Brightness(ladder, coincident, 1, peak, 1),
                "A current peak over the held cell is a dark notch; the held cell stays bright.");

            ladder.LeftValue = -10;
            var equal = Frame(ladder);
            Assert.Equal(Brightness(ladder, equal, 0, peak, 1), Brightness(ladder, equal, 1, peak, 1));
            ladder.LeftValue = -6;
            var above = Frame(ladder);
            Assert.Equal(Brightness(ladder, above, 0, peak, 1), Brightness(ladder, above, 1, peak, 1));
            ladder.LeftValue = double.NaN;
            var missing = Frame(ladder);
            Assert.Equal(Brightness(ladder, missing, 0, peak, 1), Brightness(ladder, missing, 1, peak, 1));
            ladder.LeftValue = double.NegativeInfinity;
            var silence = Frame(ladder);
            Assert.Equal(Brightness(ladder, silence, 0, peak, 1), Brightness(ladder, silence, 1, peak, 1));

            ladder.LeftValue = -28;
            ladder.IsReady = false;
            var unready = Frame(ladder);
            ladder.LeftRmsValue = double.NaN;
            Assert.Equal(unready, Frame(ladder));
        }
        finally
        {
            window.Close();
            MeterGradient.Mixer.Use(gradient);
            Glow.Use(glow, strength);
            LedMotion.ReduceMotion = reduce;
        }

        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PeakNotch_DrawsWithoutAllocatingAsThePeakMoves(bool reduceMotion) => HeadlessApp.RunAsync(() =>
    {
        var glow = Glow.IsOn;
        var strength = Glow.Strength;
        var reduce = LedMotion.ReduceMotion;
        Glow.Use(true, strength);
        LedMotion.ReduceMotion = reduceMotion;
        var ladder = new LedLadderMeter
        {
            Mode = MixerMeterMode.PeakRms,
            IsReady = true,
            Width = 40,
            Height = 220,
            LeftValue = -28,
            LeftRmsValue = -10,
            LeftPeakHold = -24,
            RightValue = -6,
            RightRmsValue = -30,
            RightPeakHold = -3,
        };
        var window = Show(ladder);
        try
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)ladder.Bounds.Width, (int)ladder.Bounds.Height));
            using var context = bitmap.CreateDrawingContext();
            for (var i = 0; i < 2; i++)
            {
                ladder.LeftValue = i == 0 ? -28 : -40;
                ladder.Render(context);
                var bytes = ChainMeterCostTests.InSteadyState(() => ladder.Render(context), 100);
                Assert.Equal(0, bytes);
            }

            // The existing styled peak property allocates when it changes; measure the draw after each update, not the
            // setter, while alternating the notch's cell. As with the other cost tests, allow runtime warm-up rounds.
            long movingBytes = 0;
            for (var round = 0; round < 10; round++)
            {
                movingBytes = 0;
                for (var i = 0; i < 100; i++)
                {
                    ladder.LeftValue = i % 2 == 0 ? -28 : -40;
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    ladder.Render(context);
                    movingBytes += GC.GetAllocatedBytesForCurrentThread() - before;
                }

                if (round >= 2 && movingBytes == 0)
                {
                    break;
                }
            }

            Assert.Equal(0, movingBytes);
        }
        finally
        {
            window.Close();
            Glow.Use(glow, strength);
            LedMotion.ReduceMotion = reduce;
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task CrossedRmsDecay_StillFadesWhileTheCurrentPeakStaysMarked() => HeadlessApp.RunAsync(() =>
    {
        using var clock = new ManualLedClock();
        var gradient = MeterGradient.Mixer.Gradient;
        var glow = Glow.IsOn;
        var strength = Glow.Strength;
        Glow.Use(false, strength);
        MeterGradient.Mixer.Use([(0.0, Colors.DodgerBlue), (1.0, Colors.DodgerBlue)]);
        var ladder = new LedLadderMeter
        {
            Mode = MixerMeterMode.PeakRms,
            IsReady = true,
            Width = 40,
            Height = 220,
            LeftValue = -40,
            LeftRmsValue = -10,
        };
        var window = Show(ladder);
        try
        {
            var peak = ladder.Cells(-40, double.NaN, ladder.CellCount).TopLit;
            var fadingCell = ladder.Cells(-20, double.NaN, ladder.CellCount).TopLit;
            var initial = Frame(ladder);
            var bright = Brightness(ladder, initial, 0, fadingCell);
            var notch = Brightness(ladder, initial, 0, peak, 1);
            Assert.True(notch + 60 < Brightness(ladder, initial, 0, peak));

            clock.Time = 1000;
            ladder.LeftRmsValue = -30;
            _ = Frame(ladder);
            clock.Frame(1020);
            var during = Frame(ladder);
            clock.Frame(1090);
            var settled = Frame(ladder);
            Assert.True(Brightness(ladder, settled, 0, fadingCell) < Brightness(ladder, during, 0, fadingCell));
            Assert.True(Brightness(ladder, during, 0, fadingCell) < bright);
            Assert.Equal(notch, Brightness(ladder, during, 0, peak, 1));
            Assert.Equal(notch, Brightness(ladder, settled, 0, peak, 1));
        }
        finally
        {
            window.Close();
            MeterGradient.Mixer.Use(gradient);
            Glow.Use(glow, strength);
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task MissingRms_IsSingleIntensityOnThatChannel_SilenceAndUnreadyAreNotFabricated() => HeadlessApp.RunAsync(() =>
    {
        var gradient = MeterGradient.Mixer.Gradient;
        var glow = Glow.IsOn;
        var strength = Glow.Strength;
        var reduce = LedMotion.ReduceMotion;
        Glow.Use(false, strength);
        LedMotion.ReduceMotion = true;
        MeterGradient.Mixer.Use([(0.0, Colors.DodgerBlue), (1.0, Colors.DodgerBlue)]);
        var ladder = new LedLadderMeter
        {
            Mode = MixerMeterMode.PeakRms,
            IsReady = true,
            Width = 40,
            Height = 220,
            LeftValue = -6,
            RightValue = -6,
            LeftRmsValue = -36,
        };
        var window = Show(ladder);
        try
        {
            var index = ladder.Cells(-18, double.NaN, ladder.CellCount).TopLit;
            var partial = Frame(ladder);
            var dim = Brightness(ladder, partial, 0, index);
            var single = Brightness(ladder, partial, 1, index);
            Assert.True(single > dim + 80);

            ladder.LeftRmsValue = double.NegativeInfinity;
            Assert.Equal(dim, Brightness(ladder, Frame(ladder), 0, index)); // Silence is a valid RMS reading.
            ladder.LeftRmsValue = double.NaN;
            Assert.Equal(single, Brightness(ladder, Frame(ladder), 0, index)); // No reading retains the old single bar.
            ladder.LeftRmsValue = -36;
            ladder.IsReady = false;
            var unready = Frame(ladder);
            Assert.True(dim > Brightness(ladder, unready, 0, index) + 35);
            Assert.Equal(Brightness(ladder, unready, 0, index), Brightness(ladder, unready, 1, index));
        }
        finally
        {
            window.Close();
            MeterGradient.Mixer.Use(gradient);
            Glow.Use(glow, strength);
            LedMotion.ReduceMotion = reduce;
        }

        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(MixerMeterMode.Vu)]
    [InlineData(MixerMeterMode.K20)]
    [InlineData(MixerMeterMode.K14)]
    [InlineData(MixerMeterMode.K12)]
    [InlineData(MixerMeterMode.LufsS)]
    public Task OtherModes_IgnoreTheRmsInputs(MixerMeterMode mode) => HeadlessApp.RunAsync(() =>
    {
        var ladder = new LedLadderMeter
        {
            Mode = mode,
            IsReady = true,
            Width = 40,
            Height = 220,
            LeftValue = -12,
            RightValue = -18,
            LeftPeakHold = -3,
            RightPeakHold = -6,
        };
        var window = Show(ladder);
        try
        {
            var before = Frame(ladder);
            ladder.LeftRmsValue = -6;
            ladder.RightRmsValue = -9;
            Assert.Equal(before, Frame(ladder));
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task ShowFeed_DoesNotAdoptMixerRmsInputs() => HeadlessApp.RunAsync(() =>
    {
        var ladder = new LedLadderMeter { Mode = MixerMeterMode.PeakRms, IsReady = true, Width = 40, Height = 220 };
        var window = Show(ladder);
        try
        {
            ladder.Show(-12, -18, -3, -6);
            var before = Frame(ladder);
            ladder.LeftRmsValue = -36;
            ladder.RightRmsValue = -42;
            Assert.Equal(before, Frame(ladder));
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    private static Window Show(Control control)
    {
        var window = new Window { Width = 80, Height = 240, Content = control };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static byte[] Frame(LedLadderMeter ladder)
    {
        var width = (int)ladder.Bounds.Width;
        var height = (int)ladder.Bounds.Height;
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        using (var context = bitmap.CreateDrawingContext())
        {
            ladder.Render(context);
        }

        var pixels = new byte[width * height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, width * 4);
        }
        finally
        {
            handle.Free();
        }

        return pixels;
    }

    private static int Brightness(LedLadderMeter ladder, byte[] frame, int side, int index, int inset = 0)
    {
        var inner = ConsolePaint.Snap(ladder.WellRect).Deflate(2);
        var width = Math.Min(ladder.ColumnWidth, inner.Width / 2);
        var column = new Rect(side == 0 ? inner.X : inner.Right - width, inner.Y, width, inner.Height);
        var cell = LedLadderMeter.CellRect(column, index);
        var offset = ((((int)cell.Y + 1) * (int)ladder.Bounds.Width) + (int)cell.X + inset) * 4;
        return frame[offset] + frame[offset + 1] + frame[offset + 2];
    }
}
