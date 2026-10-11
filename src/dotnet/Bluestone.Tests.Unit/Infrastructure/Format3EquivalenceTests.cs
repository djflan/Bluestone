using Bluestone.Application.Routing;
using Bluestone.Infrastructure.Projects;
using Bluestone.Midi.Endpoints;
using Bluestone.Profiles;
using Bluestone.Signal;
using static Bluestone.Tests.Unit.Playback.PlanDump;

namespace Bluestone.Tests.Unit.Infrastructure;

/// <summary>
/// Holds projects saved in format 3 to the MIDI 1.0 bytes the pre-refactor pipeline sent for them
/// (docs/daw-epic-handoff.md section 5), now through the format 4 migration, the routing model, the
/// signal graph, and the compiler. The fixtures and hashes are frozen: a failure is a regression.
/// </summary>
/// <remarks>
/// Every endpoint the fixtures name is present except <c>gone-*</c> (missing) and <c>old-*</c>, which is
/// present under a new key with the same name (so it binds by name). Profiles are the ones in /profiles.
/// </remarks>
public sealed class Format3EquivalenceTests
{
    public static TheoryData<string, string, string, int, int> Fixtures() => new()
    {
        { "format3-routing-full.bluestone", "2F73C9A155441A5E", "BAD8DAA4476B28E9", 3, 158 },
        { "format3-canon.bluestone", "1B5E6E6E27359652", "6F9AA00B419CABFC", 3, 1407 },
    };

    internal static IReadOnlyList<EndpointDescriptor> Endpoints { get; } =
    [
        Output("-12345", "QY Out"),
        Output("777", "Module B"),
        Output("new-9", "Module C"),
        Output("orphan-route", "Orphan route"),
        Output("canon-0", "Canon 0"),
        Output("canon-1", "Canon 1"),
        Output("canon-2", "Canon 2"),
    ];

    internal static ProfileCatalog Profiles { get; } = ProfileCatalog.LoadDirectory(Path.Combine(RepositoryRoot, "profiles"));

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Format3Project_SendsAndExportsTheSameMidiOneBytes(string fixture, string playback, string exported, int slots, int events)
    {
        var project = ProjectSerializer.Default.Deserialize(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Fixtures", fixture))).Project;

        var prepared = PlaybackRouting.Prepare(project, DeviceCatalog.BuiltIn, Profiles, Endpoints);
        var plan = prepared.Compile(project.Sequence);

        Assert.Equal(slots, prepared.Slots.Length);
        Assert.Equal(events, plan.EventCount);
        Assert.Equal(playback, Hash(PlaybackStream(plan)));
        Assert.Equal(exported, Hash(ExportStream(project.Sequence)));
    }

    [Fact]
    public void RoutingFull_ReportsWhatItLeftOut()
    {
        var project = ProjectSerializer.Default.Deserialize(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Fixtures", "format3-routing-full.bluestone"))).Project;

        var plan = PlaybackRouting.Prepare(project, DeviceCatalog.BuiltIn, Profiles, Endpoints).Compile(project.Sequence);

        var byTrack = plan.Diagnostics.Select(d => (project.Sequence.FindTrack(d.Track)!.Name, d.Message)).ToList();
        Assert.Equal(
            [
                ("Pad", "1 events in clips were replaced by the track's automation."),
                ("Pad", "1 automation lanes control the same thing as another lane once sent on the route's channel, and were left out."),
                ("Orphan", "The track is not routed to an output and will not play."),
                ("Unrouted", "The track is not routed to an output and will not play."),
                ("Settings only", "The track is not routed to an output and will not play."),
            ],
            byTrack.Where(d => !d.Message.Contains("transposed", StringComparison.Ordinal)));
    }

    private static EndpointDescriptor Output(string key, string name) =>
        new(new EndpointId("coremidi", key), name, EndpointDirection.Output, EndpointTransport.Physical, EndpointCapabilities.SystemExclusive);
}
