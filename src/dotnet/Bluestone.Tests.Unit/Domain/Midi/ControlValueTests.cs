using Bluestone.Domain.Midi;

namespace Bluestone.Tests.Unit.Domain.Midi;

public sealed class ControlValueTests
{
    [Fact]
    public void SevenBitValues_RoundTripAndKeepTheirOrder()
    {
        var previous = ControlValue.Min;
        for (var value = 0; value <= 127; value++)
        {
            var scaled = ControlValue.FromSevenBit(value);
            Assert.Equal(value, scaled.ToSevenBit());
            Assert.True(value == 0 || scaled > previous);
            previous = scaled;
        }
    }

    [Fact]
    public void FourteenBitValues_RoundTripAndKeepTheirOrder()
    {
        var previous = ControlValue.Min;
        for (var value = 0; value <= FourteenBitValue.MaxValue; value++)
        {
            var scaled = ControlValue.FromFourteenBit(value);
            Assert.Equal(value, scaled.ToFourteenBit());
            Assert.True(value == 0 || scaled > previous);
            previous = scaled;
        }
    }

    [Fact]
    public void Scaling_KeepsMinimumCenterAndMaximum()
    {
        Assert.Equal(ControlValue.Min, ControlValue.FromSevenBit(0));
        Assert.Equal(ControlValue.Center, ControlValue.FromSevenBit(64));
        Assert.Equal(ControlValue.Max, ControlValue.FromSevenBit(127));
        Assert.Equal(ControlValue.Center, ControlValue.FromFourteenBit(FourteenBitValue.Center.Value));
        Assert.Equal(ControlValue.Max, ControlValue.FromFourteenBit(FourteenBitValue.MaxValue));
    }

    [Fact]
    public void Scaling_RejectsOutOfRangeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ControlValue.FromSevenBit(128));
        Assert.Throws<ArgumentOutOfRangeException>(() => ControlValue.FromFourteenBit(-1));
    }
}
