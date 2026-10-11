using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Tests.Unit.Domain.Devices;

/// <summary>Scenario 7: loading one saved chain on several tracks creates independent device instances.</summary>
public sealed class DeviceChainPresetTests
{
    private static DeviceChain Source()
    {
        var synth = TestDevices.Instance(TestDevices.Synth)
            .WithParameter(new ParameterId(1), ControlValue.FromFraction(0.7)) with
        {
            Name = "Lead",
            State = new PluginState(ByteBlock.Copy([1, 2, 3]), "test/1"),
        };
        return DeviceChain.Create(ChainOwner.ForTrack(TrackId.New())) with
        {
            Devices = [TestDevices.Instance(TestDevices.MidiFx), synth, TestDevices.Instance(TestDevices.Filter) with { IsBypassed = true }],
        };
    }

    [Fact]
    public void Instantiate_KeepsOrderAndConfiguration()
    {
        var source = Source();
        var preset = DeviceChainPreset.From(source, "Lead");

        var loaded = preset.Instantiate(ChainOwner.ForTrack(TrackId.New()));

        Assert.Equal(source.Devices.Select(d => (d.Definition, d.Name, d.IsBypassed)), loaded.Devices.Select(d => (d.Definition, d.Name, d.IsBypassed)));
        Assert.Equal(source.Devices[1].Parameters, loaded.Devices[1].Parameters);
        Assert.Equal(source.Devices[1].State, loaded.Devices[1].State);
    }

    [Fact]
    public void LoadingTwice_CreatesIndependentInstances()
    {
        var source = Source();
        var preset = DeviceChainPreset.From(source, "Lead");
        var trackA = TrackId.New();
        var trackB = TrackId.New();

        var a = preset.Instantiate(ChainOwner.ForTrack(trackA));
        var b = preset.Instantiate(ChainOwner.ForTrack(trackB));

        var ids = source.Devices.Concat(a.Devices).Concat(b.Devices).Select(d => d.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.NotEqual(a.Id, b.Id);
        Assert.NotEqual(source.Id, a.Id);
        Assert.Equal(trackA, a.Owner.Track);
        Assert.Equal(trackB, b.Owner.Track);
    }

    [Fact]
    public void EditingOneLoadedInstance_DoesNotChangeAnother()
    {
        var preset = DeviceChainPreset.From(Source(), "Lead");
        var a = preset.Instantiate(ChainOwner.ForTrack(TrackId.New()));
        var b = preset.Instantiate(ChainOwner.ForTrack(TrackId.New()));

        var edited = a.Replace(a.Devices[1].WithParameter(new ParameterId(1), ControlValue.Min) with { IsBypassed = true });

        Assert.NotEqual(edited.Devices[1].ValueOf(new ParameterId(1)), b.Devices[1].ValueOf(new ParameterId(1)));
        Assert.False(b.Devices[1].IsBypassed);
    }

    [Fact]
    public void MovingAChain_KeepsIdentity_WhileLoadingAPresetDoesNot()
    {
        var source = Source();
        var moved = source with { Owner = ChainOwner.Rack };
        var loaded = DeviceChainPreset.From(source, "x").Instantiate(ChainOwner.Rack);

        Assert.Equal(source.Devices.Select(d => d.Id), moved.Devices.Select(d => d.Id));
        Assert.Empty(source.Devices.Select(d => d.Id).Intersect(loaded.Devices.Select(d => d.Id)));
    }

    [Fact]
    public void Duplicate_GivesANewIdentity()
    {
        var device = Source().Devices[1];

        var copy = device.Duplicate();

        Assert.NotEqual(device.Id, copy.Id);
        Assert.Equal(device.Parameters, copy.Parameters);
    }
}
