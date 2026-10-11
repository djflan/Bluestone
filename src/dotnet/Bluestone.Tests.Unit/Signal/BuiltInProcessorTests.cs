using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Signal;
using Bluestone.Signal.BuiltIn;
using static Bluestone.Tests.Unit.Signal.SignalFixture;

namespace Bluestone.Tests.Unit.Signal;

public sealed class BuiltInProcessorTests
{
    [Fact]
    public void Catalog_HasTheBuiltIns_AndRunsThem()
    {
        var catalog = DeviceCatalog.BuiltIn;

        Assert.Equal(["bluestone.midi.arpeggiator", "bluestone.midi.event-filter", "bluestone.midi.transpose"], catalog.Definitions.Select(d => d.Id.Value));
        Assert.All(catalog.Definitions, d => Assert.True(catalog.CanProcess(d.Id)));
        Assert.All(catalog.Definitions, d => Assert.Equal(DeviceOrigin.BuiltIn, d.Origin));
    }

    [Fact]
    public void Catalog_RefusesAnInProcessFactoryForAPlugin() =>
        Assert.Throws<ArgumentException>(() => DeviceCatalog.Empty.With(PluginArp, _ => new TransposeProcessor()));

    [Fact]
    public void Catalog_KeepsPluginDefinitionsAsDataOnly()
    {
        var catalog = DeviceCatalog.Empty.With(PluginArp);

        Assert.Same(PluginArp, catalog.Find(PluginArp.Id));
        Assert.False(catalog.CanProcess(PluginArp.Id));
        Assert.Null(catalog.CreateProcessor(PluginArp.Id, Resolution));
    }

    [Fact]
    public void Transpose_MovesNotesReleasesAndPolyPressure()
    {
        var note = Note(0, 60);
        var release = new NoteOffEvent(EventId.New(), new Tick(10), note.Channel, new NoteNumber(62), Velocity.DefaultRelease);
        var pressure = new PolyPressureEvent(EventId.New(), new Tick(5), note.Channel, new NoteNumber(61), ControlValue.Center);

        var output = Run(Chain(BuiltInDevices.CreateTranspose(12)), Keyed(0, note, release, pressure));

        Assert.Equal(72, ((NoteEvent)output[0].Event).Note.Value);
        Assert.Equal(73, ((PolyPressureEvent)output[1].Event).Note.Value);
        Assert.Equal(74, ((NoteOffEvent)output[2].Event).Note.Value);
        Assert.Equal(note.Id, output[0].Event.Id);
    }

    [Fact]
    public void Transpose_DropsNotesThatLeaveTheRange_AndReportsThem()
    {
        var device = BuiltInDevices.CreateTranspose(24);
        var runner = new ChainRunner(Chain(device), Catalog, Resolution);
        var output = new SignalBuffer();

        runner.Run(SignalBlock.Everything, Keyed(0, Note(0, 110), Note(10, 104), Note(20, 60)), output);

        Assert.Equal([84], output.Events.ToArray().Notes().Select(n => (int)n.Note.Value));
        var report = Assert.Single(runner.TakeReports());
        Assert.Equal(SignalDiagnosticCode.DeviceReport, report.Code);
        Assert.Contains("2 notes were transposed outside the MIDI range", report.Message, StringComparison.Ordinal);
        Assert.Equal(device.Id, report.Device);
        Assert.Empty(runner.TakeReports());
    }

    [Theory]
    [InlineData(-48)]
    [InlineData(-7)]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(48)]
    public void Transpose_StoredSemitones_ComeBackExactly(int semitones)
    {
        var output = Run(Chain(BuiltInDevices.CreateTranspose(semitones)), Keyed(0, Note(0, 60)));

        Assert.Equal(60 + semitones, Assert.Single(output.Notes()).Note.Value);
    }

    [Fact]
    public void EventFilter_RemovesOnlyTheBlockedClasses()
    {
        var events = Keyed(0, XgOn(), Program(0, 5), Volume(0, 90), Note(0));

        var output = Run(Chain(BuiltInDevices.CreateEventFilter(EventClass.SystemExclusive | EventClass.Notes)), events);

        Assert.Equal([events[1], events[2]], output);
    }

    [Fact]
    public void EventFilter_WithDefaults_PassesEverything()
    {
        var events = Keyed(0, XgOn(), Program(0, 5), Volume(0, 90), Note(0));

        var output = Run(Chain(Instance(BuiltInDevices.EventFilter)), events);

        Assert.Equal(events, output);
    }

    [Fact]
    public void Arpeggiator_ThreeNotesHeldForThreeBeats_BecomeTwelveSixteenths()
    {
        var chord = Keyed(0, Note(0, 60, length: 1440), Note(0, 64, length: 1440), Note(0, 67, length: 1440));

        var notes = Run(Chain(BuiltInDevices.CreateArpeggiator(rate: 2)), chord).Notes().ToList();

        Assert.Equal(12, notes.Count);
        Assert.Equal([0, 120, 240, 360, 480, 600, 720, 840, 960, 1080, 1200, 1320], notes.Select(n => n.Position.Value));
        Assert.Equal([60, 64, 67, 60, 64, 67, 60, 64, 67, 60, 64, 67], notes.Select(n => (int)n.Note.Value));
        Assert.All(notes, n => Assert.Equal(60, n.Duration.Value));
    }

    [Theory]
    [InlineData(ArpeggiatorPattern.Up, 1, new[] { 60, 64, 67, 60, 64, 67 })]
    [InlineData(ArpeggiatorPattern.Down, 1, new[] { 67, 64, 60, 67, 64, 60 })]
    [InlineData(ArpeggiatorPattern.UpDown, 1, new[] { 60, 64, 67, 64, 60, 64 })]
    [InlineData(ArpeggiatorPattern.Up, 2, new[] { 60, 64, 67, 72, 76, 79 })]
    public void Arpeggiator_FollowsItsPatternAndOctaves(ArpeggiatorPattern pattern, int octaves, int[] expected)
    {
        var chord = Keyed(0, Note(0, 67, length: 720), Note(0, 60, length: 720), Note(0, 64, length: 720));

        var notes = Run(Chain(BuiltInDevices.CreateArpeggiator(rate: 2, pattern, octaves)), chord).Notes();

        Assert.Equal(expected, notes.Select(n => (int)n.Note.Value));
    }

    [Fact]
    public void Arpeggiator_GeneratedNotes_KeepTheChannelVelocityAndKeyOfTheirSource()
    {
        var low = new NoteEvent(new Tick(0), new TickSpan(240), MidiChannel.FromNumber(3), new NoteNumber(48), new Velocity(30));
        var high = new NoteEvent(new Tick(0), new TickSpan(240), MidiChannel.FromNumber(4), new NoteNumber(72), new Velocity(110));
        var input = Keyed(5, low, high);

        var output = Run(Chain(BuiltInDevices.CreateArpeggiator(rate: 2)), input);

        Assert.Equal([3, 4], output.Notes().Select(n => n.Channel.Number));
        Assert.Equal([30, 110], output.Notes().Select(n => (int)n.Velocity.Value));
        Assert.Equal([input[0].Sequence, input[1].Sequence], output.Select(e => e.Sequence));
        Assert.All(output, e => Assert.Equal(5, e.Origin));
    }

    [Fact]
    public void Arpeggiator_StartsAPhraseAtTheFirstNote_AndStopsWhenNothingIsHeld()
    {
        var input = Keyed(0, Note(50, 60, length: 200), Note(1000, 62, length: 100));

        var notes = Run(Chain(BuiltInDevices.CreateArpeggiator(rate: 2)), input).Notes();

        Assert.Equal([50L, 170, 1000], notes.Select(n => n.Position.Value));
    }

    [Fact]
    public void Arpeggiator_ANotePlayedAfterAGap_StartsANewPhraseAtItsOwnTick()
    {
        var input = Keyed(0, Note(0, 60, length: 100), Note(110, 62, length: 100));

        var notes = Run(Chain(BuiltInDevices.CreateArpeggiator(rate: 2)), input).Notes();

        Assert.Equal([0L, 110], notes.Select(n => n.Position.Value));
    }

    [Fact]
    public void Arpeggiator_ALegatoNote_ContinuesThePhrase()
    {
        var input = Keyed(0, Note(0, 60, length: 100), Note(100, 62, length: 100));

        var notes = Run(Chain(BuiltInDevices.CreateArpeggiator(rate: 2)), input).Notes();

        Assert.Equal([0L, 120], notes.Select(n => n.Position.Value));
    }

    [Fact]
    public void Arpeggiator_GivesTheSameEventsEveryTime()
    {
        var chain = Chain(BuiltInDevices.CreateArpeggiator(rate: 3, ArpeggiatorPattern.UpDown, 2));
        var input = Keyed(0, Note(0, 60, length: 960), Note(0, 64, length: 960));

        var first = Run(chain, input);
        var second = Run(chain, input);

        Assert.Equal(first, second);
        Assert.Equal(first.Length, first.Select(e => e.Event.Id).Distinct().Count());
    }

    [Fact]
    public void Arpeggiator_InBlocks_PlaysTheSameAsInOneBlock()
    {
        var input = Keyed(0, Note(0, 60, length: 1000), Note(100, 64, length: 800), Note(700, 67, length: 900), Note(2000, 70, length: 300));
        var whole = Run(Chain(BuiltInDevices.CreateArpeggiator(rate: 3, ArpeggiatorPattern.UpDown, 2)), input);

        var processor = new ArpeggiatorProcessor(Resolution);
        var device = BuiltInDevices.CreateArpeggiator(rate: 3, ArpeggiatorPattern.UpDown, 2);
        foreach (var parameter in device.Parameters)
        {
            processor.SetParameter(parameter.Id, parameter.Value);
        }

        var output = new SignalBuffer();
        for (long start = 0; start < 3000; start += 97)
        {
            var block = new SignalBlock(new Tick(start), new Tick(start + 97));
            processor.Process(block, input.Where(e => block.Contains(e.Event.Position)).ToArray(), output);
        }

        Assert.Equal(whole, output.Events.ToArray());
    }

    [Fact]
    public void Arpeggiator_Reset_ForgetsHeldNotes()
    {
        var processor = new ArpeggiatorProcessor(Resolution);
        var output = new SignalBuffer();
        processor.Process(new SignalBlock(Tick.Zero, new Tick(100)), Keyed(0, Note(0, 60, length: 2000)), output);

        processor.Reset();
        output.Clear();
        processor.Process(new SignalBlock(new Tick(100), new Tick(2000)), ReadOnlySpan<SignalEvent>.Empty, output);

        Assert.Equal(0, output.Count);
    }

    [Fact]
    public void Processors_IgnoreParametersTheyDoNotHave()
    {
        ImmutableArray<ISignalProcessor> processors = [new TransposeProcessor(), new EventFilterProcessor(), new ArpeggiatorProcessor(Resolution)];

        foreach (var processor in processors)
        {
            processor.SetParameter(new ParameterId(99), ControlValue.Max);
        }

        var output = new SignalBuffer();
        processors[0].Process(SignalBlock.Everything, Keyed(0, Note(0)), output);
        Assert.Equal(60, Assert.Single(output.Events.ToArray().Notes()).Note.Value);
    }
}
