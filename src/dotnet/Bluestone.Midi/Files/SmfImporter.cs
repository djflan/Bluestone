using System.Globalization;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Wire;

namespace Bluestone.Midi.Files;

/// <summary>The imported sequence, the file's title if it had one, and everything that was repaired or not carried over.</summary>
public sealed record SmfImportResult(Sequence Sequence, string? Title, IReadOnlyList<SmfDiagnostic> Diagnostics);

/// <summary>
/// Converts a parsed MIDI file into a Bluestone <see cref="Sequence"/>. Tempo, meter, markers, and track
/// names become first-class model data; notes are paired; everything else Bluestone does not model is
/// preserved as raw events so it can be exported again. Nothing is silently discarded.
/// </summary>
public static class SmfImporter
{
    private const int StandardClocksPerClick = 24;
    private const int StandardThirtySecondsPerQuarter = 8;

    public static SmfImportResult Import(SmfFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var diagnostics = new DiagnosticBag();
        var (ppqn, smpteTempo) = ResolveTiming(file.Division, diagnostics);

        if (file.Format == SmfFormat.MultiSequence)
        {
            diagnostics.Warn(SmfDiagnosticCodes.MultiSequenceFlattened, "Format 2 patterns were imported as simultaneous tracks.");
        }

        var tempoChanges = new List<TempoChange>();
        if (smpteTempo is { } fixedTempo)
        {
            tempoChanges.Add(new TempoChange(Tick.Zero, fixedTempo));
        }

        var meterChanges = new List<(MeterChange Change, SmfMetaEvent Source, int TrackIndex, int EventIndex)>();
        var markers = new List<Marker>();
        var drafts = new List<TrackDraft>();
        string? title = null;

        var trackIndex = 0;
        foreach (var smfTrack in file.Tracks)
        {
            var draft = new TrackDraft(trackIndex);
            var pairing = new NotePairing(draft, diagnostics, trackIndex);
            for (var i = 0; i < smfTrack.Events.Length; i++)
            {
                var e = smfTrack.Events[i];
                var tick = new Tick(e.Tick);
                switch (e)
                {
                    case SmfChannelEvent { Message: var message } when message.IsNoteOn:
                        pairing.On(tick, message, i);
                        break;
                    case SmfChannelEvent { Message: var message } when message.IsNoteOff:
                        pairing.Off(tick, message, i);
                        break;
                    case SmfChannelEvent channel:
                        draft.Add(Midi1Decoder.Decode(tick, channel.Message)!, i);
                        break;
                    case SmfSysExEvent sysEx:
                        draft.Add(ImportSysEx(tick, sysEx, diagnostics, trackIndex), i);
                        break;
                    case SmfEscapeEvent escape:
                        diagnostics.Info(SmfDiagnosticCodes.SysExKeptRaw, "Escaped or continued SysEx bytes were kept verbatim.", trackIndex);
                        draft.Add(new RawMidiEvent(EventId.New(), tick, escape.Data), i);
                        break;
                    case SmfMetaEvent meta:
                        ImportMeta(meta, draft, i);
                        break;
                }
            }

            pairing.Finish(new Tick(smfTrack.EndTick));
            drafts.Add(draft);
            trackIndex++;
        }

        var meterMap = BuildMeterMap(ppqn, meterChanges, drafts, diagnostics);
        var tempoMap = new TempoMap(ppqn, tempoChanges);

        var tracks = new List<Track>();
        foreach (var draft in drafts)
        {
            var isConductor = draft.Index == 0 && file.Format == SmfFormat.MultiTrack;
            if (isConductor && draft.IsEmpty)
            {
                title = draft.Name;
                continue;
            }

            if (draft.Index == 0)
            {
                title = draft.Name;
            }

            if (file.Format == SmfFormat.SingleTrack)
            {
                tracks.AddRange(SplitChannels(draft, meterMap));
            }
            else
            {
                tracks.Add(ImportedTrack(draft.Name ?? string.Empty, [.. draft.Build()], meterMap));
            }
        }

        var sequence = new Sequence(tempoMap, meterMap, tracks, markers);
        return new SmfImportResult(sequence, title, diagnostics.ToList());

        void ImportMeta(SmfMetaEvent meta, TrackDraft draft, int eventIndex)
        {
            var tick = new Tick(meta.Tick);
            var isConductorTrack = draft.Index == 0;
            switch (meta.Type)
            {
                case SmfMetaType.Tempo when meta.Data.Length == 3 && smpteTempo is not null:
                    diagnostics.Warn(SmfDiagnosticCodes.TempoIgnoredForSmpte, "Tempo events have no effect in an SMPTE-timed file and were dropped.", draft.Index);
                    return;
                case SmfMetaType.Tempo when meta.Data.Length == 3:
                    var microseconds = (meta.Data[0] << 16) | (meta.Data[1] << 8) | meta.Data[2];
                    if (microseconds == 0)
                    {
                        break;
                    }

                    tempoChanges.Add(new TempoChange(tick, new Tempo(microseconds)));
                    NoteConductorPlacement(isConductorTrack, draft.Index);
                    return;
                case SmfMetaType.TimeSignature when meta.Data.Length == 4 && meta.Data[0] > 0 && meta.Data[1] <= 7:
                    meterChanges.Add((new MeterChange(tick, TimeSignature.FromExponent(meta.Data[0], meta.Data[1])), meta, draft.Index, eventIndex));
                    if (meta.Data[2] != StandardClocksPerClick || meta.Data[3] != StandardThirtySecondsPerQuarter)
                    {
                        diagnostics.Info(SmfDiagnosticCodes.MetronomeSettingsDropped, "Non-standard metronome click settings in a time signature were not kept.", draft.Index);
                    }

                    NoteConductorPlacement(isConductorTrack, draft.Index);
                    return;
                case SmfMetaType.Marker:
                    markers.Add(new Marker(tick, SmfText.Decode(meta.Data.Span)));
                    NoteConductorPlacement(isConductorTrack, draft.Index);
                    return;
                case SmfMetaType.TrackName when meta.Tick == 0 && draft.Name is null:
                    draft.Name = DecodeName(meta, draft.Index);
                    return;
                case SmfMetaType.Port:
                    diagnostics.Info(SmfDiagnosticCodes.PortAssignmentNotApplied, "A MIDI port assignment was kept but does not change routing; bind the track to an endpoint instead.", draft.Index);
                    break;
            }

            if (meta.Type is SmfMetaType.Tempo or SmfMetaType.TimeSignature)
            {
                diagnostics.Warn(
                    meta.Type == SmfMetaType.Tempo ? SmfDiagnosticCodes.InvalidTempo : SmfDiagnosticCodes.InvalidTimeSignature,
                    "A malformed tempo or time signature event was kept as raw data and not applied.",
                    draft.Index);
            }

            draft.Add(new MetaEvent(EventId.New(), tick, meta.Type, meta.Data), eventIndex);
        }

        void NoteConductorPlacement(bool isConductorTrack, int index)
        {
            if (!isConductorTrack && file.Format == SmfFormat.MultiTrack)
            {
                diagnostics.Info(SmfDiagnosticCodes.ConductorEventOutsideFirstTrack, "Tempo, meter, or marker events outside the first track were applied to the whole sequence.", index);
            }
        }

        string DecodeName(SmfMetaEvent meta, int index)
        {
            var name = SmfText.Decode(meta.Data.Span);
            if (name.Length <= Track.MaxNameLength)
            {
                return name;
            }

            diagnostics.Warn(SmfDiagnosticCodes.NameTruncated, string.Create(CultureInfo.InvariantCulture, $"A track name was shortened to {Track.MaxNameLength} characters."), index);
            return name[..Track.MaxNameLength];
        }
    }

    // Each file track becomes one clip from the bar of its first event to the bar line after its last.
    private static Track ImportedTrack(string name, IReadOnlyCollection<TrackEvent> events, MeterMap meter) =>
        new(TrackId.New(), name, NoteClip.Enclosing(events, meter) is { } clip ? [clip] : []);

    private static IEnumerable<Track> SplitChannels(TrackDraft draft, MeterMap meter)
    {
        var global = new List<TrackEvent>();
        var channels = new SortedDictionary<byte, List<TrackEvent>>();
        foreach (var e in draft.Build())
        {
            MidiChannel? channel = e is ChannelEvent c ? c.Channel : null;
            if (channel is not { } midiChannel)
            {
                global.Add(e);
                continue;
            }

            if (!channels.TryGetValue(midiChannel.Index, out var events))
            {
                events = [];
                channels.Add(midiChannel.Index, events);
            }

            events.Add(e);
        }

        if (channels.Count == 0)
        {
            yield return ImportedTrack(draft.Name ?? string.Empty, global, meter);
            yield break;
        }

        if (global.Count > 0)
        {
            yield return ImportedTrack("MIDI Setup", global, meter);
        }

        foreach (var (index, events) in channels)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"Channel {index + 1}");
            yield return ImportedTrack(name, events, meter);
        }
    }

    private static (Ppqn Ppqn, Tempo? FixedTempo) ResolveTiming(SmfDivision division, DiagnosticBag diagnostics)
    {
        if (!division.IsSmpte)
        {
            return (new Ppqn(division.TicksPerQuarterNote), null);
        }

        // Keep one file tick per sequence tick and pick a constant tempo that reproduces SMPTE time.
        var nominalFps = division.FramesPerSecond == 29 ? 30 : division.FramesPerSecond;
        var ticksPerSecond = nominalFps * division.TicksPerFrame;
        var ppqn = ticksPerSecond % 2 == 0 ? ticksPerSecond / 2 : ticksPerSecond;
        var actualTicksPerSecond = division.FramesPerSecond == 29
            ? division.TicksPerFrame * 30_000m / 1001m
            : ticksPerSecond;
        var microseconds = (int)decimal.Round(ppqn * 1_000_000m / actualTicksPerSecond, MidpointRounding.AwayFromZero);

        diagnostics.Info(
            SmfDiagnosticCodes.SmpteTimingConverted,
            string.Create(CultureInfo.InvariantCulture, $"SMPTE timing ({division.FramesPerSecond} fps × {division.TicksPerFrame}) was converted to {ppqn} PPQN at a constant {microseconds} µs per quarter note."));
        return (new Ppqn(ppqn), new Tempo(microseconds));
    }

    private static TrackEvent ImportSysEx(Tick tick, SmfSysExEvent sysEx, DiagnosticBag diagnostics, int trackIndex)
    {
        var bytes = new byte[sysEx.Data.Length + 1];
        bytes[0] = SysExMessage.Start;
        sysEx.Data.Span.CopyTo(bytes.AsSpan(1));

        if (SysExMessage.TryCreate(bytes, out var message, out _))
        {
            return new SysExEvent(tick, message);
        }

        diagnostics.Info(SmfDiagnosticCodes.SysExKeptRaw, "A SysEx packet that is split across events or not well formed was kept verbatim.", trackIndex);
        return new RawMidiEvent(EventId.New(), tick, ByteBlock.Copy(bytes));
    }

    private static MeterMap BuildMeterMap(
        Ppqn ppqn,
        List<(MeterChange Change, SmfMetaEvent Source, int TrackIndex, int EventIndex)> changes,
        List<TrackDraft> drafts,
        DiagnosticBag diagnostics)
    {
        var usable = new List<MeterChange>();
        foreach (var (change, source, trackIndex, eventIndex) in changes)
        {
            if (change.Signature.TryGetTicksPerBeat(ppqn, out _))
            {
                usable.Add(change);
                continue;
            }

            diagnostics.Warn(
                SmfDiagnosticCodes.InvalidTimeSignature,
                string.Create(CultureInfo.InvariantCulture, $"Time signature {change.Signature} cannot be represented at {ppqn} PPQN; it was kept as raw data and not applied."),
                trackIndex);
            drafts[trackIndex].Add(new MetaEvent(EventId.New(), change.Position, source.Type, source.Data), eventIndex);
        }

        return new MeterMap(ppqn, usable);
    }

    /// <summary>
    /// Accumulates a track's events keyed by their index in the file, leaving slots for notes until they
    /// are paired, so the built track preserves file order among simultaneous events.
    /// </summary>
    private sealed class TrackDraft(int index)
    {
        private readonly List<(int Order, TrackEvent? Event)> _events = [];

        public int Index { get; } = index;

        public string? Name { get; set; }

        public bool IsEmpty => _events.Count == 0;

        public void Add(TrackEvent trackEvent, int fileIndex) => _events.Add((fileIndex, trackEvent));

        public int Reserve(int fileIndex)
        {
            _events.Add((fileIndex, null));
            return _events.Count - 1;
        }

        public void Fill(int slot, TrackEvent trackEvent) => _events[slot] = (_events[slot].Order, trackEvent);

        public IEnumerable<TrackEvent> Build() =>
            Midi1Decoder.CombineProgramSelections(_events.Select((e, position) => (e.Order, position, e.Event))
                .OrderBy(e => e.Order).ThenBy(e => e.position)
                .Select(e => e.Event!));
    }

    /// <summary>Pairs note-ons with note-offs first-in, first-out per channel and note.</summary>
    private sealed class NotePairing(TrackDraft draft, DiagnosticBag diagnostics, int trackIndex)
    {
        private readonly Dictionary<(byte Channel, byte Note), Queue<(Tick Start, ChannelMessage On, int Slot)>> _open = [];

        public void On(Tick tick, ChannelMessage message, int fileIndex)
        {
            var key = (message.Channel.Index, message.Data1);
            if (!_open.TryGetValue(key, out var queue))
            {
                queue = new Queue<(Tick, ChannelMessage, int)>();
                _open[key] = queue;
            }

            queue.Enqueue((tick, message, draft.Reserve(fileIndex)));
        }

        public void Off(Tick tick, ChannelMessage message, int fileIndex)
        {
            if (!_open.TryGetValue((message.Channel.Index, message.Data1), out var queue) || queue.Count == 0)
            {
                diagnostics.Info(SmfDiagnosticCodes.UnpairedNoteOff, "A note-off with no matching note-on was kept as a separate release.", trackIndex);
                draft.Add(Midi1Decoder.Decode(tick, message)!, fileIndex);
                return;
            }

            var (start, on, slot) = queue.Dequeue();
            var release = message.Kind == ChannelMessageKind.NoteOff ? message.Velocity : Velocity.Off;
            draft.Fill(slot, CreateNote(start, tick - start, on, release));
        }

        public void Finish(Tick trackEnd)
        {
            foreach (var queue in _open.Values)
            {
                while (queue.TryDequeue(out var open))
                {
                    diagnostics.Warn(SmfDiagnosticCodes.UnterminatedNote, "A note with no release was ended at the end of its track.", trackIndex);
                    var length = trackEnd > open.Start ? trackEnd - open.Start : TickSpan.Zero;
                    draft.Fill(open.Slot, CreateNote(open.Start, length, open.On, Velocity.DefaultRelease));
                }
            }
        }

        private NoteEvent CreateNote(Tick start, TickSpan length, ChannelMessage on, Velocity release)
        {
            if (length == TickSpan.Zero)
            {
                diagnostics.Warn(SmfDiagnosticCodes.ZeroLengthNote, "A note that ends where it starts was lengthened to one tick.", trackIndex);
                length = new TickSpan(1);
            }

            return new NoteEvent(EventId.New(), start, length, on.Channel, on.Note, on.Velocity, release);
        }
    }
}
