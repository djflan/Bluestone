using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Wire;

namespace Bluestone.Midi.Files;

public sealed record SmfExportResult(SmfFile File, IReadOnlyList<SmfDiagnostic> Diagnostics);

/// <summary>
/// Converts a <see cref="Sequence"/> into a format 1 MIDI file: a conductor track carrying the title,
/// tempo map, meter map, and markers, followed by one file track per sequence track. Anything Bluestone
/// stores that a MIDI file cannot hold is reported.
/// </summary>
public static class SmfExporter
{
    /// <param name="sequence">The sequence to export.</param>
    /// <param name="title">The file's title, if any.</param>
    /// <param name="translate">
    /// Turns an external instrument parameter lane into the MIDI it sends (ADR 0031), or returns null when it
    /// cannot. Without it, every such lane is reported as not exported.
    /// </param>
    public static SmfExportResult Export(Sequence sequence, string? title = null, Func<AutomationLane, IReadOnlyList<TrackEvent>?>? translate = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var diagnostics = new DiagnosticBag();
        var chunks = new List<SmfChunk> { BuildConductor(sequence, title, diagnostics) };

        if (sequence.Tracks.Any(t => t.IsMuted || t.IsSoloed))
        {
            diagnostics.Info(SmfDiagnosticCodes.MixerStateNotStored, "Track mute and solo states cannot be stored in a MIDI file; all tracks were exported.");
        }

        for (var i = 0; i < sequence.Tracks.Length; i++)
        {
            chunks.Add(BuildTrack(sequence.Tracks[i], sequence.Ppqn, i + 1, diagnostics, translate));
        }

        var file = new SmfFile(SmfFormat.MultiTrack, SmfDivision.Metrical(sequence.Ppqn.TicksPerQuarterNote), [.. chunks]);
        return new SmfExportResult(file, diagnostics.ToList());
    }

    private static SmfTrack BuildConductor(Sequence sequence, string? title, DiagnosticBag diagnostics)
    {
        var events = new List<SmfEvent>();
        if (!string.IsNullOrEmpty(title))
        {
            events.Add(Text(0, SmfMetaType.TrackName, title, 0, diagnostics));
        }

        foreach (var change in sequence.MeterMap.Changes)
        {
            var signature = change.Signature;
            events.Add(new SmfMetaEvent(change.Position.Value, SmfMetaType.TimeSignature, ByteBlock.Copy([(byte)signature.Numerator, (byte)signature.DenominatorExponent, 24, 8])));
        }

        foreach (var change in sequence.TempoMap.Changes)
        {
            var us = change.Tempo.MicrosecondsPerQuarterNote;
            events.Add(new SmfMetaEvent(change.Position.Value, SmfMetaType.Tempo, ByteBlock.Copy([(byte)(us >> 16), (byte)(us >> 8), (byte)us])));
        }

        foreach (var marker in sequence.Markers)
        {
            events.Add(Text(marker.Position.Value, SmfMetaType.Marker, marker.Name, 0, diagnostics));
        }

        var ordered = events.OrderBy(e => e.Tick).ToList();
        return new SmfTrack(ordered, ordered.Count == 0 ? 0 : ordered[^1].Tick);
    }

    private static SmfTrack BuildTrack(Track track, Ppqn ppqn, int fileTrackIndex, DiagnosticBag diagnostics, Func<AutomationLane, IReadOnlyList<TrackEvent>?>? translate)
    {
        var scheduled = new List<(long Tick, EventPhase Phase, int Sequence, SmfEvent Event)>();
        if (track.Name.Length > 0)
        {
            scheduled.Add((0, EventPhase.Meta, -1, Text(0, SmfMetaType.TrackName, track.Name, fileTrackIndex, diagnostics)));
        }

        // Automation is written as the controller events it plays.
        var rendered = TrackRendering.Render(track, ppqn);
        if (rendered.SuppressedEvents > 0)
        {
            diagnostics.Info(SmfDiagnosticCodes.AutomationReplacedEvents, $"{rendered.SuppressedEvents} events in clips were left out because the track's automation replaces them.", fileTrackIndex);
        }

        var deviceLanes = track.Automation.Count(l => l.Target.IsDevice && !l.Points.IsEmpty);
        if (deviceLanes > 0)
        {
            diagnostics.Warn(SmfDiagnosticCodes.DeviceAutomationNotExported, string.Create(CultureInfo.InvariantCulture, $"{deviceLanes} automation lanes control device parameters, which a MIDI file cannot hold; they are not in the file."), fileTrackIndex);
        }

        // Instrument parameter automation is MIDI on the wire, so it is written as the MIDI it becomes, after
        // the track's own events at the same tick and phase. A MIDI file has no ports: it goes on the part's channel.
        var events = rendered.Events;
        var untranslated = 0;
        foreach (var lane in track.Automation.Where(l => l.Target.IsInstrument && !l.Points.IsEmpty))
        {
            if (translate?.Invoke(lane) is { } translated)
            {
                events = events.AddRange(translated);
            }
            else
            {
                untranslated++;
            }
        }

        if (untranslated > 0)
        {
            diagnostics.Warn(SmfDiagnosticCodes.DeviceAutomationNotExported, string.Create(CultureInfo.InvariantCulture, $"{untranslated} automation lanes control external instrument parameters that could not be translated to MIDI (the instrument, its profile, or the parameter is missing); they are not in the file."), fileTrackIndex);
        }
        for (var i = 0; i < events.Length; i++)
        {
            var e = events[i];
            var tick = e.Position.Value;
            switch (e)
            {
                case NoteEvent note:
                    scheduled.Add((tick, EventPhase.NoteOn, i, new SmfChannelEvent(tick, Midi1Encoder.NoteOn(note))));
                    scheduled.Add((note.EndPosition.Value, EventPhase.NoteOff, i, new SmfChannelEvent(note.EndPosition.Value, Midi1Encoder.NoteOff(note))));
                    break;
                case ChannelEvent channel:
                    foreach (var message in Midi1Encoder.Encode(channel))
                    {
                        scheduled.Add((tick, e.Phase, i, new SmfChannelEvent(tick, message)));
                    }

                    break;
                case SysExEvent sysEx:
                    scheduled.Add((tick, e.Phase, i, new SmfSysExEvent(tick, ByteBlock.Copy(sysEx.Message.Bytes.Span[1..]))));
                    break;
                case RawMidiEvent raw when raw.Bytes.Length > 0 && raw.Bytes[0] == SysExMessage.Start:
                    scheduled.Add((tick, e.Phase, i, new SmfSysExEvent(tick, ByteBlock.Copy(raw.Bytes.Span[1..]))));
                    break;
                case RawMidiEvent raw:
                    scheduled.Add((tick, e.Phase, i, new SmfEscapeEvent(tick, raw.Bytes)));
                    break;
                case MetaEvent meta:
                    scheduled.Add((tick, e.Phase, i, new SmfMetaEvent(tick, meta.Type, meta.Data)));
                    break;
                default:
                    throw new NotSupportedException($"Event type {e.GetType().Name} cannot be exported.");
            }
        }

        if (HasOverlappingNotes(events))
        {
            diagnostics.Warn(
                SmfDiagnosticCodes.OverlappingNotes,
                "Notes of the same pitch overlap on the same channel. A MIDI file cannot record which release belongs to which note, so their lengths may differ when the file is read back.",
                fileTrackIndex);
        }

        var ordered = scheduled.OrderBy(s => s.Tick).ThenBy(s => s.Phase).ThenBy(s => s.Sequence).Select(s => s.Event);
        // The track ends where its last event does, not at the end of its last clip.
        return new SmfTrack(ordered, events.IsEmpty ? 0 : events.Max(e => e.EndPosition.Value));
    }

    private static bool HasOverlappingNotes(ImmutableArray<TrackEvent> events)
    {
        var lastEnd = new Dictionary<(byte Channel, byte Note), long>();
        foreach (var note in events.OfType<NoteEvent>())
        {
            var key = (note.Channel.Index, note.Note.Value);
            if (lastEnd.TryGetValue(key, out var end) && note.Position.Value < end)
            {
                return true;
            }

            lastEnd[key] = Math.Max(end, note.EndPosition.Value);
        }

        return false;
    }

    private static SmfMetaEvent Text(long tick, byte type, string text, int fileTrackIndex, DiagnosticBag diagnostics)
    {
        if (!Ascii.IsValid(text))
        {
            diagnostics.Info(SmfDiagnosticCodes.TextEncodedAsUtf8, "Non-ASCII names were written as UTF-8; older devices may display them incorrectly.", fileTrackIndex);
        }

        return new SmfMetaEvent(tick, type, ByteBlock.Copy(SmfText.Encode(text)));
    }
}
