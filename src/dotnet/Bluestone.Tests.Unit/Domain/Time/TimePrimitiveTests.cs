using Bluestone.Domain.Time;

namespace Bluestone.Tests.Unit.Domain.Time;

public sealed class TimePrimitiveTests
{
    [Fact]
    public void Tick_ArithmeticWithSpans()
    {
        var start = new Tick(100);
        var span = new TickSpan(40);

        Assert.Equal(new Tick(140), start + span);
        Assert.Equal(new Tick(60), start - span);
        Assert.Equal(new TickSpan(40), new Tick(140) - start);
    }

    [Fact]
    public void Tick_RejectsNegativeResults()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Tick(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Tick(5) - new TickSpan(6));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Tick(5) - new Tick(6));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TickSpan(-1));
        Assert.Throws<OverflowException>(() => new Tick(long.MaxValue) + new TickSpan(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32768)]
    public void Ppqn_RejectsOutOfRange(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Ppqn(value));

    [Fact]
    public void Ppqn_DefaultIsInvalidUntilInitialized()
    {
        Assert.False(default(Ppqn).IsValid);
        Assert.Throws<ArgumentException>(() => TempoMap.Constant(default, Tempo.Default));
    }

    [Theory]
    [InlineData(120.0, 500_000)]
    [InlineData(60.0, 1_000_000)]
    [InlineData(140.0, 428_571)]
    public void Tempo_ConvertsBeatsPerMinute(double bpm, int microseconds)
    {
        var tempo = Tempo.FromBeatsPerMinute(bpm);

        Assert.Equal(microseconds, tempo.MicrosecondsPerQuarterNote);
        Assert.Equal(bpm, tempo.BeatsPerMinute, precision: 3);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-10.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1.0)] // 60,000,000 µs exceeds the 24-bit MIDI file limit
    public void Tempo_RejectsUnrepresentableBeatsPerMinute(double bpm) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Tempo.FromBeatsPerMinute(bpm));

    [Fact]
    public void Tempo_FormatsWithInvariantCulture() =>
        Assert.Equal("120 BPM", Tempo.Default.ToString());

    [Theory]
    [InlineData(4, 4, 480, 480)]
    [InlineData(6, 8, 480, 240)]
    [InlineData(2, 2, 480, 960)]
    [InlineData(7, 16, 96, 24)]
    public void TimeSignature_ComputesTicksPerBeat(int numerator, int denominator, int ppqn, long expected)
    {
        Assert.True(new TimeSignature(numerator, denominator).TryGetTicksPerBeat(new Ppqn(ppqn), out var beat));
        Assert.Equal(expected, beat.Value);
    }

    [Fact]
    public void TimeSignature_ReportsNonIntegralBeats() =>
        Assert.False(new TimeSignature(4, 8).TryGetTicksPerBeat(new Ppqn(1), out _));

    [Theory]
    [InlineData(0, 4)]
    [InlineData(256, 4)]
    [InlineData(4, 3)]
    [InlineData(4, 0)]
    [InlineData(4, 256)]
    public void TimeSignature_RejectsInvalidParts(int numerator, int denominator) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeSignature(numerator, denominator));

    [Fact]
    public void TimeSignature_RoundTripsExponent()
    {
        var signature = TimeSignature.FromExponent(6, 3);

        Assert.Equal(new TimeSignature(6, 8), signature);
        Assert.Equal(3, signature.DenominatorExponent);
        Assert.Equal("6/8", signature.ToString());
    }
}
