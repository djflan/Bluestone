using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>The words a junction's tooltip and a screen reader get for what it carries.</summary>
public sealed class SignalDescriptionTests
{
    [Fact]
    public void Describe_SaysWhatIsThere_InAJunctionsWords()
    {
        Assert.Equal("After Polysynth · notes held · peak -12.4 dB", SignalDescription.Describe("After Polysynth", true, true, true, Levels(rms: -20, peak: -12.4), null));
        Assert.Equal("Into the chain · no notes · silent · also sent to Strings", SignalDescription.Describe("Into the chain", true, false, true, new MeterLevels(), "Strings"));
        Assert.Equal("peak 1.0 dB, clipping", SignalDescription.Describe(null, false, false, true, Levels(rms: -3, peak: 1), null));
        Assert.Equal("silent", SignalDescription.Describe(null, false, false, true, null, null));
    }

    private static MeterLevels Levels(double rms, double peak)
    {
        var levels = new MeterLevels();
        levels.Update(new MeterReading(Linear(peak), Linear(peak), Linear(rms), Linear(rms)), 1_000_000_000);
        return levels;
    }

    private static float Linear(double decibels) => (float)Math.Pow(10, decibels / 20);
}
