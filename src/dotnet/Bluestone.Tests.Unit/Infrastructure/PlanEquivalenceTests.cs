using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Infrastructure.Projects;
using CsCheck;
using static Bluestone.Tests.Unit.Playback.PlanDump;

namespace Bluestone.Tests.Unit.Infrastructure;

/// <summary>
/// Guards that projects saved in older formats still send and export the same MIDI 1.0 bytes once read
/// by the current version, however the model behind them changes. The fixtures were written by the
/// format 2 serializer and must never be regenerated; a failure here means old projects play differently.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>format2-full: <see cref="ProjectSamples.Full"/>, every event type and routing option. Its mute and
/// solo flags were cleared by hand after writing, so that it plays at all.</item>
/// <item>format2-edges: a note outlasting the last event, events on bar lines, an orphan release and a
/// meta at the last tick, a track starting after a mid-bar meter change, a muted track, an empty track.</item>
/// <item>format2-canon-gm16: canon-gm16.mid imported and saved without routes, so its per-track hashes
/// equal the sample's hashes in MidiOneOutputGoldenTests.</item>
/// </list>
/// Plans are compiled through the routing model the old routes migrate to (each route's channel on its
/// connection, its transposition as a Transpose device), once with a slot per track and once with every
/// track on one slot, which pins the order between tracks that share an output. Voice selections depend on
/// resolved profiles, not on the track model, so they are left out.
/// </remarks>
public sealed class PlanEquivalenceTests
{
    public static TheoryData<string, string, string, string> Fixtures() => new()
    {
        { "format2-full.bluestone", "16195787EB3D796E", "16195787EB3D796E", "A959AF30B339DF00" },
        { "format2-edges.bluestone", "B3A0AB00DA40E3F0", "EC1E2E5597714704", "A9D37B3B99CF7FF3" },
        { "format2-canon-gm16.bluestone", "BE918A5129078D96", "C1BD096BB50430FF", "6F9AA00B419CABFC" },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void OlderFormat_SendsAndExportsTheSameMidiOneBytes(string fixture, string slotPerTrack, string sharedSlot, string exported)
    {
        var project = ProjectSerializer.Default.Deserialize(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Fixtures", fixture))).Project;

        Assert.Equal(slotPerTrack, Hash(PlaybackStream(Compile(OnTestSlots(project, shared: false)))));
        Assert.Equal(sharedSlot, Hash(PlaybackStream(Compile(OnTestSlots(project, shared: true)))));
        Assert.Equal(exported, Hash(ExportStream(project.Sequence)));
    }

    private static readonly Gen<TrackEvent> GenEvent =
        Gen.OneOf<TrackEvent>(
            Gen.Select(Gen.Long[0, 20_000], Gen.Long[1, 4000], Gen.Int[0, 127]).Select(x =>
                (TrackEvent)new NoteEvent(new Tick(x.Item1), new TickSpan(x.Item2), MidiChannel.FromIndex(0), new NoteNumber(x.Item3), Velocity.Max)),
            Gen.Select(Gen.Long[0, 20_000], Gen.Int[0, 127]).Select(x =>
                (TrackEvent)new NoteOffEvent(EventId.New(), new Tick(x.Item1), MidiChannel.FromIndex(0), new NoteNumber(x.Item2), Velocity.DefaultRelease)),
            Gen.Long[0, 20_000].Select(x =>
                (TrackEvent)new ControllerEvent(new Tick(x), MidiChannel.FromIndex(0), ControllerNumber.ModulationWheel, ControlValue.Max)));

    // Wrapping events in a clip, from tick 0 or bar-aligned around them as import and migration do,
    // never changes what plays.
    [Fact]
    public void AnEnclosingClip_PlaysLikeTheBareEvents() =>
        Gen.Select(GenEvent.Array[1, 40], Gen.Int[0, 3]).Sample((events, meterIndex) =>
        {
            var ppqn = new Ppqn(480);
            var meter = meterIndex switch
            {
                0 => MeterMap.Constant(ppqn, TimeSignature.CommonTime),
                1 => MeterMap.Constant(ppqn, new TimeSignature(7, 8)),
                2 => new MeterMap(ppqn, [new MeterChange(new Tick(1000), new TimeSignature(3, 4))]),
                _ => new MeterMap(ppqn, [new MeterChange(new Tick(5000), new TimeSignature(5, 4)), new MeterChange(new Tick(9000), new TimeSignature(2, 4))]),
            };
            var id = TrackId.New();
            Sequence SequenceOf(Track track) => new(TempoMap.Constant(ppqn, Tempo.Default), meter, [track], []);

            var bare = PlaybackStream(SequenceOf(Track.FromEvents(id, "t", events)));
            var enclosed = PlaybackStream(SequenceOf(new Track(id, "t", [NoteClip.Enclosing(events, meter)!])));
            return bare.SequenceEqual(enclosed);
        });

    // Each track keeps the channel and transposition its route migrated to, and is sent to a test endpoint.
    private static Project OnTestSlots(Project project, bool shared) =>
        Routed(project, shared, t => TrackOutputs.Read(project, t.Id) with { Profile = null, Voice = null });
}
