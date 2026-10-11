using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Files;
using Bluestone.Tests.Unit.Playback;
using CsCheck;
using static Bluestone.Tests.Unit.Midi.Files.SmfBytes;
using static Bluestone.Tests.Unit.Midi.Files.SmfReaderTests;

namespace Bluestone.Tests.Unit.Midi.Files;

public sealed class SmfImportExportTests
{
    private static readonly MidiChannel One = MidiChannel.FromIndex(0);

    private static SmfImportResult ImportBytes(byte[] bytes) => SmfImporter.Import(SmfReader.Read(bytes).File);

    [Fact]
    public void Export_WritesAutomationAsTheControllerEventsItPlays()
    {
        var clipVolume = new ControllerEvent(new Tick(10), One, ControllerNumber.ChannelVolume, ControlValue.Max);
        var lane = new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForController(One, ControllerNumber.ChannelVolume), [
            new AutomationPoint(new Tick(0), ControlValue.FromSevenBit(20), AutomationCurve.Hold),
            new AutomationPoint(new Tick(960), ControlValue.FromSevenBit(90), AutomationCurve.Hold),
        ]);
        var track = Track.FromEvents(TrackId.New(), "t", [clipVolume]).WithAutomation([lane]);
        var sequence = Sequence.CreateEmpty(new Ppqn(480)).WithTrack(track);

        var exported = SmfReader.Read(SmfWriter.Write(SmfExporter.Export(sequence).File)).File;

        var fileTrack = exported.Tracks.ElementAt(1);
        var volume = fileTrack.Events.OfType<SmfChannelEvent>().Select(e => (e.Tick, (int)e.Message.Data2)).ToList();
        Assert.Equal([(0L, 20), (960L, 90)], volume);
        Assert.Equal(960, fileTrack.EndTick);
    }

    [Fact]
    public void Export_ReportsDeviceAutomation_ItCannotWrite()
    {
        var lane = new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForDevice(Bluestone.Domain.Devices.DeviceId.New(), new Bluestone.Domain.Devices.ParameterId(1)), [new AutomationPoint(Tick.Zero, ControlValue.Max)]);
        var track = Track.FromEvents(TrackId.New(), "t", [new NoteEvent(Tick.Zero, new TickSpan(10), One, NoteNumber.MiddleC, Velocity.Max)]).WithAutomation([lane]);

        var result = SmfExporter.Export(Sequence.CreateEmpty(new Ppqn(480)).WithTrack(track));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == SmfDiagnosticCodes.DeviceAutomationNotExported);
        Assert.Equal(SmfDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.DoesNotContain(result.File.Tracks.ElementAt(1).Events.OfType<SmfChannelEvent>(), e => e.Message.Status is >= 0xB0 and <= 0xBF);
    }

    [Fact]
    public void Import_FormatZeroSplitsChannelsAndKeepsGlobalEventsOnce()
    {
        var result = ImportBytes(File(0, 96, MTrk(
            0x00, 0xFF, 0x03, 0x04, (byte)'S', (byte)'o', (byte)'n', (byte)'g',
            0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20,
            0x00, 0xFF, 0x58, 0x04, 0x04, 0x02, 0x18, 0x08,
            0x00, 0xF0, 0x08, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7,
            0x00, 0xB9, 0x00, 0x7F,
            0x00, 0xB9, 0x20, 0x00,
            0x00, 0xC9, 0x00,
            0x00, 0xB0, 0x00, 0x00,
            0x00, 0xB0, 0x20, 0x01,
            0x00, 0xC0, 0x28,
            0x00, 0xB0, 0x65, 0x00,
            0x00, 0x64, 0x00,
            0x00, 0x06, 0x02,
            0x00, 0x90, 0x3C, 0x64,
            0x00, 0x99, 0x3C, 0x50,
            0x0A, 0xE0, 0x00, 0x50,
            0x00, 0xD0, 0x30,
            0x00, 0xA0, 0x3C, 0x20,
            0x00, 0xFF, 0x05, 0x02, (byte)'A', (byte)'h',
            0x00, 0xFF, 0x06, 0x01, (byte)'A',
            0x0A, 0x99, 0x3C, 0x00,
            0x0A, 0x80, 0x3C, 0x40)));

        Assert.Empty(result.Diagnostics);
        Assert.Equal("Song", result.Title);
        Assert.Equal(["MIDI Setup", "Channel 1", "Channel 10"], result.Sequence.Tracks.Select(t => t.Name));
        var setup = result.Sequence.Tracks[0];
        Assert.Equal(2, setup.ArrangedEvents.Length);
        Assert.Equal<byte>([0xF0, 0x43, 0x10, 0x4C, 0, 0, 0x7E, 0, 0xF7], Assert.Single(setup.ArrangedEvents.OfType<SysExEvent>()).Message.Bytes.ToArray());
        Assert.Equal(10, Assert.Single(setup.ArrangedEvents.OfType<MetaEvent>()).Position.Value);
        Assert.Equal([new Marker(new Tick(10), "A")], result.Sequence.Markers);
        Assert.Equal(new Tempo(500_000), Assert.Single(result.Sequence.TempoMap.Changes).Tempo);
        Assert.Equal(TimeSignature.CommonTime, Assert.Single(result.Sequence.MeterMap.Changes).Signature);

        var part = result.Sequence.Tracks[1];
        var drum = result.Sequence.Tracks[2];
        var melodicNote = Assert.Single(part.ArrangedEvents.OfType<NoteEvent>());
        Assert.Equal((0L, 30L, (byte)64), (melodicNote.Position.Value, melodicNote.Duration.Value, melodicNote.ReleaseVelocity.Value));
        var drumNote = Assert.Single(drum.ArrangedEvents.OfType<NoteEvent>());
        Assert.Equal((0L, 20L, (byte)0), (drumNote.Position.Value, drumNote.Duration.Value, drumNote.ReleaseVelocity.Value));
        Assert.Equal([101, 100, 6], part.ArrangedEvents.OfType<ControllerEvent>().Select(e => (int)e.Controller.Value));
        Assert.Equal(new ProgramSelection(new ProgramNumber(0x28), new SevenBitValue(0), new SevenBitValue(1)), Assert.Single(part.ArrangedEvents.OfType<ProgramEvent>()).Selection);
        Assert.Equal(new ProgramSelection(new ProgramNumber(0), new SevenBitValue(0x7F), new SevenBitValue(0)), Assert.Single(drum.ArrangedEvents.OfType<ProgramEvent>()).Selection);
        Assert.All(part.ArrangedEvents.OfType<ChannelEvent>(), e => Assert.Equal(One, e.Channel));
        Assert.All(drum.ArrangedEvents.OfType<ChannelEvent>(), e => Assert.Equal(10, e.Channel.Number));

        var plan = PlanDump.Compile(PlanDump.Routed(result.Sequence, shared: true));
        Assert.Empty(plan.Diagnostics);
        Assert.Single(plan.Payloads);
        Assert.Equal(0, plan.Events[0].PayloadIndex);
        // Each bank select travels with its program change: SysEx, then channel 1's CC 0, CC 32, program.
        Assert.Equal(["B000", "B020", "C028"], plan.Events[1..4].Select(e => $"{e.Message.Status:X2}{e.Message.Data1:X2}"));

        var roundTrip = SmfImporter.Import(SmfReader.Read(SmfWriter.Write(SmfExporter.Export(result.Sequence, result.Title).File)).File);
        Assert.Empty(roundTrip.Diagnostics);
        Assert.Equal(result.Title, roundTrip.Title);
        Assert.Equal(result.Sequence.Tracks.Select(Describe), roundTrip.Sequence.Tracks.Select(Describe));
    }

    [Fact]
    public void Import_FormatZeroKeepsRawPacketsAndInvalidMeterOnSetupTrack()
    {
        var result = ImportBytes(File(0, 1, MTrk(
            0x00, 0xF0, 0x02, 0x43, 0x10,
            0x00, 0xBF, 0x07, 0x64,
            0x05, 0xF7, 0x02, 0x4C, 0xF7,
            0x00, 0xFF, 0x58, 0x04, 0x03, 0x03, 0x18, 0x08)));

        Assert.Equal(["MIDI Setup", "Channel 16"], result.Sequence.Tracks.Select(t => t.Name));
        var setup = result.Sequence.Tracks[0].ArrangedEvents;
        Assert.Equal([0L, 5L], setup.OfType<RawMidiEvent>().Select(e => e.Position.Value));
        Assert.Equal<byte>([0xF0, 0x43, 0x10], setup.OfType<RawMidiEvent>().First().Bytes.ToArray());
        Assert.Equal<byte>([0x4C, 0xF7], setup.OfType<RawMidiEvent>().Last().Bytes.ToArray());
        Assert.Equal(SmfMetaType.TimeSignature, Assert.Single(setup.OfType<MetaEvent>()).Type);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.InvalidTimeSignature);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.SysExKeptRaw, SmfDiagnosticSeverity.Info);
        Assert.Equal(16, Assert.IsType<ControllerEvent>(Assert.Single(result.Sequence.Tracks[1].ArrangedEvents)).Channel.Number);
    }

    [Fact]
    public void Import_FormatZeroSeparatesPairingAndRepairsByChannel()
    {
        var result = ImportBytes(File(0, 96, MTrk(
            0x00, 0x91, 0x3C, 0x64,
            0x00, 0x90, 0x3C, 0x50,
            0x0A, 0x80, 0x3C, 0x40,
            0x05, 0x80, 0x3D, 0x40)));

        Assert.Equal(["Channel 1", "Channel 2"], result.Sequence.Tracks.Select(t => t.Name));
        Assert.Equal(10, Assert.Single(result.Sequence.Tracks[0].ArrangedEvents.OfType<NoteEvent>()).Duration.Value);
        Assert.Equal(15, Assert.Single(result.Sequence.Tracks[1].ArrangedEvents.OfType<NoteEvent>()).Duration.Value);
        Assert.Single(result.Sequence.Tracks[0].ArrangedEvents.OfType<NoteOffEvent>());
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.UnpairedNoteOff, SmfDiagnosticSeverity.Info);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.UnterminatedNote);
        Assert.All(result.Diagnostics, d => Assert.Equal(0, d.TrackIndex));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Import_OtherFormatsKeepMultiChannelTrackIntact(int format)
    {
        var result = ImportBytes(File(format, 96, MTrk(
            0x00, 0xB0, 0x07, 0x64,
            0x00, 0xB9, 0x07, 0x50)));

        Assert.Equal(2, Assert.Single(result.Sequence.Tracks).ArrangedEvents.Length);
    }

    [Fact]
    public void Import_PairsOverlappingNotesFirstInFirstOut()
    {
        var result = ImportBytes(File(0, 96, MTrk(
            0x00, 0x90, 0x3C, 0x64,   // on A @0
            0x0A, 0x90, 0x3C, 0x50,   // on B @10
            0x0A, 0x80, 0x3C, 0x20,   // off @20 → A
            0x0A, 0x90, 0x3C, 0x00))); // off (vel 0) @30 → B

        var notes = result.Sequence.Tracks.Single().ArrangedEvents.Cast<NoteEvent>().ToList();
        Assert.Equal([(0L, 20L, 100, 0x20), (10L, 20L, 80, 0)], notes.Select(n => (n.Position.Value, n.Duration.Value, (int)n.Velocity.Value, (int)n.ReleaseVelocity.Value)));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Import_KeepsUnpairedNoteOffsAsReleases()
    {
        var result = ImportBytes(File(0, 96, MTrk(0x00, 0x80, 0x3C, 0x40)));

        var release = Assert.IsType<NoteOffEvent>(result.Sequence.Tracks.Single().ArrangedEvents.Single());
        Assert.Equal((NoteNumber.MiddleC, new Velocity(0x40)), (release.Note, release.ReleaseVelocity));
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.UnpairedNoteOff, SmfDiagnosticSeverity.Info);
    }

    [Fact]
    public void Import_EndsUnterminatedNotesAtTrackEnd()
    {
        byte[] body = [0x00, 0x90, 0x3C, 0x64, 0x83, 0x00, 0xFF, 0x2F, 0x00]; // end of track at 384
        var result = ImportBytes(File(0, 96, body));

        var note = Assert.IsType<NoteEvent>(result.Sequence.Tracks.Single().ArrangedEvents.Single());
        Assert.Equal(new TickSpan(384), note.Duration);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.UnterminatedNote);
    }

    [Fact]
    public void Import_LengthensZeroLengthNotes()
    {
        var result = ImportBytes(File(0, 96, MTrk(0x00, 0x90, 0x3C, 0x64, 0x00, 0x80, 0x3C, 0x40)));

        Assert.Equal(new TickSpan(1), Assert.IsType<NoteEvent>(result.Sequence.Tracks.Single().ArrangedEvents.Single()).Duration);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.ZeroLengthNote);
    }

    [Fact]
    public void Import_ExtractsConductorDataAndTitle()
    {
        byte[] conductor = MTrk(
            0x00, 0xFF, 0x03, 0x04, (byte)'S', (byte)'o', (byte)'n', (byte)'g',
            0x00, 0xFF, 0x58, 0x04, 0x03, 0x02, 0x18, 0x08,
            0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20,
            0x60, 0xFF, 0x06, 0x05, (byte)'V', (byte)'e', (byte)'r', (byte)'s', (byte)'e',
            0x00, 0xFF, 0x51, 0x03, 0x0F, 0x42, 0x40);
        byte[] part = MTrk(0x00, 0xFF, 0x03, 0x04, (byte)'B', (byte)'a', (byte)'s', (byte)'s', 0x00, 0x91, 0x28, 0x64, 0x60, 0x81, 0x28, 0x40);

        var result = ImportBytes(File(1, 96, conductor, part));
        var sequence = result.Sequence;

        Assert.Equal("Song", result.Title);
        Assert.Equal("Bass", Assert.Single(sequence.Tracks).Name);
        Assert.Equal([new TempoChange(Tick.Zero, new Tempo(500_000)), new TempoChange(new Tick(96), new Tempo(1_000_000))], sequence.TempoMap.Changes);
        Assert.Equal(new TimeSignature(3, 4), sequence.MeterMap.SignatureAt(Tick.Zero));
        Assert.Equal([new Marker(new Tick(96), "Verse")], sequence.Markers);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Import_ReportsNonStandardMetronomeSettings()
    {
        var result = ImportBytes(File(0, 96, MTrk(0x00, 0xFF, 0x58, 0x04, 0x06, 0x03, 0x24, 0x08)));

        Assert.Equal(new TimeSignature(6, 8), result.Sequence.MeterMap.SignatureAt(Tick.Zero));
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.MetronomeSettingsDropped, SmfDiagnosticSeverity.Info);
    }

    [Fact]
    public void Import_KeepsMalformedConductorEventsRaw()
    {
        var result = ImportBytes(File(0, 96, MTrk(0x00, 0xFF, 0x51, 0x02, 0x07, 0xA1, 0x00, 0xFF, 0x58, 0x02, 0x04, 0x02)));

        Assert.Equal(2, result.Sequence.Tracks.Single().ArrangedEvents.OfType<MetaEvent>().Count());
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.InvalidTempo);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.InvalidTimeSignature);
    }

    [Fact]
    public void Import_KeepsMetersIncompatibleWithResolutionRaw()
    {
        var result = ImportBytes(File(0, 1, MTrk(0x00, 0xFF, 0x58, 0x04, 0x03, 0x03, 0x18, 0x08)));

        Assert.Equal(TimeSignature.CommonTime, result.Sequence.MeterMap.SignatureAt(Tick.Zero));
        Assert.Equal(SmfMetaType.TimeSignature, Assert.IsType<MetaEvent>(result.Sequence.Tracks.Single().ArrangedEvents.Single()).Type);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.InvalidTimeSignature);
    }

    [Theory]
    [InlineData(0xE728, 500, 500_000)]  // 25 fps × 40
    [InlineData(0xE250, 1200, 500_000)] // 30 fps × 80
    [InlineData(0xE350, 1200, 500_500)] // 29.97 drop-frame × 80
    [InlineData(0xE719, 625, 1_000_000)] // 25 fps × 25 → 625 ticks/s, odd
    public void Import_ConvertsSmpteTiming(int division, int expectedPpqn, int expectedTempo)
    {
        var result = ImportBytes(File(0, division, MTrk(0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20)));

        Assert.Equal(expectedPpqn, result.Sequence.Ppqn.TicksPerQuarterNote);
        Assert.Equal(new Tempo(expectedTempo), Assert.Single(result.Sequence.TempoMap.Changes).Tempo);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.SmpteTimingConverted, SmfDiagnosticSeverity.Info);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.TempoIgnoredForSmpte);
    }

    [Fact]
    public void Import_ConvertsCompleteSysExAndKeepsSplitPacketsRaw()
    {
        var result = ImportBytes(File(0, 96, MTrk(
            0x00, 0xF0, 0x08, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7,
            0x00, 0xF0, 0x02, 0x43, 0x10,
            0x05, 0xF7, 0x02, 0x4C, 0xF7)));

        var events = result.Sequence.Tracks.Single().ArrangedEvents;
        Assert.IsType<SysExEvent>(events[0]);
        Assert.Equal([0xF0, 0x43, 0x10], Assert.IsType<RawMidiEvent>(events[1]).Bytes.ToArray());
        Assert.Equal([0x4C, 0xF7], Assert.IsType<RawMidiEvent>(events[2]).Bytes.ToArray());
        Assert.Equal(2, Assert.Single(result.Diagnostics).Count);
    }

    [Fact]
    public void Import_PreservesFileOrderAmongTies()
    {
        var result = ImportBytes(File(0, 96, MTrk(0x00, 0xB0, 0x65, 0x00, 0x00, 0x64, 0x00, 0x00, 0x06, 0x0C)));

        Assert.Equal([101, 100, 6], result.Sequence.Tracks.Single().ArrangedEvents.Cast<ControllerEvent>().Select(e => (int)e.Controller.Value));
    }

    [Fact]
    public void Import_ReportsMultiSequenceAndPortAssignments()
    {
        var result = ImportBytes(File(2, 96, MTrk(0x00, 0xFF, 0x21, 0x01, 0x01), MTrk()));

        Assert.Equal(2, result.Sequence.Tracks.Length);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.MultiSequenceFlattened);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.PortAssignmentNotApplied, SmfDiagnosticSeverity.Info);
    }

    [Fact]
    public void Import_DecodesLatin1NamesThatAreNotUtf8() =>
        Assert.Equal("Café", ImportBytes(File(0, 96, MTrk(0x00, 0xFF, 0x03, 0x04, (byte)'C', (byte)'a', (byte)'f', 0xE9))).Sequence.Tracks.Single().Name);

    [Fact]
    public void Export_OrdersReleasesBeforeRetriggers()
    {
        var first = new NoteEvent(Tick.Zero, new TickSpan(96), One, NoteNumber.MiddleC, Velocity.Max);
        var second = new NoteEvent(new Tick(96), new TickSpan(96), One, NoteNumber.MiddleC, Velocity.Max);
        var sequence = Sequence.CreateEmpty(new Ppqn(96)).WithTrack(Track.FromEvents(TrackId.New(), "", [second, first]));

        var track = SmfExporter.Export(sequence).File.Tracks.Skip(1).Single();

        var messages = track.Events.Cast<SmfChannelEvent>().Select(e => (e.Tick, e.Message.IsNoteOn)).ToList();
        Assert.Equal([(0L, true), (96L, false), (96L, true), (192L, false)], messages);
        Assert.Equal(192, track.EndTick);
    }

    [Fact]
    public void Export_WritesConductorTrack()
    {
        var sequence = Sequence.CreateEmpty(new Ppqn(480))
            .WithTempoMap(new TempoMap(new Ppqn(480), [new TempoChange(new Tick(960), new Tempo(400_000))]))
            .WithMarkers([new Marker(new Tick(10), "Intro")]);

        var file = SmfExporter.Export(sequence, "Demo").File;

        Assert.Equal(SmfFormat.MultiTrack, file.Format);
        var conductor = file.Tracks.First().Events.Cast<SmfMetaEvent>().ToList();
        Assert.Equal(
            [(0L, SmfMetaType.TrackName), (0L, SmfMetaType.TimeSignature), (0L, SmfMetaType.Tempo), (10L, SmfMetaType.Marker), (960L, SmfMetaType.Tempo)],
            conductor.Select(e => (e.Tick, e.Type)));
        Assert.Equal([0x04, 0x02, 0x18, 0x08], conductor[1].Data.ToArray());
        Assert.Equal([0x06, 0x1A, 0x80], conductor[4].Data.ToArray());
    }

    [Fact]
    public void Export_ReportsWhatCannotBeStored()
    {
        var sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(Track.Create("Bässe").WithMuted(true));

        var diagnostics = SmfExporter.Export(sequence).Diagnostics;

        AssertCode(diagnostics, SmfDiagnosticCodes.MixerStateNotStored, SmfDiagnosticSeverity.Info);
        AssertCode(diagnostics, SmfDiagnosticCodes.TextEncodedAsUtf8, SmfDiagnosticSeverity.Info);
    }

    private static readonly Gen<TrackEvent> GenTrackEvent =
        Gen.Frequency(
            (5, Gen.Select(Gen.Long[0, 4000], Gen.Long[1, 500], Gen.Int[0, 15], Gen.Int[0, 127], Gen.Int[1, 127], Gen.Int[0, 127])
                .Select(x => (TrackEvent)new NoteEvent(EventId.New(), new Tick(x.Item1), new TickSpan(x.Item2), MidiChannel.FromIndex(x.Item3), new NoteNumber(x.Item4), new Velocity(x.Item5), new Velocity(x.Item6)))),
            // Bank select controllers are left to dedicated tests: next to a program change they merge into it.
            (3, Gen.Select(Gen.Long[0, 4000], Gen.Int[0, 15], Gen.Int[1, 127].Where(c => c != 32), Gen.Int[0, 127])
                .Select(x => (TrackEvent)new ControllerEvent(new Tick(x.Item1), MidiChannel.FromIndex(x.Item2), new ControllerNumber(x.Item3), ControlValue.FromSevenBit(x.Item4)))),
            (1, Gen.Select(Gen.Long[0, 4000], Gen.Int[0, 15], Gen.Int[0, 16383])
                .Select(x => (TrackEvent)new PitchBendEvent(new Tick(x.Item1), MidiChannel.FromIndex(x.Item2), ControlValue.FromFourteenBit(x.Item3)))),
            (1, Gen.Select(Gen.Long[0, 4000], Gen.Int[0, 15], Gen.Int[0, 127], Gen.Int[-1, 127], Gen.Int[-1, 127])
                .Select(x => (TrackEvent)new ProgramEvent(new Tick(x.Item1), MidiChannel.FromIndex(x.Item2), new ProgramSelection(
                    new ProgramNumber(x.Item3),
                    x.Item4 < 0 ? null : new SevenBitValue(x.Item4),
                    x.Item5 < 0 ? null : new SevenBitValue(x.Item5))))),
            (1, Gen.Select(Gen.Long[0, 4000], Gen.Int[0, 15], Gen.Int[0, 127], Gen.Int[0, 127], Gen.Bool)
                .Select(x => x.Item5
                    ? (TrackEvent)new PolyPressureEvent(EventId.New(), new Tick(x.Item1), MidiChannel.FromIndex(x.Item2), new NoteNumber(x.Item3), ControlValue.FromSevenBit(x.Item4))
                    : new ChannelPressureEvent(new Tick(x.Item1), MidiChannel.FromIndex(x.Item2), ControlValue.FromSevenBit(x.Item4)))),
            (1, Gen.Select(Gen.Long[0, 4000], Gen.Byte[0, 127].Array[0, 16])
                .Select(x => (TrackEvent)new SysExEvent(new Tick(x.Item1), SysExMessage.Create([0xF0, .. x.Item2, 0xF7])))),
            (1, Gen.Select(Gen.Long[0, 4000], Gen.Byte[0x01, 0x05], Gen.Byte[0x20, 0x7E].Array[0, 12])
                .Where(x => x.Item2 != SmfMetaType.TrackName || x.Item1 != 0)
                .Select(x => (TrackEvent)new MetaEvent(EventId.New(), new Tick(x.Item1), x.Item2, ByteBlock.Copy(x.Item3)))));

    private static readonly Gen<Sequence> GenSequence =
        Gen.Select(
            Gen.Select(Gen.String[Gen.Char['a', 'z'], 0, 12], GenTrackEvent.Array[0, 30]).Array[1, 4],
            Gen.Select(Gen.Long[1, 4000], Gen.Int[200_000, 1_500_000]).Array[0, 4],
            Gen.Select(Gen.Long[1, 4000], Gen.Int[1, 12], Gen.Int[0, 3]).Array[0, 3],
            (tracks, tempos, meters) =>
            {
                var ppqn = new Ppqn(480);
                return new Sequence(
                    new TempoMap(ppqn, tempos.Select(t => new TempoChange(new Tick(t.Item1), new Tempo(t.Item2)))),
                    new MeterMap(ppqn, meters.Select(m => new MeterChange(new Tick(m.Item1), TimeSignature.FromExponent(m.Item2, m.Item3)))),
                    tracks.Select(t => Track.FromEvents(TrackId.New(), t.Item1, WithoutAmbiguousEvents(t.Item2))),
                    []);
            });

    [Fact]
    public void ExportThenImport_PreservesTheSequence() =>
        GenSequence.Sample(original =>
        {
            var bytes = SmfWriter.Write(SmfExporter.Export(original, "Title").File);
            var imported = SmfImporter.Import(SmfReader.Read(bytes).File);
            return imported.Title == "Title"
                && imported.Diagnostics.Count == 0
                && imported.Sequence.TempoMap.Changes.SequenceEqual(original.TempoMap.Changes)
                && imported.Sequence.MeterMap.Changes.SequenceEqual(original.MeterMap.Changes)
                && imported.Sequence.Tracks.Select(Describe).SequenceEqual(original.Tracks.Select(Describe));
        });

    /// <summary>
    /// Drops what a MIDI file cannot represent unambiguously: notes that overlap an earlier note of the
    /// same pitch and channel, and a second program selection on the same channel and tick (whose bank
    /// select messages would be read back as belonging to the first).
    /// </summary>
    private static IEnumerable<TrackEvent> WithoutAmbiguousEvents(TrackEvent[] events)
    {
        var busyUntil = new Dictionary<(MidiChannel, NoteNumber), long>();
        var programs = new HashSet<(MidiChannel, Tick)>();
        foreach (var e in events.OrderBy(e => e.Position))
        {
            if (e is ProgramEvent program && !programs.Add((program.Channel, program.Position)))
            {
                continue;
            }

            if (e is NoteEvent note)
            {
                var key = (note.Channel, note.Note);
                if (busyUntil.TryGetValue(key, out var end) && note.Position.Value < end)
                {
                    continue;
                }

                busyUntil[key] = note.EndPosition.Value;
            }

            yield return e;
        }
    }

    [Fact]
    public void Export_WarnsThatOverlappingSamePitchNotesAreAmbiguous()
    {
        var sequence = Sequence.CreateEmpty(new Ppqn(96)).WithTrack(Track.FromEvents(TrackId.New(), "", [
            new NoteEvent(Tick.Zero, new TickSpan(100), One, NoteNumber.MiddleC, Velocity.Max),
            new NoteEvent(new Tick(10), new TickSpan(20), One, NoteNumber.MiddleC, Velocity.Max),
            new NoteEvent(new Tick(10), new TickSpan(20), MidiChannel.FromIndex(1), NoteNumber.MiddleC, Velocity.Max),
        ]));

        AssertCode(SmfExporter.Export(sequence).Diagnostics, SmfDiagnosticCodes.OverlappingNotes);
    }

    private static string Describe(Track track) =>
        track.Name + ":" + string.Join(";", track.ArrangedEvents.Select(e => e switch
        {
            NoteEvent n => $"N{n.Position}/{n.Duration}/{n.Channel}/{n.Note}/{n.Velocity}/{n.ReleaseVelocity}",
            ChannelEvent c => $"C{c with { Id = default }}",
            SysExEvent s => $"S{s.Position}/{Convert.ToHexString(s.Message.Bytes.Span)}",
            MetaEvent m => $"M{m.Position}/{m.Type}/{Convert.ToHexString(m.Data.Span)}",
            RawMidiEvent r => $"R{r.Position}/{Convert.ToHexString(r.Bytes.Span)}",
            _ => "?",
        }));
}
