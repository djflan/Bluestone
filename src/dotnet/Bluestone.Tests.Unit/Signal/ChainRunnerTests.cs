using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Signal;
using Bluestone.Signal.BuiltIn;
using static Bluestone.Tests.Unit.Signal.SignalFixture;

namespace Bluestone.Tests.Unit.Signal;

public sealed class ChainRunnerTests
{
    [Fact]
    public void ADevice_IsGivenOnlyTheClassesItHandles_AndTheRestComesBackInOrder()
    {
        var recorder = new RecordingProcessor();
        var events = Keyed(0, XgOn(0), Volume(0, 100), Note(0, 60), Program(240, 3), Note(240, 62), XgOn(480));

        var output = Run(Chain(Instance(Recorder)), events, CatalogWith(recorder));

        Assert.All(recorder.Received, e => Assert.IsType<NoteEvent>(e));
        Assert.Equal(2, recorder.Received.Count);
        Assert.Equal(events, output);
    }

    [Fact]
    public void Devices_RunInSequence_EachOnThePreviousOutput()
    {
        // Three notes, arpeggiated into twelve, then transposed: the transpose sees the twelve.
        var recorder = new RecordingProcessor();
        var chord = Keyed(0, Note(0, 60, length: 1440), Note(0, 64, length: 1440), Note(0, 67, length: 1440));

        var output = Run(Chain(BuiltInDevices.CreateArpeggiator(rate: 2), BuiltInDevices.CreateTranspose(7), Instance(Recorder)), chord, CatalogWith(recorder));

        Assert.Equal(12, recorder.Received.Count);
        Assert.Equal([67, 71, 74, 67, 71, 74, 67, 71, 74, 67, 71, 74], output.Notes().Select(n => (int)n.Note.Value));
    }

    [Fact]
    public void ABypassedDevice_IsTransparent()
    {
        var events = Keyed(0, XgOn(), Note(0, 60), Note(120, 64));
        var arp = BuiltInDevices.CreateArpeggiator() with { IsBypassed = true };
        var transpose = BuiltInDevices.CreateTranspose(12) with { IsBypassed = true };

        var output = Run(Chain(arp, transpose), events);

        Assert.Equal(events, output);
    }

    [Fact]
    public void AnEmptyChain_PassesEverythingThrough()
    {
        var events = Keyed(0, XgOn(), Volume(0, 64), Note(0));

        Assert.Equal(events, Run(Chain(), events));
    }

    [Fact]
    public void ADeviceThatIsNotInstalled_PassesEventsThrough_AndIsReported()
    {
        var missing = DeviceInstance.Create(new DeviceReference(new DeviceDefinitionId("vst3:gone"), "Gone Arp"));
        var events = Keyed(0, Note(0));

        var runner = new ChainRunner(Chain(missing), Catalog, Resolution);
        var output = new SignalBuffer();
        runner.Run(SignalBlock.Everything, events, output);

        Assert.Equal(events, output.Events.ToArray());
        var diagnostic = Assert.Single(runner.Diagnostics);
        Assert.Equal(SignalDiagnosticCode.DeviceUnavailable, diagnostic.Code);
        Assert.Contains("Gone Arp", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APluginMidiEffect_IsNotRunInProcess_ItsEventsPassThrough_AndThatIsReported()
    {
        var plugin = Instance(PluginArp);
        var events = Keyed(0, Note(0), Note(10, 64));

        var runner = new ChainRunner(Chain(plugin), Catalog, Resolution);
        var output = new SignalBuffer();
        runner.Run(SignalBlock.Everything, events, output);

        Assert.Equal(events, output.Events.ToArray());
        Assert.Equal(StageKind.Transparent, runner.KindOf(plugin.Id));
        var diagnostic = Assert.Single(runner.Diagnostics);
        Assert.Equal(SignalDiagnosticCode.NotProcessedHere, diagnostic.Code);
        Assert.Contains("plugin worker", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOutOfProcessDevice_IsGivenItsClassesAndItsOwnChanges_AndItsOutputIsMergedBack()
    {
        var plugin = Instance(PluginArp);
        var other = Instance(Recorder);
        var renderer = new ShiftingRenderer(semitones: 5);
        var events = Keyed(0, XgOn(), Volume(0, 90), Note(0, 60), Note(240, 64));
        ParameterChange[] changes =
        [
            new(new Tick(120), plugin.Id, new ParameterId(0), ControlValue.Max),
            new(new Tick(120), other.Id, new ParameterId(0), ControlValue.Max),
        ];

        var runner = new ChainRunner(Chain(plugin), Catalog.WithRenderer(renderer), Resolution);
        var output = new SignalBuffer();
        runner.Run(SignalBlock.Everything, events, output, changes);

        Assert.Equal(StageKind.OutOfProcess, runner.KindOf(plugin.Id));
        Assert.Empty(runner.Diagnostics);
        Assert.All(renderer.Received, e => Assert.IsType<NoteEvent>(e.Event));
        Assert.Equal(2, renderer.Received.Count);
        Assert.Equal(plugin.Id, Assert.Single(renderer.Changes).Device);
        Assert.Equal(Resolution, renderer.LastBlock!.Value.Ppqn);
        Assert.Equal([events[0], events[1]], output.Events[..2].ToArray());
        Assert.Equal([65, 69], output.Events.ToArray().Notes().Select(n => (int)n.Note.Value));
    }

    [Fact]
    public void AnOutOfProcessDevice_ThatFails_PassesItsEventsThroughUnchanged_AndTheFailureIsReported()
    {
        var plugin = Instance(PluginArp);
        var events = Keyed(0, XgOn(), Note(0, 60), Note(240, 64));

        var failing = new ChainRunner(Chain(plugin), Catalog.WithRenderer(new ShiftingRenderer(0) { Fail = "is not running." }), Resolution);
        var throwing = new ChainRunner(Chain(plugin), Catalog.WithRenderer(new ShiftingRenderer(0) { Throw = true }), Resolution);
        var failed = new SignalBuffer();
        var thrown = new SignalBuffer();
        failing.Run(SignalBlock.Everything, events, failed);
        throwing.Run(SignalBlock.Everything, events, thrown);

        Assert.Equal(events, failed.Events.ToArray());
        Assert.Equal(events, thrown.Events.ToArray());
        var report = Assert.Single(failing.TakeReports());
        Assert.Equal(SignalDiagnosticCode.NotProcessedHere, report.Code);
        Assert.Equal("Plugin Arp: is not running. Events pass through it unchanged.", report.Message);
        Assert.Contains("could not run", Assert.Single(throwing.TakeReports()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABypassedOutOfProcessDevice_IsNotRendered()
    {
        var plugin = Instance(PluginArp) with { IsBypassed = true };
        var renderer = new ShiftingRenderer(12);
        var events = Keyed(0, Note(0, 60));

        Assert.Equal(events, Run(Chain(plugin), events, Catalog.WithRenderer(renderer)));
        Assert.Empty(renderer.Received);
    }

    [Fact]
    public void AnInstrument_PlaysTheClassesItHandles_AndPassesTheWholeBundleOn()
    {
        var synth = Instance(TestDevices.Synth);
        var events = Keyed(0, XgOn(), Program(0, 1), Volume(0, 90), Note(0));
        var observer = new CollectingObserver();

        var output = Run(Chain(synth), events, observer: observer);

        Assert.Equal([events[1], events[2], events[3]], observer.Instruments[synth.Id]);
        Assert.Equal(events, output);
    }

    [Fact]
    public void TwoInstrumentsInARow_BothPlayTheNotes_SoTheyLayer()
    {
        var first = Instance(TestDevices.Synth);
        var second = Instance(TestDevices.Synth);
        var events = Keyed(0, Note(0, 60), Note(0, 64));
        var observer = new CollectingObserver();

        Run(Chain(first, second), events, observer: observer);

        Assert.Equal(events, observer.Instruments[first.Id]);
        Assert.Equal(events, observer.Instruments[second.Id]);
    }

    [Fact]
    public void ANoteEffectAfterAnInstrument_TransformsTheNotesTheInstrumentPassedOn()
    {
        var synth = Instance(TestDevices.Synth);
        var second = Instance(TestDevices.Synth);
        var events = Keyed(0, Note(0, 60));
        var observer = new CollectingObserver();

        Run(Chain(synth, BuiltInDevices.CreateTranspose(12), second), events, observer: observer);

        Assert.Equal(60, Assert.Single(observer.Instruments[synth.Id]).Event is NoteEvent n ? n.Note.Value : -1);
        Assert.Equal(72, Assert.Single(observer.Instruments[second.Id]).Event is NoteEvent m ? m.Note.Value : -1);
    }

    [Fact]
    public void AnAudioEffect_BetweenInstruments_PassesTheNotesOn()
    {
        var first = Instance(TestDevices.Synth);
        var second = Instance(TestDevices.Synth);
        var events = Keyed(0, Note(0, 60));
        var observer = new CollectingObserver();

        Run(Chain(first, Instance(TestDevices.Filter), second), events, observer: observer);

        Assert.Equal(events, observer.Instruments[second.Id]);
    }

    [Fact]
    public void AnInstrumentThatEndsTheNotePath_PassesOnlyWhatItDoesNotHandle()
    {
        var sink = TestDevices.Synth with { Id = new DeviceDefinitionId("test.sink"), Name = "Sink", EventThru = EventClass.None };
        var device = Instance(sink);
        var events = Keyed(0, XgOn(), Note(0));
        var observer = new CollectingObserver();

        var output = Run(Chain(device), events, Catalog.With(sink), observer: observer);

        Assert.Equal([events[1]], observer.Instruments[device.Id]);
        Assert.Equal([events[0]], output);
    }

    [Fact]
    public void ThroughClasses_AreDerivedFromTheCategory_UnlessTheDefinitionSaysOtherwise()
    {
        Assert.Equal(TestDevices.Synth.Handles, TestDevices.Synth.PassesThrough);
        Assert.Equal(EventClass.None, TestDevices.MidiFx.PassesThrough);
        Assert.Equal(EventClass.None, TestDevices.Filter.PassesThrough);
        Assert.Equal(EventClass.Notes, (TestDevices.Synth with { EventThru = EventClass.Notes | EventClass.SystemExclusive }).PassesThrough);
    }

    [Fact]
    public void ANoteEffectWithThru_MixesItsInputWithItsOutput()
    {
        var octave = BuiltInDevices.Transpose with { EventThru = EventClass.Notes };
        var catalog = Catalog.With(octave, _ => new TransposeProcessor());

        var output = Run(Chain(BuiltInDevices.CreateTranspose(12)), Keyed(0, Note(0, 60)), catalog);

        Assert.Equal([60, 72], output.Select(e => (int)((NoteEvent)e.Event).Note.Value).Order());
    }

    [Fact]
    public void AnAudioEffect_LeavesEventsAlone()
    {
        var events = Keyed(0, XgOn(), Note(0));

        Assert.Equal(events, Run(Chain(Instance(TestDevices.Filter)), events));
    }

    [Fact]
    public void TheObserver_SeesTheSignalAfterEachDevice()
    {
        var arp = BuiltInDevices.CreateArpeggiator(rate: 2);
        var transpose = BuiltInDevices.CreateTranspose(12);
        var observer = new CollectingObserver();

        Run(Chain(arp, transpose), Keyed(0, Note(0, 60, length: 480)), observer: observer);

        Assert.Equal([60, 60, 60, 60], observer.After[arp.Id].Notes().Select(n => (int)n.Note.Value));
        Assert.Equal([72, 72, 72, 72], observer.After[transpose.Id].Notes().Select(n => (int)n.Note.Value));
    }

    [Fact]
    public void ParameterChanges_ApplyFromTheirOwnTick()
    {
        var transpose = BuiltInDevices.CreateTranspose(0);
        var up = BuiltInDevices.Transpose.Parameters[0].ToStored(12);
        ImmutableArray<ParameterChange> changes = [new(new Tick(100), transpose.Id, BuiltInDevices.TransposeSemitones, up)];

        var output = Run(Chain(transpose), Keyed(0, Note(0), Note(99), Note(100), Note(200)), changes: changes);

        Assert.Equal([60, 60, 72, 72], output.Notes().Select(n => (int)n.Note.Value));
    }

    [Fact]
    public void ParameterChanges_ForADeviceThatIsBypassed_StillUpdateIt()
    {
        var recorder = new RecordingProcessor();
        var device = Instance(Recorder) with { IsBypassed = true };
        ImmutableArray<ParameterChange> changes = [new(new Tick(50), device.Id, new ParameterId(3), ControlValue.Max)];

        var output = Run(Chain(device), Keyed(0, Note(0)), CatalogWith(recorder), changes);

        Assert.Equal([(new ParameterId(3), ControlValue.Max)], recorder.Parameters);
        Assert.Empty(recorder.Received);
        Assert.Single(output);
    }

    [Fact]
    public void OutOfOrderOutput_IsPutBackInCanonicalOrder()
    {
        // The arpeggiator passes the release on before it generates the notes around it.
        var release = new NoteOffEvent(EventId.New(), new Tick(200), Note(0).Channel, Note(0).Note, Note(0).ReleaseVelocity);

        var output = Run(Chain(BuiltInDevices.CreateArpeggiator(rate: 2)), Keyed(0, Note(0, 60, length: 480), release));

        Assert.True(SignalOrder.IsOrdered(output.AsSpan()));
        Assert.Equal([0L, 120, 200, 240, 360], output.Select(e => e.Event.Position.Value));
    }

    [Fact]
    public void APassthroughChain_AllocatesNothingAfterWarmUp()
    {
        var chain = Chain(
            BuiltInDevices.CreateTranspose(0),
            Instance(BuiltInDevices.EventFilter),
            BuiltInDevices.CreateArpeggiator() with { IsBypassed = true },
            Instance(TestDevices.Filter));
        var events = Keyed(0, [.. Enumerable.Range(0, 500).SelectMany(i => new TrackEvent[] { Note(i * 10, 40 + (i % 40)), Volume(i * 10, i % 128), XgOn(i * 10) })]);
        var runner = new ChainRunner(chain, Catalog, Resolution);
        var output = new SignalBuffer(events.Length);
        var observer = new NullObserver();

        for (var i = 0; i < 3; i++)
        {
            output.Clear();
            runner.Run(SignalBlock.Everything, events, output, default, observer);
        }

        var allocated = Allocations.InSteadyState(
            () =>
            {
                output.Clear();
                runner.Run(SignalBlock.Everything, events, output, default, observer);
            },
            1);

        Assert.Equal(0, allocated);
        Assert.Equal(events, output.Events.ToArray());
    }

    private sealed class NullObserver : IChainObserver
    {
        public void AfterDevice(DeviceId device, ReadOnlySpan<SignalEvent> events)
        {
        }

        public void InstrumentInput(DeviceId device, ReadOnlySpan<SignalEvent> events)
        {
        }
    }

    // Moves notes like a MIDI effect in a worker would, recording what it was given.
    private sealed class ShiftingRenderer(int semitones) : IOutOfProcessRenderer
    {
        public List<SignalEvent> Received { get; } = [];

        public List<ParameterChange> Changes { get; } = [];

        public OutOfProcessBlock? LastBlock { get; private set; }

        public string? Fail { get; init; }

        public bool Throw { get; init; }

        public bool CanRender(DeviceInstance device, DeviceDefinition definition) => definition.Origin == DeviceOrigin.Plugin;

        public OutOfProcessResult Render(in OutOfProcessBlock block, ReadOnlySpan<SignalEvent> input, ReadOnlySpan<ParameterChange> changes)
        {
            LastBlock = block;
            Received.AddRange(input);
            Changes.AddRange(changes);
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            if (Fail is { } message)
            {
                return OutOfProcessResult.Failed(message);
            }

            return new OutOfProcessResult(
                [.. input.ToArray().Select(e => e.Event is NoteEvent n && n.Note.TryTranspose(semitones, out var note) ? e.With(n with { Note = note }) : e)],
                []);
        }
    }
}
