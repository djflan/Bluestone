using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;

namespace Bluestone.Tests.Unit.Domain.Devices;

public sealed class DeviceInstanceTests
{
    [Fact]
    public void ValueOf_FindsEveryStoredParameter_AndNothingElse()
    {
        // Out of order, sparse, and including the extremes of the id range.
        uint[] ids = [uint.MaxValue, 0, 7, 3, 1_000_000, 42, 2];
        var device = TestDevices.Instance(TestDevices.Synth)
            .WithParameters(ids.Select(id => new ParameterValue(new ParameterId(id), ControlValue.FromFraction((id % 100) / 100.0))));

        foreach (var id in ids)
        {
            Assert.Equal(ControlValue.FromFraction((id % 100) / 100.0), device.ValueOf(new ParameterId(id)));
        }

        foreach (var missing in new uint[] { 1, 4, 41, 43, 999_999, uint.MaxValue - 1 })
        {
            Assert.Null(device.ValueOf(new ParameterId(missing)));
        }

        Assert.Null(TestDevices.Instance(TestDevices.Synth).ValueOf(new ParameterId(0)));
    }

    [Fact]
    public void WithParameters_KeepsOtherValues_AndTheLastValueOfARepeatedParameterWins()
    {
        var device = TestDevices.Instance(TestDevices.Synth)
            .WithParameter(new ParameterId(1), ControlValue.Min)
            .WithParameter(new ParameterId(2), ControlValue.Min);

        var changed = device.WithParameters([
            new ParameterValue(new ParameterId(2), ControlValue.Center),
            new ParameterValue(new ParameterId(3), ControlValue.Max),
            new ParameterValue(new ParameterId(2), ControlValue.Max),
        ]);

        Assert.Equal([1u, 2u, 3u], changed.Parameters.Select(p => p.Id.Value));
        Assert.Equal([ControlValue.Min, ControlValue.Max, ControlValue.Max], changed.Parameters.Select(p => p.Value));
        Assert.Equal(ControlValue.Min, device.ValueOf(new ParameterId(2)));
    }
}
