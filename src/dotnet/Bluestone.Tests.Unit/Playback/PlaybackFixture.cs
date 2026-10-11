using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Playback;

namespace Bluestone.Tests.Unit.Playback;

/// <summary>
/// An engine wired to loopback outputs on a virtual clock. At 500 PPQN and 120 BPM one tick is exactly
/// one millisecond, so tick numbers double as times.
/// </summary>
internal sealed class PlaybackFixture : IDisposable
{
    public static readonly Ppqn Resolution = new(500);
    public static readonly MidiChannel One = MidiChannel.FromIndex(0);
    public static readonly TimeSpan Start = TimeSpan.FromSeconds(100);

    private readonly List<IMidiOutput> _outputs = [];

    public PlaybackFixture(PlaybackOptions? options = null, params EndpointCapabilities[] outputs)
    {
        if (outputs.Length == 0)
        {
            outputs = [EndpointCapabilities.ScheduledDelivery | EndpointCapabilities.SystemExclusive];
        }

        Engine = new PlaybackEngine(Clock, TempoMap.Constant(Resolution, Tempo.Default), options);
        foreach (var capabilities in outputs)
        {
            var provider = new LoopbackMidiProvider(Clock, capabilities);
            Providers.Add(provider);
            var port = provider.CreatePort($"Port {Ports.Count + 1}", $"p{Ports.Count + 1}");
            Ports.Add(port);
            _outputs.Add(provider.OpenOutputAsync(port.OutputId).AsTask().GetAwaiter().GetResult());
        }

        Engine.SetOutputs(_outputs);
    }

    public VirtualClock Clock { get; } = new(Start);

    public PlaybackEngine Engine { get; }

    public List<LoopbackMidiProvider> Providers { get; } = [];

    public List<LoopbackPort> Ports { get; } = [];

    public LoopbackPort Port => Ports[0];

    /// <summary>The open output for each port, as given to the engine.</summary>
    public IReadOnlyList<IMidiOutput> Outputs => _outputs;

    public static NoteEvent Note(long at, long length, int note = 60, int velocity = 100) =>
        new(EventId.New(), new Tick(at), new TickSpan(length), One, new NoteNumber(note), new Velocity(velocity), new Velocity(64));

    public static ControllerEvent Cc(long at, int controller, int value, int channel = 0) =>
        new(new Tick(at), MidiChannel.FromIndex(channel), new ControllerNumber(controller), ControlValue.FromSevenBit(value));

    public static ProgramEvent Program(long at, int program) =>
        new(new Tick(at), One, new ProgramSelection(new ProgramNumber(program)));

    public static PlaybackPlan Plan(params TrackEvent[] events) => Plan(TempoMap.Constant(Resolution, Tempo.Default), events);

    public static PlaybackPlan Plan(TempoMap tempo, params TrackEvent[] events)
    {
        var track = Track.FromEvents(TrackId.New(), "t", events);
        var sequence = new Sequence(tempo, MeterMap.Constant(tempo.Ppqn, TimeSignature.CommonTime), [track], []);
        return PlanDump.Compile(PlanDump.Routed(sequence));
    }

    public void Load(params TrackEvent[] events) => Engine.Load(Plan(events));

    /// <summary>Advances to <paramref name="milliseconds"/> after <see cref="Start"/> and pumps.</summary>
    public TimeSpan PumpAt(double milliseconds)
    {
        Clock.AdvanceTo(Start + TimeSpan.FromMilliseconds(milliseconds));
        return Engine.Pump();
    }

    /// <summary>Messages sent to a port as "HEX@ms", where ms is the requested delivery time (or send time when immediate).</summary>
    public List<string> Sent(int port = 0) =>
        [.. Ports[port].Sent.Select(m =>
        {
            var time = m.Requested.IsImmediate ? m.SentAt : m.Requested.Time;
            return $"{Convert.ToHexString(m.Bytes)}@{(time - Start).TotalMilliseconds:0.###}";
        })];

    public void Dispose()
    {
        foreach (var output in _outputs)
        {
            output.Dispose();
        }

        foreach (var provider in Providers)
        {
            provider.Dispose();
        }

        Engine.Dispose();
    }
}
