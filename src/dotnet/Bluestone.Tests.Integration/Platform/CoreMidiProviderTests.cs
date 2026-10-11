using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Platform.CoreMidi;
using Bluestone.Playback;
using Bluestone.Signal;

namespace Bluestone.Tests.Integration.Platform;

/// <summary>
/// Exercises the real CoreMIDI stack on macOS using only virtual ports and, when enabled, the IAC
/// bus. These tests never send to physical hardware.
/// </summary>
[SupportedOSPlatform("macos")]
[Collection(TimingTests.Name)]
public sealed class CoreMidiProviderTests
{
    private static readonly SystemMonotonicClock Clock = SystemMonotonicClock.Instance;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequireMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("CoreMIDI is only available on macOS.");
        }
    }

    private static async Task<T> EventuallyAsync<T>(Func<T?> probe, string what)
        where T : class
    {
        var deadline = Clock.Now + TimeSpan.FromSeconds(5);
        while (Clock.Now < deadline)
        {
            if (probe() is { } value)
            {
                return value;
            }

            await Task.Delay(20, Ct);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    [Fact]
    public void Provider_EnumeratesWithoutHardware()
    {
        RequireMacOS();
        using var provider = new CoreMidiProvider(Clock, "Bluestone Tests");

        var endpoints = provider.GetEndpoints();

        Assert.All(endpoints, e => Assert.Equal(CoreMidiProvider.ProviderId, e.Id.Provider));
        TestContext.Current.SendDiagnosticMessage($"CoreMIDI endpoints: {string.Join("; ", endpoints.Select(e => $"{e.Direction} {e.DisplayName} [{e.Transport}]"))}");
    }

    [Fact]
    public async Task VirtualOutput_IsReceivedByAnotherClient()
    {
        RequireMacOS();
        var name = $"Bluestone Test {Guid.NewGuid():N}"[..24];
        using var publisher = new CoreMidiProvider(Clock, "Bluestone Test Publisher");
        using var listener = new CoreMidiProvider(Clock, "Bluestone Test Listener");

        var outputId = publisher.CreateVirtualOutput(name);
        Assert.Contains(publisher.GetEndpoints(), e => e.Id == outputId && e.Direction == EndpointDirection.Output && e.Transport == EndpointTransport.Virtual);
        var source = await EventuallyAsync(() => listener.GetEndpoints().FirstOrDefault(e => e.Direction == EndpointDirection.Input && e.DisplayName == name), "the virtual source");

        using var input = await listener.OpenInputAsync(source.Id, Ct);
        var received = new ConcurrentQueue<byte[]>();
        input.SetReceiver((message, _) => received.Enqueue(message.ToArray()));
        using var output = await publisher.OpenOutputAsync(outputId, Ct);

        Assert.Equal(SendResult.Sent, output.Send([0x90, 0x3C, 0x64], MidiTimestamp.Immediate));
        Assert.Equal(SendResult.Sent, output.Send([0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7], MidiTimestamp.Immediate));
        Assert.Equal(SendResult.Rejected, output.Send([0x90, 0x3C], MidiTimestamp.Immediate));

        await EventuallyAsync(() => received.Count >= 2 ? received : null, "two messages");
        Assert.Equal(["903C64", "F07E7F0901F7"], received.Select(Convert.ToHexString));
    }

    [Fact]
    public async Task RemovedVirtualPort_DisconnectsListeners()
    {
        RequireMacOS();
        var name = $"Bluestone Test {Guid.NewGuid():N}"[..24];
        using var listener = new CoreMidiProvider(Clock, "Bluestone Test Listener");
        var changes = 0;
        listener.EndpointsChanged += (_, _) => Interlocked.Increment(ref changes);
        IMidiInput input;

        using (var publisher = new CoreMidiProvider(Clock, "Bluestone Test Publisher"))
        {
            publisher.CreateVirtualOutput(name);
            var source = await EventuallyAsync(() => listener.GetEndpoints().FirstOrDefault(e => e.DisplayName == name), "the virtual source");
            input = await listener.OpenInputAsync(source.Id, Ct);
        }

        using (input)
        {
            await EventuallyAsync(() => input.State == EndpointState.Disconnected ? input : null, "the input to disconnect");
            Assert.True(Volatile.Read(ref changes) > 0);
            Assert.DoesNotContain(listener.GetEndpoints(), e => e.DisplayName == name);
        }
    }

    [Fact]
    public async Task Playback_ThroughBluestoneVirtualPort_ArrivesInOrder()
    {
        RequireMacOS();
        var name = $"Bluestone Test {Guid.NewGuid():N}"[..24];
        using var bluestone = new CoreMidiProvider(Clock, "Bluestone Test Player");
        using var synth = new CoreMidiProvider(Clock, "Bluestone Test Synth");
        var outputId = bluestone.CreateVirtualOutput(name);
        var source = await EventuallyAsync(() => synth.GetEndpoints().FirstOrDefault(e => e.Direction == EndpointDirection.Input && e.DisplayName == name), "the virtual source");
        using var input = await synth.OpenInputAsync(source.Id, Ct);
        var received = new ConcurrentQueue<byte[]>();
        input.SetReceiver((message, _) => received.Enqueue(message.ToArray()));
        using var output = await bluestone.OpenOutputAsync(outputId, Ct);

        var channel = MidiChannel.FromIndex(0);
        var track = Track.FromEvents(TrackId.New(), "t", Enumerable.Range(0, 16)
            .Select(i => (TrackEvent)new NoteEvent(new Tick(i * 10), new TickSpan(5), channel, new NoteNumber(48 + i), Velocity.Max)));
        var sequence = Sequence.CreateEmpty(new Ppqn(500)).WithTrack(track);
        var part = new PlanPart(0, [.. track.ArrangedEvents.Select((e, i) => new SignalEvent(e, 0, i))])
        {
            InitialEvents = [new SignalEvent(new ProgramEvent(Tick.Zero, channel, new ProgramSelection(new ProgramNumber(5), BankMsb: SevenBitValue.Min)), 0, -1)],
        };
        using var engine = new PlaybackEngine(Clock, sequence.TempoMap);
        engine.SetOutputs([output]);
        engine.Load(PlaybackPlanCompiler.Compile(sequence, [part]));

        using (new PlaybackThread(engine))
        {
            engine.Play(Tick.Zero);
            await EventuallyAsync(() => received.Count >= 34 ? received : null, "all notes");
            engine.Stop();
        }

        var messages = received.ToList();
        Assert.Equal(["B00000", "C005"], messages.Take(2).Select(Convert.ToHexString));
        Assert.Equal(Enumerable.Range(48, 16), messages.Where(m => m[0] == 0x90).Select(m => (int)m[1]));
        Assert.Equal(0, engine.Statistics.Snapshot().Dropped);
    }

    [Fact]
    public async Task IacBus_DeliversScheduledMessagesOnTime()
    {
        RequireMacOS();
        using var provider = new CoreMidiProvider(Clock, "Bluestone Test IAC");
        var endpoints = provider.GetEndpoints();
        var iacOut = endpoints.FirstOrDefault(e => e.Direction == EndpointDirection.Output && e.DisplayName.Contains("IAC", StringComparison.OrdinalIgnoreCase));
        var iacIn = iacOut is null ? null : endpoints.FirstOrDefault(e => e.Direction == EndpointDirection.Input && e.DisplayName == iacOut.DisplayName);
        if (iacOut is null || iacIn is null)
        {
            Assert.Skip("The IAC Driver bus is not enabled (Audio MIDI Setup → IAC Driver → Device is online).");
            return;
        }

        using var input = await provider.OpenInputAsync(iacIn.Id, Ct);
        var arrivals = new ConcurrentQueue<(byte[] Bytes, TimeSpan Stamp, TimeSpan Arrived)>();
        input.SetReceiver((message, stamp) => arrivals.Enqueue((message.ToArray(), stamp, Clock.Now)));
        using var output = await provider.OpenOutputAsync(iacOut.Id, Ct);

        var due = Clock.Now + TimeSpan.FromMilliseconds(100);
        Assert.Equal(SendResult.Sent, output.Send([0x9F, 0x01, 0x01], MidiTimestamp.At(due)));
        Assert.Equal(SendResult.Sent, output.Send([0x8F, 0x01, 0x00], MidiTimestamp.Immediate));

        await EventuallyAsync(() => arrivals.Count >= 2 ? arrivals : null, "IAC loopback");
        var ordered = arrivals.ToList();
        var scheduled = ordered.Single(a => a.Bytes[0] == 0x9F);
        var immediate = ordered.Single(a => a.Bytes[0] == 0x8F);
        var lateness = scheduled.Arrived - due;
        TestContext.Current.SendDiagnosticMessage($"IAC scheduled delivery lateness: {lateness.TotalMilliseconds:0.00} ms; immediate arrived {(immediate.Arrived - (due - TimeSpan.FromMilliseconds(100))).TotalMilliseconds:0.00} ms after send");
        Assert.InRange(lateness, TimeSpan.FromMilliseconds(-5), TimeSpan.FromMilliseconds(30));
        Assert.True(immediate.Arrived < scheduled.Arrived);
    }
}
