using Bluestone.Domain.Midi;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Wire;
using Bluestone.Playback;
using static Bluestone.Tests.Unit.Playback.PlaybackFixture;

namespace Bluestone.Tests.Unit.Playback;

/// <summary>Metronome, count-in, immediate sends, and mapping input timestamps to song positions.</summary>
public sealed class MetronomeAndInputTimingTests
{
    private const EndpointCapabilities Immediate = EndpointCapabilities.SystemExclusive;

    // Channel 10: accent (note 76, velocity 110) and beat (note 77, velocity 80).
    private const string Accent = "994C6E";
    private const string Beat = "994D50";

    private static readonly MeterMap FourFour = MeterMap.Constant(Resolution, TimeSignature.CommonTime);
    private static readonly TempoMap OneTwenty = TempoMap.Constant(Resolution, Tempo.Default);

    private static List<string> Clicks(PlaybackFixture f) => [.. f.Sent().Where(m => m.StartsWith("99", StringComparison.Ordinal))];

    [Fact]
    public void Metronome_ClicksEveryBeatWithAnAccentedDownbeat()
    {
        using var f = new PlaybackFixture();
        f.Engine.SetMetronome(new MetronomeClick(0), enabled: true);
        f.Load();
        f.Engine.Play(Tick.Zero);

        for (var ms = 0; ms <= 2000; ms += 10)
        {
            f.PumpAt(ms);
        }

        Assert.Equal([$"{Accent}@0", $"{Beat}@500", $"{Beat}@1000", $"{Beat}@1500", $"{Accent}@2000"], Clicks(f));
        Assert.Contains("894C40@40", f.Sent());
    }

    [Fact]
    public void Metronome_StartingMidBar_ClicksFromTheNextBeat()
    {
        using var f = new PlaybackFixture(null, Immediate);
        f.Engine.SetMetronome(new MetronomeClick(0), enabled: true);
        f.Load();
        f.Engine.Play(new Tick(1700));

        for (var ms = 0; ms <= 400; ms += 5)
        {
            f.PumpAt(ms);
        }

        // Tick 2000 (the next downbeat) plays 300 ms after starting at tick 1700.
        Assert.Equal([$"{Accent}@300"], Clicks(f));
    }

    [Fact]
    public void Metronome_EnabledDuringPlayback_StartsAtTheNextBeatWithoutABurst()
    {
        using var f = new PlaybackFixture(null, Immediate);
        f.Engine.SetMetronome(new MetronomeClick(0), enabled: false);
        f.Load();
        f.Engine.Play(Tick.Zero);
        for (var ms = 0; ms <= 1200; ms += 10)
        {
            f.PumpAt(ms);
        }

        f.Engine.SetMetronome(new MetronomeClick(0), enabled: true);
        for (var ms = 1210; ms <= 1600; ms += 10)
        {
            f.PumpAt(ms);
        }

        Assert.Equal([$"{Beat}@1500"], Clicks(f));
    }

    [Fact]
    public void Metronome_FollowsTheLoop()
    {
        using var f = new PlaybackFixture(null, Immediate);
        f.Engine.SetMetronome(new MetronomeClick(0), enabled: true);
        f.Engine.SetLoop(new LoopRegion(Tick.Zero, new Tick(1000)));
        f.Load();
        f.Engine.Play(Tick.Zero);

        for (var ms = 0; ms <= 1600; ms += 10)
        {
            f.PumpAt(ms);
        }

        Assert.Equal([$"{Accent}@0", $"{Beat}@500", $"{Accent}@1000", $"{Beat}@1500"], Clicks(f));
    }

    [Fact]
    public void CountIn_ClicksABarThenPlaysFromTheStart()
    {
        using var f = new PlaybackFixture();
        f.Engine.SetMetronome(new MetronomeClick(0), enabled: false);
        f.Load(Note(0, 100));
        f.Engine.Play(Tick.Zero, CountIn.Bars(OneTwenty, FourFour, Tick.Zero, 1));

        f.PumpAt(0);
        Assert.True(f.Engine.IsCountingIn);
        Assert.Equal(Tick.Zero, f.Engine.Position);
        for (var ms = 10; ms <= 2100; ms += 10)
        {
            f.PumpAt(ms);
        }

        Assert.Equal([$"{Accent}@0", $"{Beat}@500", $"{Beat}@1000", $"{Beat}@1500"], Clicks(f));
        Assert.Contains("903C64@2000", f.Sent());
        Assert.False(f.Engine.IsCountingIn);
        Assert.Equal(new Tick(100), f.Engine.Position);
    }

    [Fact]
    public void CountIn_UsesTheMeterAndTempoAtTheStart()
    {
        var waltz = MeterMap.Constant(Resolution, new TimeSignature(3, 4));

        var countIn = CountIn.Bars(TempoMap.Constant(Resolution, Tempo.FromBeatsPerMinute(60)), waltz, Tick.Zero, 2);

        Assert.Equal(TimeSpan.FromSeconds(6), countIn.Duration);
        Assert.Equal([true, false, false, true, false, false], countIn.Clicks.Select(c => c.Downbeat));
    }

    [Fact]
    public void TryGetTickAt_MapsInputTimesToTheSongPosition()
    {
        using var f = new PlaybackFixture();
        f.Load();

        Assert.False(f.Engine.TryGetTickAt(Start, out _));
        f.Engine.Play(new Tick(1000));
        f.PumpAt(0);

        Assert.True(f.Engine.TryGetTickAt(Start + TimeSpan.FromMilliseconds(250), out var tick));
        Assert.Equal(new Tick(1250), tick);
    }

    [Fact]
    public void TryGetTickAt_DuringCountIn_MapsToThePickupBar()
    {
        using var f = new PlaybackFixture();
        f.Load();
        f.Engine.Play(new Tick(4000), CountIn.Bars(OneTwenty, FourFour, new Tick(4000), 1));
        f.PumpAt(0);

        Assert.True(f.Engine.TryGetTickAt(Start + TimeSpan.FromMilliseconds(1500), out var pickup));

        Assert.Equal(new Tick(3500), pickup);
        Assert.False(f.Engine.TryGetTickAt(Start - TimeSpan.FromSeconds(10), out _));
    }

    [Fact]
    public void TryGetTickAt_KeepsInputJustBeforeALoopWrapInThePassItWasPlayedIn()
    {
        using var f = new PlaybackFixture(null, Immediate);
        f.Engine.SetLoop(new LoopRegion(Tick.Zero, new Tick(1000)));
        f.Load();
        f.Engine.Play(Tick.Zero);
        for (var ms = 0; ms <= 1050; ms += 10)
        {
            f.PumpAt(ms);
        }

        Assert.True(f.Engine.TryGetTickAt(Start + TimeSpan.FromMilliseconds(995), out var late));
        Assert.True(f.Engine.TryGetTickAt(Start + TimeSpan.FromMilliseconds(1040), out var wrapped));

        Assert.Equal(new Tick(995), late);
        Assert.Equal(new Tick(40), wrapped);
    }

    [Fact]
    public void TryGetTickAt_FoldsInputAfterAnUnprocessedWrapIntoTheLoop()
    {
        using var f = new PlaybackFixture(null, Immediate);
        f.Engine.SetLoop(new LoopRegion(Tick.Zero, new Tick(1000)));
        f.Load();
        f.Engine.Play(Tick.Zero);
        f.PumpAt(0);
        f.PumpAt(990);
        var wraps = f.Engine.LoopWraps;

        // The wrap at 1000 ms has not been processed yet.
        Assert.True(f.Engine.TryGetTickAt(Start + TimeSpan.FromMilliseconds(1004), out var early));
        Assert.Equal(new Tick(4), early);

        f.PumpAt(1010);
        Assert.Equal(wraps + 1, f.Engine.LoopWraps);
    }

    [Fact]
    public void Seek_EndsACountIn()
    {
        using var f = new PlaybackFixture();
        f.Load();
        f.Engine.Play(Tick.Zero, CountIn.Bars(OneTwenty, FourFour, Tick.Zero, 1));
        f.PumpAt(0);

        f.Engine.Seek(new Tick(500));
        f.PumpAt(10);

        Assert.False(f.Engine.IsCountingIn);
    }

    [Fact]
    public void SendNow_DeliversImmediatelyEvenWhenStopped()
    {
        using var f = new PlaybackFixture();
        var on = ChannelMessage.NoteOn(One, NoteNumber.MiddleC, new Velocity(90));

        f.Engine.SendNow(f.Outputs[0], on);
        Assert.Equal(Timeout.InfiniteTimeSpan, f.PumpAt(3));

        Assert.Equal(["903C5A@3"], f.Sent());
        Assert.True(f.Port.Sent[0].Requested.IsImmediate);
    }
}
