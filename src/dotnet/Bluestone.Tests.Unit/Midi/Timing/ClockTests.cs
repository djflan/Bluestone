using Bluestone.Midi.Timing;

namespace Bluestone.Tests.Unit.Midi.Timing;

public sealed class ClockTests
{
    [Fact]
    public void VirtualClock_MovesOnlyWhenAdvanced()
    {
        var clock = new VirtualClock(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(1), clock.Now);
        clock.Advance(TimeSpan.FromMilliseconds(5));
        Assert.Equal(TimeSpan.FromMilliseconds(1005), clock.Now);
        clock.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), clock.Now);
    }

    [Fact]
    public void VirtualClock_CannotMoveBackwards()
    {
        var clock = new VirtualClock(TimeSpan.FromSeconds(1));

        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.AdvanceTo(TimeSpan.Zero));
    }

    [Fact]
    public void SystemClock_IsMonotonic()
    {
        var previous = SystemMonotonicClock.Instance.Now;
        for (var i = 0; i < 1000; i++)
        {
            var now = SystemMonotonicClock.Instance.Now;
            Assert.True(now >= previous);
            previous = now;
        }
    }
}
