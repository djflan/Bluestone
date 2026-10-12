using Bluestone.Application.Editing;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Presentation;

namespace Bluestone.Tests.Unit.Application;

/// <summary>Replacing a device in a chain: position, identity, automation, taps, the audio path, undo, and drop targets.</summary>
public sealed class ReplaceDeviceTests
{
    private static (EditHistory History, TrackId Track, DeviceInstance Fx, DeviceInstance Synth) Chain()
    {
        var track = Track.Create("t", TrackRole.Hybrid);
        var fx = TestDevices.Instance(TestDevices.MidiFx);
        var synth = TestDevices.Instance(TestDevices.Synth);
        var history = new EditHistory(Project.CreateNew() with { Sequence = Project.CreateNew().Sequence.WithTrack(track) });
        history.Execute(DeviceCommands.InsertDevice(track.Id, fx, TestDevices.Lookup));
        history.Execute(DeviceCommands.InsertDevice(track.Id, synth, TestDevices.Lookup));
        return (history, track.Id, fx, synth);
    }

    private static DeviceChain ChainOf(EditHistory history, TrackId track) => history.Current.ChainOf(track)!;

    [Fact]
    public void AReplacement_TakesTheOldDevicesPlace_WithItsOwnId_InOneUndoStep()
    {
        var (history, track, fx, synth) = Chain();
        var before = history.Current;
        var replacement = TestDevices.Instance(TestDevices.MidiFx);

        history.Execute(DeviceCommands.ReplaceDevice(fx.Id, replacement, TestDevices.Lookup));

        Assert.Equal([replacement.Id, synth.Id], ChainOf(history, track).Devices.Select(d => d.Id));
        Assert.Equal("Replace Device", history.UndoLabel);
        history.Undo();
        Assert.Same(before, history.Current);
        history.Redo();
        Assert.Equal(replacement.Id, ChainOf(history, track).Devices[0].Id);
    }

    [Fact]
    public void AutomationOfADifferentDevice_IsKeptForTheOldOne_ButFollowsTheSameKindOfDevice()
    {
        var (history, track, fx, synth) = Chain();
        var lane = new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForDevice(synth.Id, new ParameterId(1)), [new AutomationPoint(Tick.Zero, ControlValue.Center)]);
        var fxLane = new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForDevice(fx.Id, new ParameterId(1)), [new AutomationPoint(Tick.Zero, ControlValue.Max)]);
        history.Execute(new ProjectCommand("lanes", p => p with { Sequence = p.Sequence.WithTrack(p.Sequence.FindTrack(track)!.WithAutomation([lane, fxLane])) }));

        var otherSynth = TestDevices.Instance(TestDevices.Filter);
        history.Execute(DeviceCommands.ReplaceDevice(synth.Id, otherSynth, TestDevices.Lookup));
        var sameFx = TestDevices.Instance(TestDevices.MidiFx);
        history.Execute(DeviceCommands.ReplaceDevice(fx.Id, sameFx, TestDevices.Lookup));

        var lanes = history.Current.Sequence.FindTrack(track)!.Automation;
        Assert.Equal(synth.Id, lanes.Single(l => l.Id == lane.Id).Target.Device);
        Assert.Equal(sameFx.Id, lanes.Single(l => l.Id == fxLane.Id).Target.Device);
        Assert.Equal(fxLane.Points, lanes.Single(l => l.Id == fxLane.Id).Points);
    }

    [Fact]
    public void Taps_MoveToTheReplacement_OnlyWhenItProducesTheirKindOfSignal()
    {
        var (history, track, fx, synth) = Chain();
        var eventsTap = SignalConnection.Create(SignalKind.Events, SignalNode.Device(fx.Id), SignalNode.Track(track));
        var audioTap = SignalConnection.Create(SignalKind.Audio, SignalNode.Device(synth.Id), SignalNode.Master);
        var other = Track.Create("other", TrackRole.Hybrid);
        eventsTap = eventsTap with { Destination = SignalNode.Track(other.Id) };
        history.Execute(new ProjectCommand("taps", p => p with { Sequence = p.Sequence.WithTrack(other), Connections = p.Connections.Add(eventsTap).Add(audioTap) }));

        // A note effect replaced by another keeps its events tap; the synth replaced by an audio effect keeps its audio tap.
        var newFx = TestDevices.Instance(TestDevices.MidiFx);
        var filter = TestDevices.Instance(TestDevices.Filter);
        history.Execute(DeviceCommands.ReplaceDevice(fx.Id, newFx, TestDevices.Lookup));
        history.Execute(DeviceCommands.ReplaceDevice(synth.Id, filter, TestDevices.Lookup));

        var connections = history.Current.Connections;
        Assert.Equal(SignalNode.Device(newFx.Id), connections.Single(c => c.Id == eventsTap.Id).Source);
        Assert.Equal(SignalNode.Device(filter.Id), connections.Single(c => c.Id == audioTap.Id).Source);

        // A note effect replaced by an audio effect loses its events tap (nothing would come out of it).
        var noteFx = ChainOf(history, track).Devices[0];
        var delay = TestDevices.Instance(TestDevices.Delay);
        try
        {
            history.Execute(DeviceCommands.ReplaceDevice(noteFx.Id, delay, TestDevices.Lookup));
            Assert.DoesNotContain(history.Current.Connections, c => c.Id == eventsTap.Id);
        }
        catch (CommandRefusedException)
        {
            // Routing may refuse it (an audio effect before the synth); then nothing changed at all.
            Assert.Contains(history.Current.Connections, c => c.Id == eventsTap.Id);
        }
    }

    [Fact]
    public void ReplacingANoteEffectWithAnInstrument_GivesASilentTrackItsAudioPath()
    {
        var track = Track.Create("t", TrackRole.Hybrid);
        var fx = TestDevices.Instance(TestDevices.MidiFx);
        var history = new EditHistory(Project.CreateNew() with { Sequence = Project.CreateNew().Sequence.WithTrack(track) });
        history.Execute(DeviceCommands.InsertDevice(track.Id, fx, TestDevices.Lookup));
        Assert.DoesNotContain(history.Current.Connections, c => c.Kind == SignalKind.Audio);

        history.Execute(DeviceCommands.ReplaceDevice(fx.Id, TestDevices.Instance(TestDevices.Synth), TestDevices.Lookup));

        Assert.Contains(history.Current.Connections, c => c.Kind == SignalKind.Audio && c.Destination == SignalNode.Master);
    }

    [Theory]
    [InlineData(150, true, 1)]
    [InlineData(105, false, 1)]
    [InlineData(196, false, 2)]
    [InlineData(40, true, 0)]
    [InlineData(260, false, 2)]
    public void ADrop_ReplacesOverAPanelsMiddle_AndInsertsNearItsEdges(double x, bool replace, int index)
    {
        (double, double)[] panels = [(0, 80), (100, 200)];

        Assert.Equal((replace, index), DeviceViewViewModel.DropTargetAt(panels, x, canReplace: true));
    }

    [Fact]
    public void ADeviceMovedWithinTheChain_NeverReplaces() =>
        Assert.Equal((false, 1), DeviceViewViewModel.DropTargetAt([(0, 80), (100, 200)], 120, canReplace: false));
}
