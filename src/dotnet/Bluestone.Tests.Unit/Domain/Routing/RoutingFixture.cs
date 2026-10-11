using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Tests.Unit.Domain.Routing;

/// <summary>Builds small projects for routing tests: instrument tracks with a chain each, racks, hardware, and mixer channels.</summary>
internal static class RoutingFixture
{
    public static readonly MidiChannel One = MidiChannel.FromNumber(1);

    public static NoteEvent Note(long at, int note = 60, int channel = 1) =>
        new(new Tick(at), new TickSpan(10), MidiChannel.FromNumber(channel), new NoteNumber(note), new Velocity(100));

    public static Track NoteTrack(string name, TrackRole role = TrackRole.Instrument, int channel = 1) =>
        Track.FromEvents(TrackId.New(), name, [Note(0, 60, channel)]).WithRole(role);

    public static Project Empty() => Project.CreateNew("test");

    public static Project With(this Project project, Track track) => project with { Sequence = project.Sequence.WithTrack(track) };

    public static Project WithTrackChain(this Project project, Track track, params DeviceDefinition[] devices) =>
        project.With(track).WithChain(DeviceChain.Create(ChainOwner.ForTrack(track.Id)) with { Devices = [.. devices.Select(TestDevices.Instance)] });

    public static Project WithRack(this Project project, out DeviceChain rack, string name, params DeviceDefinition[] devices)
    {
        rack = DeviceChain.Create(ChainOwner.Rack, name) with { Devices = [.. devices.Select(TestDevices.Instance)] };
        return project.WithChain(rack);
    }

    public static Project WithMixerChannel(this Project project, out MixerChannel channel, string name)
    {
        channel = MixerChannel.Create(name);
        return project with { Mixer = project.Mixer.With(channel) };
    }

    public static Project WithInstrument(this Project project, out ExternalInstrument instrument, string name = "MU2000")
    {
        instrument = ExternalInstrument.Create(name, new EndpointReference("coremidi", "1", name)).WithPort(new ExternalPort("B", "Port B"));
        return project with { Instruments = project.Instruments.Add(instrument) };
    }

    public static Project Connect(this Project project, SignalKind kind, SignalNode source, SignalNode destination, ChannelMapping? mapping = null, VoiceAssignment? voice = null) =>
        project with
        {
            Connections = project.Connections.Add(SignalConnection.Create(kind, source, destination) with { Mapping = mapping ?? ChannelMapping.Preserve, Voice = voice }),
        };

    public static ImmutableArray<RoutingIssue> Validate(this Project project) => SignalRoutingValidator.Validate(project, TestDevices.Lookup);

    public static ImmutableArray<RoutingIssue> Validate(this Project project, DeviceDefinitionLookup definitions) => SignalRoutingValidator.Validate(project, definitions);

    public static IEnumerable<RoutingIssue> Errors(this Project project) => SignalRoutingValidator.Errors(project.Validate());
}
