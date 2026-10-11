using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Tests.Unit.Infrastructure;

internal static class ProjectSamples
{
    /// <summary>A project exercising every event type, conductor data, routing options, and a loop.</summary>
    public static Project Full()
    {
        var ppqn = new Ppqn(480);
        var channel = MidiChannel.FromNumber(2);
        var bass = Track.FromEvents(TrackId.New(), "Bass", [
            new NoteEvent(EventId.New(), new Tick(0), new TickSpan(240), channel, new NoteNumber(40), new Velocity(100), new Velocity(30)),
            new ProgramEvent(new Tick(0), channel, new ProgramSelection(new ProgramNumber(33), new SevenBitValue(0), new SevenBitValue(64))),
            new PitchBendEvent(new Tick(10), channel, ControlValue.FromFourteenBit(9000)),
            new ChannelPressureEvent(new Tick(12), channel, ControlValue.FromSevenBit(5)),
            new ControllerEvent(new Tick(12), channel, ControllerNumber.ChannelVolume, new ControlValue(0x1234_5678)),
            new PolyPressureEvent(EventId.New(), new Tick(14), channel, NoteNumber.MiddleC, ControlValue.Max),
            new NoteOffEvent(EventId.New(), new Tick(16), channel, NoteNumber.MiddleC, new Velocity(3)),
            new SysExEvent(new Tick(0), SysExMessage.Create([0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7])),
            new RawMidiEvent(EventId.New(), new Tick(20), ByteBlock.Copy([0xF0, 0x43])),
            new MetaEvent(EventId.New(), new Tick(30), 0x05, ByteBlock.Copy("la"u8)),
        ], isMuted: true);
        var drums = new Track(
            TrackId.New(),
            "Drüms ♪",
            [new AudioClip(ClipId.New(), new Tick(960), new TickSpan(1920), new TickSpan(48), new AudioSource("audio/loop.wav", 48_000, 96_000, 2), "Loop")],
            isSoloed: true,
            role: TrackRole.Hybrid);
        // Two clips, one trimmed and named, with content hidden on both sides.
        var lead = new Track(TrackId.New(), "Lead", [
            new NoteClip(ClipId.New(), new Tick(1920), new TickSpan(960), new TickSpan(240), new EventList([
                new NoteEvent(new Tick(0), new TickSpan(120), channel, new NoteNumber(72), new Velocity(90)),
                new NoteEvent(new Tick(480), new TickSpan(2000), channel, new NoteNumber(74), new Velocity(91)),
                new ControllerEvent(new Tick(1300), channel, ControllerNumber.ModulationWheel, ControlValue.Max),
            ]), "Hook"),
            new NoteClip(ClipId.New(), new Tick(3840), new TickSpan(480), TickSpan.Zero, new EventList([
                new NoteEvent(new Tick(0), new TickSpan(240), channel, new NoteNumber(76), new Velocity(92)),
            ])),
        ], automation: [
            new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForController(channel, ControllerNumber.ChannelVolume), [
                new AutomationPoint(new Tick(0), ControlValue.FromSevenBit(10)),
                new AutomationPoint(new Tick(1920), new ControlValue(0xDEAD_BEEF), AutomationCurve.Hold),
            ]),
            new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForPitchBend(channel), []),
            new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForChannelPressure(MidiChannel.FromNumber(16)), [new AutomationPoint(new Tick(5), ControlValue.Max)]),
        ]);
        var group = Track.Create("Band", TrackRole.Group);
        var arp = BuiltInDevices.CreateArpeggiator(rate: 3, ArpeggiatorPattern.UpDown, octaves: 2) with { Name = "Up and down" };
        var plugin = DeviceInstance.Create(new DeviceReference(new DeviceDefinitionId("vst3:ABCDEF0123456789ABCDEF0123456789"), "Vital", "1.5")) with
        {
            IsBypassed = true,
            Parameters = [new ParameterValue(new ParameterId(4_000_000_001), ControlValue.Center)],
            State = new PluginState(ByteBlock.Copy([0, 1, 2, 0xFF]), "vst3-component-v1"),
        };
        lead = lead.WithLane(new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForDevice(arp.Id, BuiltInDevices.ArpeggiatorRate), [new AutomationPoint(Tick.Zero, ControlValue.Max, AutomationCurve.Hold)]));
        var sequence = new Sequence(
            new TempoMap(ppqn, [new TempoChange(new Tick(960), new Tempo(400_000))]),
            new MeterMap(ppqn, [new MeterChange(new Tick(1920), new TimeSignature(7, 8))]),
            [bass, drums, lead.WithGroup(group.Id), group.WithFolded(true)],
            [new Marker(new Tick(480), "Verse \"A\"")]);

        var rack = DeviceChain.Create(ChainOwner.Rack, "Shared synth") with { Devices = [plugin] };
        var bus = MixerChannel.Create("Synth bus") with { GainDecibels = -3.5, Pan = 0.25, IsMuted = true };
        var master = MixerChannel.Create("Sub mix") with { IsSoloed = true };
        var project = new Project
        {
            Id = ProjectId.New(),
            Name = "Demo",
            Sequence = sequence,
            Loop = new TickRange(Tick.Zero, new Tick(1920)),
            Mixer = new Mixer { Channels = [bus with { Output = master.Id }, master], MasterGainDecibels = -1 },
        };
        project = TrackOutputs.Write(project, bass.Id, new TrackOutput
        {
            Profile = new ProfileReference("bluestone.generic.xg", "Generic XG"),
            Endpoint = new EndpointReference("coremidi", "-12345", "QY Out", "Maker", "Model"),
            Channel = MidiChannel.FromNumber(3),
            Transpose = -12,
            Voice = new VoiceAssignment("normal", ProgramNumber.FromNumber(34)),
        });
        var qy = project.Instruments[0].WithPort(new ExternalPort("B", "Port B")) with { OperatingMode = "XG" };
        project = project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(lead.Id)) with { Devices = [arp] }).WithChain(rack) with
        {
            Instruments = [qy],
        };
        return project with
        {
            Connections = project.Connections.AddRange(new SignalConnection[]
            {
                SignalConnection.Create(SignalKind.Events, SignalNode.Track(lead.Id), SignalNode.Rack(rack.Id)) with
                {
                    Mapping = new ChannelMapping { Only = [MidiChannel.FromNumber(2), MidiChannel.FromNumber(9)], Remap = [new ChannelRemap(MidiChannel.FromNumber(2), MidiChannel.FromNumber(16))] },
                },
                SignalConnection.Create(SignalKind.Events, SignalNode.Device(arp.Id), SignalNode.ExternalPart(qy.Id, "B")),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Rack(rack.Id), SignalNode.Mixer(bus.Id)),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(drums.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Events, SignalNode.Track(TrackId.New()), SignalNode.ExternalPart(qy.Id)),
            }),
            ParkedConnections = [SignalConnection.Create(SignalKind.Events, SignalNode.Track(lead.Id), SignalNode.ExternalPart(qy.Id, "B")) with { Mapping = ChannelMapping.ForceTo(MidiChannel.FromNumber(5)) }],
        };
    }

    /// <summary>A comparable description of a project, ignoring nothing that should persist.</summary>
    public static string Describe(Project project)
    {
        var s = project.Sequence;
        var lines = new List<string>
        {
            $"{project.Id} {project.Name} loop={project.Loop?.Start}-{project.Loop?.End} ppqn={s.Ppqn}",
            "tempo " + string.Join(",", s.TempoMap.Changes),
            "meter " + string.Join(",", s.MeterMap.Changes),
            "markers " + string.Join(",", s.Markers),
        };
        foreach (var track in s.Tracks)
        {
            lines.Add($"track {track.Id} {track.Name} m={track.IsMuted} s={track.IsSoloed} role={track.Role} group={track.Group} folded={track.IsFolded}");
            lines.AddRange(track.Automation.Select(l => $"  lane {l.Id} {l.Target} {string.Join(",", l.Points)}"));
            lines.AddRange(track.Clips.OfType<AudioClip>().Select(c => $"  audio {c.Id} {c.Start}+{c.Length} offset={c.ContentOffset} \"{c.Name}\" {c.Source}"));
            foreach (var clip in track.Clips.OfType<NoteClip>())
            {
                lines.Add($"  clip {clip.Id} {clip.Start}+{clip.Length} offset={clip.ContentOffset} \"{clip.Name}\"");
                lines.AddRange(clip.Content.Items.Select(e => e switch
                {
                    SysExEvent x => $"    {x.Id} {x.Position} sysex {Convert.ToHexString(x.Message.Bytes.Span)}",
                    RawMidiEvent x => $"    {x.Id} {x.Position} raw {Convert.ToHexString(x.Bytes.Span)}",
                    MetaEvent x => $"    {x.Id} {x.Position} meta {x.Type} {Convert.ToHexString(x.Data.Span)}",
                    _ => "    " + e,
                }));
            }
        }

        foreach (var instrument in project.Instruments)
        {
            lines.Add($"instrument {instrument.Id} {instrument.Name} {instrument.Profile} mode={instrument.OperatingMode}");
            lines.AddRange(instrument.Ports.Select(p => $"  port {p}"));
        }

        foreach (var chain in project.Chains)
        {
            lines.Add($"chain {chain.Id} {chain.Owner} \"{chain.Name}\"");
            lines.AddRange(chain.Devices.Select(d =>
                $"  device {d.Id} {d.Definition} \"{d.Name}\" bypassed={d.IsBypassed} {string.Join(",", d.Parameters)} state={d.State?.Format}:{(d.State is null ? string.Empty : Convert.ToHexString(d.State.Data.Span))}"));
        }

        lines.AddRange(project.Connections.Select(c => Describe("connection", c)));
        lines.AddRange(project.ParkedConnections.Select(c => Describe("parked", c)));
        lines.Add($"mixer master={project.Mixer.MasterGainDecibels}");
        lines.AddRange(project.Mixer.Channels.Select(c => $"  channel {c}"));
        return string.Join("\n", lines);
    }

    private static string Describe(string what, SignalConnection c) =>
        $"{what} {c.Id} {c.Kind} {c.Source} -> {c.Destination} only={string.Join(",", c.Mapping.Only)} force={c.Mapping.Force} remap={string.Join(",", c.Mapping.Remap)} voice={c.Voice}";
}
