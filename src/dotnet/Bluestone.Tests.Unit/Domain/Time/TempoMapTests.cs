using Bluestone.Domain.Time;
using CsCheck;

namespace Bluestone.Tests.Unit.Domain.Time;

public sealed class TempoMapTests
{
    private static readonly Ppqn Resolution = new(960);

    [Fact]
    public void EmptyChanges_DefaultTo120Bpm()
    {
        var map = new TempoMap(Resolution, []);

        Assert.Equal([new TempoChange(Tick.Zero, Tempo.Default)], map.Changes);
        Assert.Equal(TimeSpan.FromMilliseconds(500), map.TimeAt(new Tick(960)));
    }

    [Fact]
    public void TimeAt_AccumulatesAcrossSegments()
    {
        var map = new TempoMap(Resolution, [new TempoChange(new Tick(960), Tempo.FromBeatsPerMinute(60))]);

        Assert.Equal(TimeSpan.FromSeconds(0.5), map.TimeAt(new Tick(960)));
        Assert.Equal(TimeSpan.FromSeconds(1.5), map.TimeAt(new Tick(1920)));
        Assert.Equal(Tempo.FromBeatsPerMinute(60), map.TempoAt(new Tick(1920)));
        Assert.Equal(Tempo.Default, map.TempoAt(new Tick(959)));
    }

    [Fact]
    public void LaterChangeAtSameTick_Wins()
    {
        var map = new TempoMap(Resolution, [
            new TempoChange(new Tick(10), new Tempo(400_000)),
            new TempoChange(Tick.Zero, new Tempo(600_000)),
            new TempoChange(new Tick(10), new Tempo(300_000)),
        ]);

        Assert.Equal([new TempoChange(Tick.Zero, new Tempo(600_000)), new TempoChange(new Tick(10), new Tempo(300_000))], map.Changes);
    }

    [Fact]
    public void TimeAt_FloorsToHundredNanoseconds()
    {
        // One tick at 960 PPQN and 120 BPM is 520.8333… µs.
        var map = TempoMap.Constant(Resolution, Tempo.Default);

        Assert.Equal(TimeSpan.FromTicks(5208), map.TimeAt(new Tick(1)));
        Assert.Equal(TimeSpan.FromTicks(10416), map.TimeAt(new Tick(2)));
    }

    [Fact]
    public void TickAt_InvertsTimeAt()
    {
        var map = new TempoMap(Resolution, [new TempoChange(new Tick(960), Tempo.FromBeatsPerMinute(60))]);

        Assert.Equal(Tick.Zero, map.TickAt(TimeSpan.Zero));
        Assert.Equal(new Tick(960), map.TickAt(TimeSpan.FromSeconds(0.5)));
        Assert.Equal(new Tick(959), map.TickAt(TimeSpan.FromSeconds(0.5) - TimeSpan.FromTicks(1)));
        Assert.Equal(new Tick(1920), map.TickAt(TimeSpan.FromSeconds(1.5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.TickAt(TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void With_AddsOrReplacesChange()
    {
        var map = TempoMap.Constant(Resolution, Tempo.Default).With(new TempoChange(Tick.Zero, new Tempo(250_000)));

        Assert.Equal([new TempoChange(Tick.Zero, new Tempo(250_000))], map.Changes);
    }

    private static readonly Gen<TempoMap> GenMap =
        Gen.Select(
            Gen.Int[1, 2000],
            Gen.Select(Gen.Long[0, 200_000], Gen.Int[1, Tempo.MaxMicrosecondsPerQuarterNote]).Array[0, 12],
            (ppqn, changes) => new TempoMap(new Ppqn(ppqn), changes.Select(c => new TempoChange(new Tick(c.Item1), new Tempo(c.Item2)))));

    [Fact]
    public void TimeAt_IsMonotonic() =>
        Gen.Select(GenMap, Gen.Long[0, 1_000_000], Gen.Long[0, 1_000_000]).Sample((map, a, b) =>
        {
            var (low, high) = a <= b ? (a, b) : (b, a);
            return map.TimeAt(new Tick(low)) <= map.TimeAt(new Tick(high));
        });

    [Fact]
    public void TickAt_IsTheLastTickAtOrBeforeTime() =>
        Gen.Select(GenMap, Gen.Long[0, 100_000_000_000]).Sample((map, hundredNs) =>
        {
            var elapsed = TimeSpan.FromTicks(hundredNs);
            var tick = map.TickAt(elapsed);
            return map.TimeAt(tick) <= elapsed && map.TimeAt(new Tick(tick.Value + 1)) > elapsed;
        });

    [Fact]
    public void TickAt_RoundTripsWhenTicksAreAtLeast100ns() =>
        Gen.Select(Gen.Int[1, 960], Gen.Int[100_000, 2_000_000], Gen.Long[0, 10_000_000]).Sample((ppqn, tempo, tick) =>
        {
            var map = TempoMap.Constant(new Ppqn(ppqn), new Tempo(tempo));
            return map.TickAt(map.TimeAt(new Tick(tick))) == new Tick(tick);
        });

    [Fact]
    public void TimeAt_MatchesExactRationalComputation() =>
        Gen.Select(GenMap, Gen.Long[0, 1_000_000]).Sample((map, tick) =>
        {
            decimal microsecondTicks = 0;
            for (var i = 0; i < map.Changes.Length; i++)
            {
                var start = map.Changes[i].Position.Value;
                if (start >= tick)
                {
                    break;
                }

                var end = i + 1 < map.Changes.Length ? Math.Min(map.Changes[i + 1].Position.Value, tick) : tick;
                microsecondTicks += (decimal)(end - start) * map.Changes[i].Tempo.MicrosecondsPerQuarterNote;
            }

            var expected = (long)decimal.Floor(microsecondTicks * 10 / map.Ppqn.TicksPerQuarterNote);
            return map.TimeAt(new Tick(tick)).Ticks == expected;
        });
}
