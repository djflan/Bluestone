using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Signal;
using Bluestone.Signal.Live;
using static Bluestone.Tests.Unit.Signal.SignalFixture;

namespace Bluestone.Tests.Unit.Signal;

/// <summary>The Note Receiver (ADR 0037): events from anywhere in the project, brought into a chain at its place.</summary>
public sealed class ReceiverTests
{
    private static DeviceInstance NoteReceiver(bool replace = false) =>
        DeviceInstance.Create(BuiltInDevices.NoteReceiver.ToReference()).WithParameter(BuiltInDevices.ReceiverReplace, replace ? ControlValue.Max : ControlValue.Min);

    private static SignalNode Inlet(DeviceInstance receiver) => SignalNode.DeviceInput(receiver.Id, DeviceInputPort.Receive);

    [Theory]
    [InlineData(false, new[] { 48, 60 })]
    [InlineData(true, new[] { 48 })]
    public void ANoteReceiver_MixesOrReplaces_TheChainsNotes_WithAnotherTracks(bool replace, int[] heard)
    {
        // Lead's chain receives Bass's notes; whatever follows the receiver (here, hardware) gets the result.
        var lead = TrackOf("Lead", Note(0, 60));
        var bass = TrackOf("Bass", Note(0, 48));
        var receiver = NoteReceiver(replace);
        var project = NewProject()
            .With(bass)
            .With(lead, receiver)
            .WithInstrument(out var mu2000)
            .Connect(SignalNode.Track(bass.Id), Inlet(receiver))
            .Connect(out var output, SignalNode.Track(lead.Id), SignalNode.ExternalPart(mu2000.Id));

        var result = project.Evaluate();

        Assert.Equal(heard, result.PartFor(output).Events.Notes().Select(n => (int)n.Note.Value).Order());
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is SignalDiagnosticCode.InvalidConnection or SignalDiagnosticCode.Feedback);
    }

    [Fact]
    public void ANoteReceiver_CanTakeAnEarlierPointOfItsOwnChain_AndApplyTheConnectionsMapping()
    {
        // The dry note from the chain's input comes back in after the transposer, forced to channel 2.
        var keys = TrackOf("Keys", Note(0, 60));
        var transpose = BuiltInDevices.CreateTranspose(12);
        var receiver = NoteReceiver();
        var project = NewProject().With(keys, transpose, receiver).WithInstrument(out var mu2000);
        var chain = project.ChainOf(keys.Id)!;
        project = project
            .Connect(SignalNode.ChainInput(chain.Id), Inlet(receiver), ChannelMapping.ForceTo(MidiChannel.FromNumber(2)))
            .Connect(out var output, SignalNode.Track(keys.Id), SignalNode.ExternalPart(mu2000.Id));

        var notes = project.Evaluate().PartFor(output).Events.Notes().OrderBy(n => n.Note.Value).ToList();

        Assert.Equal([(60, 2), (72, 1)], notes.Select(n => ((int)n.Note.Value, n.Channel.Number)));
    }

    [Fact]
    public void ABypassedReceiver_BringsNothingIn()
    {
        var lead = TrackOf("Lead", Note(0, 60));
        var bass = TrackOf("Bass", Note(0, 48));
        var receiver = NoteReceiver(replace: true) with { IsBypassed = true };
        var project = NewProject().With(bass).With(lead, receiver).WithInstrument(out var mu2000)
            .Connect(SignalNode.Track(bass.Id), Inlet(receiver))
            .Connect(out var output, SignalNode.Track(lead.Id), SignalNode.ExternalPart(mu2000.Id));

        Assert.Equal([60], project.Evaluate().PartFor(output).Events.Notes().Select(n => (int)n.Note.Value));
    }

    [Fact]
    public void AfterAReceiver_EventsAndAudioMayBePresent_EvenOnAnEmptyChain()
    {
        var bus = TrackOf("Bus").WithRole(TrackRole.Effect);
        var audio = DeviceInstance.Create(BuiltInDevices.AudioReceiver.ToReference());
        var receiver = NoteReceiver(replace: true);
        var project = NewProject().With(bus, receiver, audio);

        var presence = SignalFlow.PresenceOf(project, SignalNode.Device(audio.Id), Catalog.Lookup);

        Assert.True(presence.Carries(SignalKind.Audio));
        Assert.NotEqual(EventClass.None, presence.Events & EventClass.Notes);
    }

    [Fact]
    public void TheLiveReceiver_StopsTheTracksOwnNotesInReplace_ButNeverLeavesOneHanging()
    {
        var processor = new LiveNoteReceiverProcessor();
        var output = new LiveEventBuffer(16);
        var block = new LiveBlock(0, 0);
        processor.Process(block, [LiveEvent.NoteOn(0, 0, 60, 100)], output);
        processor.SetParameter(BuiltInDevices.ReceiverReplace, ControlValue.Max);
        processor.Process(block, [LiveEvent.NoteOn(0, 0, 62, 100), LiveEvent.NoteOff(0, 0, 60)], output);

        Assert.Equal([(true, 60), (false, 60)], output.Events.ToArray().Select(e => (e.IsNoteOn, e.NoteSlot & 127)));
    }
}
