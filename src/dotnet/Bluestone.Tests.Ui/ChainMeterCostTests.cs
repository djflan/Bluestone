using System.Reflection;
using Avalonia.Controls;
using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// What the Device View's chain meters cost per level update (ADR 0043: the warm meter path allocates nothing). A level
/// update runs the junction's or out meter's frame handler and feeds its ladder; once warm, that must allocate nothing,
/// whether the levels move or hold still, under either level style. Bytes are this thread's managed allocations
/// (<see cref="GC.GetAllocatedBytesForCurrentThread"/>). Each test writes what it measured to its output.
/// </summary>
public sealed class ChainMeterCostTests
{
    private const long Now = 1_000_000_000;
    private const int Updates = 1000;

    private static readonly FieldInfo HoveredField = typeof(ChainJunction).GetField("_hovered", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ChainJunction._hovered was not found.");

    public static TheoryData<JunctionLevelStyle> Styles => [JunctionLevelStyle.RmsAndPeak, JunctionLevelStyle.Peak];

    [Theory]
    [MemberData(nameof(Styles))]
    public Task Junction_UpdatePath_AllocatesNothing(JunctionLevelStyle style) => HeadlessApp.RunAsync(() =>
    {
        var signal = Carrying();
        var junction = new ChainJunction { Signal = signal, LevelStyle = style, Width = 48, Height = 220 };
        var window = Show(junction);
        try
        {
            var (steady, moving) = Measure(signal);

            // Under the pointer the tooltip follows the levels, but only when they change.
            HoveredField.SetValue(junction, true);
            var hoveredSteady = InSteadyState(() => signal.Levels.Update(Reading(rms: -12, peak: -6), Now), Updates);
            HoveredField.SetValue(junction, false);

            Report($"ChainJunction ({style}), bytes per {Updates} updates: steady {steady}, moving {moving}, hovered and steady {hoveredSteady}");
            Assert.Equal(0, steady);
            Assert.Equal(0, moving);
            Assert.Equal(0, hoveredSteady);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Theory]
    [MemberData(nameof(Styles))]
    public Task OutMeter_UpdatePath_AllocatesNothing(JunctionLevelStyle style) => HeadlessApp.RunAsync(() =>
    {
        var signal = Carrying();
        var meter = new ChainOutMeter { Signal = signal, LevelStyle = style, Width = 80, Height = 220 };
        var window = Show(meter);
        try
        {
            var (steady, moving) = Measure(signal);
            Report($"ChainOutMeter ({style}), bytes per {Updates} updates: steady {steady}, moving {moving}");
            Assert.Equal(0, steady);
            Assert.Equal(0, moving);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    // Steady: the same reading every update. Moving: every update changes the levels (the held peak stays at the louder one).
    private static (long Steady, long Moving) Measure(JunctionSignal signal)
    {
        var steadyReading = Reading(rms: -12, peak: -6);
        var steady = InSteadyState(() => signal.Levels.Update(steadyReading, Now), Updates);
        var low = Reading(rms: -30, peak: -20);
        var high = Reading(rms: -6, peak: -1.5);
        var flip = false;
        var moving = InSteadyState(
            () =>
            {
                flip = !flip;
                signal.Levels.Update(flip ? high : low, Now);
            },
            Updates);
        return (steady, moving);
    }

    private static JunctionSignal Carrying()
    {
        var signal = new JunctionSignal();
        (typeof(JunctionSignal).GetProperty(nameof(JunctionSignal.CanCarryAudio)) ?? throw new InvalidOperationException("JunctionSignal.CanCarryAudio was not found."))
            .SetValue(signal, true);
        signal.Levels.Update(Reading(rms: -9, peak: -3), Now);
        return signal;
    }

    private static Window Show(Control control)
    {
        var window = new Window { Width = 200, Height = 300, Content = control };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    // As Bluestone.Tests.Unit's Allocations.InSteadyState: the last of up to ten rounds, stopping at the first from the third
    // on that allocates nothing, so one-time runtime work (a tier-up) cannot fail a path that does not allocate.
    private static long InSteadyState(Action step, int steps)
    {
        long allocated = 0;
        for (var round = 0; round < 10; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < steps; i++)
            {
                step();
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (round >= 2 && allocated == 0)
            {
                break;
            }
        }

        return allocated;
    }

    private static void Report(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);

    private static MeterReading Reading(double rms, double peak) => new(Linear(peak), Linear(peak), Linear(rms), Linear(rms));

    private static float Linear(double decibels) => (float)Math.Pow(10, decibels / 20);
}
