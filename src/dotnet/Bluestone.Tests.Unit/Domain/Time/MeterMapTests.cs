using Bluestone.Domain.Time;
using CsCheck;

namespace Bluestone.Tests.Unit.Domain.Time;

public sealed class MeterMapTests
{
    private static readonly Ppqn Resolution = new(480);

    [Theory]
    [InlineData(0, "1.1.0")]
    [InlineData(479, "1.1.479")]
    [InlineData(480, "1.2.0")]
    [InlineData(1920, "2.1.0")]
    [InlineData(2401, "2.2.1")]
    public void CommonTime_ConvertsTicksToBarsAndBeats(long tick, string expected) =>
        Assert.Equal(expected, new MeterMap(Resolution, []).ToBarBeatTick(new Tick(tick)).ToString());

    [Fact]
    public void CompoundMeter_UsesDenominatorBeats()
    {
        var map = MeterMap.Constant(Resolution, new TimeSignature(6, 8));

        Assert.Equal("1.6.0", map.ToBarBeatTick(new Tick(1200)).ToString());
        Assert.Equal("2.1.0", map.ToBarBeatTick(new Tick(1440)).ToString());
    }

    [Fact]
    public void MidBarChange_ShortensThePreviousBar()
    {
        // 4/4 for half a bar, then 3/4.
        var map = new MeterMap(Resolution, [new MeterChange(new Tick(960), new TimeSignature(3, 4))]);

        Assert.Equal("1.2.0", map.ToBarBeatTick(new Tick(480)).ToString());
        Assert.Equal("2.1.0", map.ToBarBeatTick(new Tick(960)).ToString());
        Assert.Equal("3.1.0", map.ToBarBeatTick(new Tick(960 + 1440)).ToString());
        Assert.False(map.TryGetTick(new BarBeatTick(1, 3, 0), out _));
        Assert.True(map.TryGetTick(new BarBeatTick(2, 3, 479), out var tick));
        Assert.Equal(new Tick(960 + 960 + 479), tick);
    }

    [Fact]
    public void TryGetTick_RejectsBeatsAndTicksOutsideTheBar()
    {
        var map = new MeterMap(Resolution, []);

        Assert.False(map.TryGetTick(new BarBeatTick(1, 5, 0), out _));
        Assert.False(map.TryGetTick(new BarBeatTick(1, 1, 480), out _));
    }

    [Fact]
    public void BarStart_FindsContainingBar()
    {
        var map = new MeterMap(Resolution, [new MeterChange(new Tick(960), new TimeSignature(3, 4))]);

        Assert.Equal(Tick.Zero, map.BarStart(new Tick(959)));
        Assert.Equal(new Tick(960), map.BarStart(new Tick(961)));
        Assert.Equal(new Tick(960 + 1440), map.BarStart(new Tick(960 + 1440 + 5)));
    }

    [Fact]
    public void NextBarStart_StopsAtAMidBarChange()
    {
        var map = new MeterMap(Resolution, [new MeterChange(new Tick(960), new TimeSignature(3, 4))]);

        Assert.Equal(new Tick(960), map.NextBarStart(Tick.Zero));
        Assert.Equal(new Tick(960), map.NextBarStart(new Tick(959)));
        Assert.Equal(new Tick(960 + 1440), map.NextBarStart(new Tick(960)));
    }

    [Fact]
    public void Constructor_RejectsMetersWithFractionalBeats() =>
        Assert.Throws<ArgumentException>(() => MeterMap.Constant(new Ppqn(1), new TimeSignature(3, 8)));

    [Fact]
    public void Constructor_RejectsUninitializedSignature() =>
        Assert.Throws<ArgumentException>(() => new MeterMap(Resolution, [new MeterChange(Tick.Zero, default)]));

    private static readonly Gen<MeterMap> GenMap =
        Gen.Select(Gen.Long[0, 20_000], Gen.Int[1, 13], Gen.Int[0, 4]).Array[0, 8]
            .Select(changes => new MeterMap(Resolution, changes.Select(c => new MeterChange(new Tick(c.Item1), TimeSignature.FromExponent(c.Item2, c.Item3)))));

    [Fact]
    public void BarBeatTick_RoundTrips() =>
        Gen.Select(GenMap, Gen.Long[0, 100_000]).Sample((map, tick) =>
            map.TryGetTick(map.ToBarBeatTick(new Tick(tick)), out var back) && back == new Tick(tick));

    [Fact]
    public void NextBarStart_IsTheStartOfTheNextBarNumber() =>
        Gen.Select(GenMap, Gen.Long[0, 100_000]).Sample((map, tick) =>
            map.TryGetTick(new BarBeatTick(map.ToBarBeatTick(new Tick(tick)).Bar + 1, 1, 0), out var next)
            && map.NextBarStart(new Tick(tick)) == next);
}
