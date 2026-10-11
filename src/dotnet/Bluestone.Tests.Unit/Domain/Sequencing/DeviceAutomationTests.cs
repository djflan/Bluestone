using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using static Bluestone.Tests.Unit.Domain.Routing.RoutingFixture;

namespace Bluestone.Tests.Unit.Domain.Sequencing;

/// <summary>Scenario 5: automation targets device parameters, independent of order and of MIDI.</summary>
public sealed class DeviceAutomationTests
{
    private static readonly Ppqn Resolution = new(480);

    private static AutomationLane Lane(DeviceInstance device, uint parameter, params (long Tick, double Value)[] points) =>
        new(AutomationLaneId.New(), AutomationTarget.ForDevice(device.Id, new ParameterId(parameter)),
            points.Select(p => new AutomationPoint(new Tick(p.Tick), ControlValue.FromFraction(p.Value), AutomationCurve.Hold)));

    [Fact]
    public void ADeviceTarget_IsNotMidi_AndCannotBecomeAnEvent()
    {
        var target = AutomationTarget.ForDevice(DeviceId.New(), new ParameterId(7));

        Assert.False(target.IsMidi);
        Assert.True(target.IsValid);
        Assert.Throws<InvalidOperationException>(() => target.CreateEvent(Tick.Zero, ControlValue.Max));
        Assert.Equal(target, target.WithChannel(MidiChannel.FromNumber(9)));
        Assert.Throws<ArgumentException>(() => AutomationTarget.ForDevice(default, new ParameterId(1)));
    }

    [Fact]
    public void Targets_AreEqualOnlyForTheSameDeviceAndParameter()
    {
        var device = DeviceId.New();

        Assert.Equal(AutomationTarget.ForDevice(device, new ParameterId(1)), AutomationTarget.ForDevice(device, new ParameterId(1)));
        Assert.NotEqual(AutomationTarget.ForDevice(device, new ParameterId(1)), AutomationTarget.ForDevice(device, new ParameterId(2)));
        Assert.NotEqual(AutomationTarget.ForDevice(device, new ParameterId(1)), AutomationTarget.ForDevice(DeviceId.New(), new ParameterId(1)));
        Assert.NotEqual(AutomationTarget.ForDevice(device, new ParameterId(1)), AutomationTarget.ForPitchBend(One));
    }

    [Fact]
    public void TwoLanesOnOneDeviceParameter_AreRejected_ButDifferentParametersAreFine()
    {
        var device = TestDevices.Instance(TestDevices.Filter);

        Assert.Throws<ArgumentException>(() => Track.Create("t").WithAutomation([Lane(device, 1, (0, 0.1)), Lane(device, 1, (0, 0.9))]));
        Assert.Equal(2, Track.Create("t").WithAutomation([Lane(device, 1, (0, 0.1)), Lane(device, 2, (0, 0.9))]).Automation.Length);
    }

    [Fact]
    public void DeviceLanes_RenderAsParameterChanges_NotAsMidiEvents()
    {
        var fx = TestDevices.Instance(TestDevices.MidiFx);
        var synth = TestDevices.Instance(TestDevices.Synth);
        var filter = TestDevices.Instance(TestDevices.Filter);
        var cc = AutomationLane.Create(AutomationTarget.ForController(One, ControllerNumber.ChannelVolume)).WithPoint(new AutomationPoint(Tick.Zero, ControlValue.FromSevenBit(64)));
        var track = Track.FromEvents(TrackId.New(), "t", [Note(0)]).WithAutomation([
            Lane(fx, 1, (0, 0.2), (480, 0.4)),
            Lane(synth, 1, (100, 0.6)),
            Lane(filter, 1, (0, 0.8)),
            cc,
        ]);

        var rendered = TrackRendering.Render(track, Resolution);

        // Only the note and the controller lane become events; the three device lanes never touch MIDI.
        Assert.Equal(2, rendered.Events.Length);
        Assert.Contains(rendered.Events, e => e is NoteEvent);
        Assert.Contains(rendered.Events, e => e is ControllerEvent);

        // A lane holds its first value from tick 0, so the synth lane starts there, not at its first point.
        var expected = new HashSet<(DeviceId, long)> { (fx.Id, 0), (fx.Id, 480), (synth.Id, 0), (filter.Id, 0) };
        Assert.Equal(expected.Count, rendered.ParameterChanges.Length);
        Assert.True(expected.SetEquals(rendered.ParameterChanges.Select(c => (c.Device, c.Position.Value))));
        Assert.Equal(0, rendered.DroppedLanes);
    }

    [Fact]
    public void EachDeviceLane_DrivesItsOwnDeviceParameter()
    {
        var fx = TestDevices.Instance(TestDevices.MidiFx);
        var synth = TestDevices.Instance(TestDevices.Synth);
        var filter = TestDevices.Instance(TestDevices.Filter);
        var track = Track.Create("t").WithAutomation([Lane(fx, 1, (0, 0.25)), Lane(synth, 1, (0, 0.5)), Lane(filter, 1, (0, 0.75))]);

        var changes = TrackRendering.Render(track, Resolution).ParameterChanges;

        Assert.Equal(3, changes.Length);
        Assert.Equal(0.25, changes.Single(c => c.Device == fx.Id).Value.ToFraction(), 6);
        Assert.Equal(0.5, changes.Single(c => c.Device == synth.Id).Value.ToFraction(), 6);
        Assert.Equal(0.75, changes.Single(c => c.Device == filter.Id).Value.ToFraction(), 6);
    }

    [Fact]
    public void LinearDeviceLanes_KeepResolutionThatAMidiControllerWouldDiscard()
    {
        // The two ends differ by about a millionth of full scale: invisible at 7 bits, real for a device.
        var start = new ControlValue(0x8000_0000);
        var end = new ControlValue(0x8000_0FA0);
        var filter = TestDevices.Instance(TestDevices.Filter);
        var asDevice = new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForDevice(filter.Id, new ParameterId(1)), [new AutomationPoint(Tick.Zero, start), new AutomationPoint(new Tick(480), end)]);
        var asController = new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForController(One, ControllerNumber.ChannelVolume), [new AutomationPoint(Tick.Zero, start), new AutomationPoint(new Tick(480), end)]);

        var changes = TrackRendering.Render(Track.Create("t").WithAutomation([asDevice]), Resolution).ParameterChanges;
        var events = TrackRendering.Render(Track.Create("t").WithAutomation([asController]), Resolution).Events;

        Assert.Single(events);
        Assert.True(changes.Length > 10);
        Assert.Equal(changes.Length, changes.Select(c => c.Value).Distinct().Count());
        Assert.Equal(end, changes[^1].Value);
    }

    [Fact]
    public void ReorderingDevices_ChangesNoAutomationTarget()
    {
        var chain = DeviceChain.Create(ChainOwner.Rack) with
        {
            Devices = [TestDevices.Instance(TestDevices.MidiFx), TestDevices.Instance(TestDevices.Synth), TestDevices.Instance(TestDevices.Filter), TestDevices.Instance(TestDevices.Delay)],
        };
        var lanes = chain.Devices.Select(d => Lane(d, 1, (0, 0.5))).ToList();
        var track = Track.Create("t").WithAutomation(lanes);

        var reordered = chain.Move(chain.Devices[2].Id, 0).Move(chain.Devices[0].Id, 3);

        Assert.NotEqual(chain.Devices.Select(d => d.Id), reordered.Devices.Select(d => d.Id));
        Assert.Equal(lanes.Select(l => l.Target), track.Automation.Select(l => l.Target));
        Assert.All(track.Automation, lane => Assert.NotNull(reordered.Find(lane.Target.Device)));
    }

    [Fact]
    public void FirstChannel_IgnoresDeviceLanes()
    {
        var filter = TestDevices.Instance(TestDevices.Filter);
        var track = Track.Create("t").WithAutomation([Lane(filter, 1, (0, 0.5)), AutomationLane.Create(AutomationTarget.ForPitchBend(MidiChannel.FromNumber(7)))]);

        Assert.Equal(MidiChannel.FromNumber(7), track.FirstChannel);
        Assert.Null(Track.Create("t").WithAutomation([Lane(filter, 1, (0, 0.5))]).FirstChannel);
    }
}
