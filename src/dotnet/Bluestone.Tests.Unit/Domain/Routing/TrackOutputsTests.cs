using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Tests.Unit.Domain.Routing;

public sealed class TrackOutputsTests
{
    private static readonly EndpointReference Qy = new("coremidi", "1", "QY Out");
    private static readonly EndpointReference Module = new("coremidi", "2", "Module");
    private static readonly ProfileReference Xg = new("bluestone.generic.xg", "Generic XG");
    private static readonly ProfileReference Gm = new("bluestone.generic.gm1", "General MIDI");

    private static (Project Project, Track A, Track B) TwoTracks()
    {
        var a = Track.Create("a");
        var b = Track.Create("b");
        return (Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Bluestone.Domain.Time.Ppqn.Default).WithTrack(a).WithTrack(b) }, a, b);
    }

    [Fact]
    public void Write_ThenRead_GivesTheSameOutput()
    {
        var (project, a, _) = TwoTracks();
        var output = new TrackOutput { Endpoint = Qy, Profile = Xg, Channel = MidiChannel.FromNumber(4), Transpose = -7, Voice = new VoiceAssignment("normal", new ProgramNumber(3)) };

        var written = TrackOutputs.Write(project, a.Id, output);

        Assert.Equal(output, TrackOutputs.Read(written, a.Id));
        Assert.Equal(-7, BuiltInDevices.TransposeOf(Assert.Single(written.ChainOf(a.Id)!.Devices)));
        Assert.Empty(SignalRoutingValidator.Errors(SignalRoutingValidator.Validate(written, BuiltInDevices.Find)));
    }

    [Fact]
    public void TracksOnTheSameEndpointAndProfile_ShareOneInstrument_AndADifferentProfileGetsItsOwn()
    {
        var (project, a, b) = TwoTracks();

        var same = TrackOutputs.Write(TrackOutputs.Write(project, a.Id, new TrackOutput { Endpoint = Qy, Profile = Xg }), b.Id, new TrackOutput { Endpoint = Qy, Profile = Xg });
        var different = TrackOutputs.Write(TrackOutputs.Write(project, a.Id, new TrackOutput { Endpoint = Qy, Profile = Xg }), b.Id, new TrackOutput { Endpoint = Qy, Profile = Gm });

        Assert.Single(same.Instruments);
        Assert.Equal(2, same.Connections.Length);
        Assert.Equal(["QY Out", "QY Out (General MIDI)"], different.Instruments.Select(i => i.Name));
    }

    [Fact]
    public void ChangingTheOutput_RetargetsTheConnection_AndKeepsItsIdAndChannelFilter()
    {
        var (project, a, _) = TwoTracks();
        project = TrackOutputs.Write(project, a.Id, new TrackOutput { Endpoint = Qy });
        var connection = TrackOutputs.PrimaryConnection(project, a.Id)!;
        project = project with { Connections = [connection with { Mapping = new ChannelMapping { Only = [MidiChannel.FromNumber(10)] } }] };

        var moved = TrackOutputs.Write(project, a.Id, TrackOutputs.Read(project, a.Id) with { Endpoint = Module, Channel = MidiChannel.FromNumber(2) });

        var retargeted = Assert.Single(moved.Connections);
        Assert.Equal(connection.Id, retargeted.Id);
        Assert.Equal("Module", moved.FindInstrument(retargeted.Destination.AsInstrument())!.Name);
        Assert.Equal([MidiChannel.FromNumber(10)], retargeted.Mapping.Only);
        Assert.Equal(MidiChannel.FromNumber(2), retargeted.Mapping.Force);
        Assert.Equal(2, moved.Instruments.Length);
    }

    [Fact]
    public void ClearingTheOutput_RemovesOnlyItsConnection_AndKeepsTheInstrumentAndOtherConnections()
    {
        var (project, a, b) = TwoTracks();
        project = TrackOutputs.Write(project, a.Id, new TrackOutput { Endpoint = Qy, Transpose = 3 });
        var toB = SignalConnection.Create(SignalKind.Events, SignalNode.Track(a.Id), SignalNode.Track(b.Id));
        project = project with { Connections = project.Connections.Add(toB) };

        var cleared = TrackOutputs.Write(project, a.Id, TrackOutput.None);

        Assert.Equal([toB], cleared.Connections);
        Assert.Single(cleared.Instruments);
        Assert.Equal(TrackOutput.None, TrackOutputs.Read(cleared, a.Id));
        Assert.Equal(0, BuiltInDevices.TransposeOf(Assert.Single(cleared.ChainOf(a.Id)!.Devices)));
    }

    [Fact]
    public void Transposition_UsesTheFirstTransposeDevice_AndLeavesOtherDevicesAlone()
    {
        var (project, a, _) = TwoTracks();
        var arp = BuiltInDevices.CreateArpeggiator();
        project = project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(a.Id)) with { Devices = [arp, BuiltInDevices.CreateTranspose(1) with { IsBypassed = true }] });

        var bypassed = TrackOutputs.Read(project, a.Id);
        var written = TrackOutputs.Write(project, a.Id, bypassed with { Transpose = 12 });

        Assert.Equal(0, bypassed.Transpose);
        var devices = written.ChainOf(a.Id)!.Devices;
        Assert.Equal(arp, devices[0]);
        Assert.Equal((12, false), (BuiltInDevices.TransposeOf(devices[1]), devices[1].IsBypassed));
    }

    [Fact]
    public void EditingOtherSettings_LeavesABypassedTransposeDeviceAlone()
    {
        var (project, a, _) = TwoTracks();
        var transpose = BuiltInDevices.CreateTranspose(12) with { IsBypassed = true };
        project = project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(a.Id)) with { Devices = [transpose] });

        var written = TrackOutputs.Write(project, a.Id, TrackOutputs.Read(project, a.Id) with { Endpoint = Qy, Channel = MidiChannel.FromNumber(3) });

        Assert.Equal(transpose, Assert.Single(written.ChainOf(a.Id)!.Devices));
    }

    [Fact]
    public void ConnectionIds_AreUnique()
    {
        var (project, a, b) = TwoTracks();
        var connection = SignalConnection.Create(SignalKind.Events, SignalNode.Track(a.Id), SignalNode.Track(b.Id));

        Assert.Throws<ArgumentException>(() => project with { Connections = [connection, connection with { Destination = SignalNode.Master }] });
    }

    [Fact]
    public void RemovingATrack_RemovesItsChainAndEveryConnectionTouchingIt()
    {
        var (project, a, b) = TwoTracks();
        var arp = BuiltInDevices.CreateArpeggiator();
        project = TrackOutputs.Write(project, a.Id, new TrackOutput { Endpoint = Qy }).WithChain(DeviceChain.Create(ChainOwner.ForTrack(a.Id)) with { Devices = [arp] });
        var tap = SignalConnection.Create(SignalKind.Events, SignalNode.Device(arp.Id), SignalNode.Track(b.Id));
        var into = SignalConnection.Create(SignalKind.Events, SignalNode.Track(b.Id), SignalNode.Track(a.Id));
        project = project with { Connections = project.Connections.Add(tap).Add(into) };

        var removed = project.WithoutTrack(a.Id);

        Assert.Equal([b.Id], removed.Sequence.Tracks.Select(t => t.Id));
        Assert.Empty(removed.Chains);
        Assert.Empty(removed.Connections);
        Assert.Single(removed.Instruments);
    }
}
