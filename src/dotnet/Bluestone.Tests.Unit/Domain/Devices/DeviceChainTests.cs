using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Tests.Unit.Domain.Devices;

public sealed class DeviceChainTests
{
    private static DeviceChain Chain(params DeviceDefinition[] definitions) =>
        DeviceChain.Create(ChainOwner.ForTrack(TrackId.New())) with { Devices = [.. definitions.Select(TestDevices.Instance)] };

    [Fact]
    public void Devices_KeepTheirOrder()
    {
        var chain = Chain(TestDevices.MidiFx, TestDevices.Synth, TestDevices.Filter, TestDevices.Delay);

        Assert.Equal(["MIDI FX", "Synth", "Filter", "Delay"], chain.Devices.Select(d => d.DisplayName));
    }

    [Fact]
    public void Move_ChangesPositionAndKeepsIdentity()
    {
        var chain = Chain(TestDevices.MidiFx, TestDevices.Synth, TestDevices.Filter, TestDevices.Delay);
        var filter = chain.Devices[2];

        var moved = chain.Move(filter.Id, 0);

        Assert.Equal(["Filter", "MIDI FX", "Synth", "Delay"], moved.Devices.Select(d => d.DisplayName));
        Assert.Equal(filter, moved.Devices[0]);
        Assert.Equal(0, moved.IndexOf(filter.Id));
        Assert.Equal(chain.Id, moved.Id);
    }

    [Fact]
    public void Move_ToTheSamePosition_ReturnsTheSameChain()
    {
        var chain = Chain(TestDevices.MidiFx, TestDevices.Synth);

        Assert.Same(chain, chain.Move(chain.Devices[1].Id, 1));
    }

    [Fact]
    public void Move_RejectsUnknownDevicesAndBadPositions()
    {
        var chain = Chain(TestDevices.MidiFx, TestDevices.Synth);

        Assert.Throws<KeyNotFoundException>(() => chain.Move(DeviceId.New(), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => chain.Move(chain.Devices[0].Id, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => chain.Move(chain.Devices[0].Id, -1));
    }

    [Fact]
    public void Insert_AndRemove_LeaveOtherDevicesAlone()
    {
        var chain = Chain(TestDevices.MidiFx, TestDevices.Filter);
        var synth = TestDevices.Instance(TestDevices.Synth);

        var inserted = chain.Insert(1, synth);
        var removed = inserted.Remove(chain.Devices[0].Id);

        Assert.Equal(["MIDI FX", "Synth", "Filter"], inserted.Devices.Select(d => d.DisplayName));
        Assert.Equal(["Synth", "Filter"], removed.Devices.Select(d => d.DisplayName));
        Assert.Same(removed, removed.Remove(DeviceId.New()));
    }

    [Fact]
    public void DuplicateDevice_IsRejected()
    {
        var chain = Chain(TestDevices.Synth);

        Assert.Throws<ArgumentException>(() => chain.Add(chain.Devices[0]));
    }

    [Fact]
    public void Replace_UpdatesInPlace()
    {
        var chain = Chain(TestDevices.MidiFx, TestDevices.Synth);
        var bypassed = chain.Devices[1] with { IsBypassed = true };

        var replaced = chain.Replace(bypassed);

        Assert.True(replaced.Devices[1].IsBypassed);
        Assert.Equal(chain.Devices[0], replaced.Devices[0]);
        Assert.Throws<KeyNotFoundException>(() => chain.Replace(TestDevices.Instance(TestDevices.Delay)));
    }

    [Fact]
    public void Chain_HasExactlyOneOwner_AndKeepsItsIdentityWhenOwnershipMoves()
    {
        var track = TrackId.New();
        var chain = DeviceChain.Create(ChainOwner.ForTrack(track));

        var moved = chain with { Owner = ChainOwner.Rack };

        Assert.Equal(ChainOwnerKind.Track, chain.Owner.Kind);
        Assert.Equal(track, chain.Owner.Track);
        Assert.Equal(ChainOwnerKind.Rack, moved.Owner.Kind);
        Assert.Equal(chain.Id, moved.Id);
    }

    [Fact]
    public void Parameters_AreStoredSortedAndUnique()
    {
        var device = TestDevices.Instance(TestDevices.Synth)
            .WithParameter(new ParameterId(9), ControlValue.Max)
            .WithParameter(new ParameterId(1), ControlValue.Center)
            .WithParameter(new ParameterId(9), ControlValue.Min);

        Assert.Equal([1u, 9u], device.Parameters.Select(p => p.Id.Value));
        Assert.Equal(ControlValue.Min, device.ValueOf(new ParameterId(9)));
        Assert.Null(device.ValueOf(new ParameterId(2)));
        Assert.Throws<ArgumentException>(() => device with { Parameters = [new(new ParameterId(1), ControlValue.Min), new(new ParameterId(1), ControlValue.Max)] });
    }
}

public sealed class DeviceDefinitionTests
{
    [Fact]
    public void Category_FollowsWhatTheDeviceTakesInAndPutsOut()
    {
        Assert.Equal(DeviceCategory.MidiEffect, TestDevices.MidiFx.Category);
        Assert.Equal(DeviceCategory.Instrument, TestDevices.Synth.Category);
        Assert.Equal(DeviceCategory.AudioEffect, TestDevices.Filter.Category);
    }

    [Fact]
    public void Parameters_ConvertBetweenPlainAndStoredValues()
    {
        var cutoff = TestDevices.Filter.FindParameter(new ParameterId(1))!;

        Assert.Equal(1000, cutoff.ToPlain(cutoff.ToStored(1000)), 3);
        Assert.Equal(cutoff.Minimum, cutoff.ToPlain(ControlValue.Min));
        Assert.Equal(cutoff.Maximum, cutoff.ToPlain(ControlValue.Max));
        Assert.Equal(cutoff.ToStored(cutoff.Default), cutoff.DefaultValue);
    }

    [Fact]
    public void SteppedParameters_SnapToTheirSteps()
    {
        var rate = TestDevices.MidiFx.FindParameter(new ParameterId(1))!;

        Assert.Equal(8, rate.ToPlain(rate.ToStored(8.2)), 9);
        Assert.Equal(16, rate.ToPlain(ControlValue.Max));
    }

    [Fact]
    public void InvalidParameters_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new ParameterDescriptor(new ParameterId(1), "x", 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParameterDescriptor(new ParameterId(1), "x", 0, 1, 2));
        Assert.Throws<ArgumentException>(() => TestDevices.Synth with { Parameters = [TestDevices.Synth.Parameters[0], TestDevices.Synth.Parameters[0]] });
    }

    [Fact]
    public void FractionConversion_ClampsAndRoundTrips()
    {
        Assert.Equal(ControlValue.Min, ControlValue.FromFraction(-3));
        Assert.Equal(ControlValue.Max, ControlValue.FromFraction(7));
        Assert.Equal(ControlValue.Min, ControlValue.FromFraction(double.NaN));
        Assert.Equal(0.25, ControlValue.FromFraction(0.25).ToFraction(), 9);
    }
}
