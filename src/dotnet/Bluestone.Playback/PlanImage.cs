using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Time;
using Bluestone.Midi.Wire;

namespace Bluestone.Playback;

/// <summary>
/// A playback plan as one flat block of bytes, for handing a plan from the UI process to the engine process through
/// shared memory (ADR 0044). The writer lays the plan out; the reader gets a <see cref="PlaybackPlan"/> whose events and
/// payload bytes are views of the image, not copies, so a plan of any size adds nothing to the reader's managed heap.
/// The small tables (tempo, meter, chased hardware parameters) are decoded on read.
/// </summary>
/// <remarks>
/// <para>
/// Layout (version 1), native-endian, which is little-endian on every supported platform, every section 8-aligned:
/// a 128-byte header (magic "BSPL", version, the size of one event, the resolution, and each section's count and
/// offset), then the tempo changes, meter changes, events (<see cref="PlanEvent"/> as laid out in memory), payload
/// ranges, payload bytes, chased parameter changes, and their messages. Diagnostics are not included: they are for the
/// UI.
/// </para>
/// <para>
/// Writer and reader must be the same build: the event layout is the runtime's, and a different event size or version
/// is refused. Reading checks every count, offset, and index against the image's length, so a damaged image is an
/// <see cref="InvalidDataException"/>, never an out-of-range read. The image must stay unchanged and alive while the plan
/// read from it is in use.
/// </para>
/// </remarks>
public static class PlanImage
{
    public const int Version = 1;

    private const uint Magic = 0x4C505342; // "BSPL"
    private const int HeaderSize = 128;

    /// <summary>The number of bytes <see cref="Write"/> needs for <paramref name="plan"/>.</summary>
    public static int Measure(PlaybackPlan plan) => Lay(plan).Total;

    /// <summary>Writes <paramref name="plan"/> to the start of <paramref name="destination"/>; returns the bytes written.</summary>
    public static int Write(PlaybackPlan plan, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var layout = Lay(plan);
        if (destination.Length < layout.Total)
        {
            throw new ArgumentException($"The plan needs {layout.Total} bytes; the destination has {destination.Length}.", nameof(destination));
        }

        var image = destination[..layout.Total];
        image.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(image, Magic);
        Header(image)[0] = Version;
        Header(image)[1] = Unsafe.SizeOf<PlanEvent>();
        Header(image)[2] = plan.TempoMap.Ppqn.TicksPerQuarterNote;
        for (var section = 0; section < SectionCount; section++)
        {
            Header(image)[3 + (section * 2)] = layout.Counts[section];
            Header(image)[4 + (section * 2)] = layout.Offsets[section];
        }

        var tempo = Section<TempoEntry>(image, layout, TempoSection);
        for (var i = 0; i < tempo.Length; i++)
        {
            var change = plan.TempoMap.Changes[i];
            tempo[i] = new TempoEntry(change.Position.Value, change.Tempo.MicrosecondsPerQuarterNote);
        }

        var meter = Section<MeterEntry>(image, layout, MeterSection);
        for (var i = 0; i < meter.Length; i++)
        {
            var change = plan.MeterMap.Changes[i];
            meter[i] = new MeterEntry(change.Position.Value, change.Signature.Numerator, change.Signature.Denominator);
        }

        plan.Events.CopyTo(Section<PlanEvent>(image, layout, EventSection));
        plan.PayloadRanges.CopyTo(Section<PayloadRange>(image, layout, RangeSection));
        var payloadBytes = Section<byte>(image, layout, PayloadSection);
        plan.PayloadBytes.CopyTo(payloadBytes);

        // A chased change's SysEx is appended after the plan's own payloads, with its own range.
        var ranges = Section<PayloadRange>(image, layout, RangeSection);
        var nextPayload = plan.PayloadCount;
        var nextByte = plan.PayloadBytes.Length;
        var chases = Section<ChaseEntry>(image, layout, ChaseSection);
        var messages = Section<ChaseMessageEntry>(image, layout, ChaseMessageSection);
        var nextMessage = 0;
        for (var i = 0; i < chases.Length; i++)
        {
            var change = plan.ParameterChases[i];
            chases[i] = new ChaseEntry(change.Tick, change.Key, nextMessage, change.Messages.Length);
            foreach (var message in change.Messages)
            {
                var payload = -1;
                if (message.Payload is { } bytes)
                {
                    bytes.Span.CopyTo(payloadBytes[nextByte..]);
                    ranges[nextPayload] = new PayloadRange(nextByte, bytes.Length);
                    payload = nextPayload++;
                    nextByte += bytes.Length;
                }

                messages[nextMessage++] = new ChaseMessageEntry(message.Slot, payload, message.Message.Status, message.Message.Data1, message.Message.Data2);
            }
        }

        return layout.Total;
    }

    /// <summary>Writes <paramref name="plan"/> to a new array.</summary>
    public static byte[] ToArray(PlaybackPlan plan)
    {
        var image = new byte[Measure(plan)];
        Write(plan, image);
        return image;
    }

    /// <summary>
    /// The plan in <paramref name="image"/>. Its events and payloads are views of <paramref name="image"/>, which must
    /// stay unchanged and alive while the plan is in use.
    /// </summary>
    /// <exception cref="InvalidDataException">The image is damaged, or was written by another version or build.</exception>
    public static PlaybackPlan Read(ReadOnlyMemory<byte> image)
    {
        var bytes = image.Span;
        if (bytes.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic)
        {
            throw new InvalidDataException("This is not a plan image.");
        }

        var header = MemoryMarshal.Cast<byte, int>(bytes[4..HeaderSize]);
        if (header[0] != Version)
        {
            throw new InvalidDataException($"The plan image is version {header[0]}; this build reads version {Version}.");
        }

        if (header[1] != Unsafe.SizeOf<PlanEvent>())
        {
            throw new InvalidDataException($"The plan image's events are {header[1]} bytes; this build's are {Unsafe.SizeOf<PlanEvent>()}. Writer and reader must be the same build.");
        }

        var counts = new int[SectionCount];
        var offsets = new int[SectionCount];
        for (var section = 0; section < SectionCount; section++)
        {
            counts[section] = header[3 + (section * 2)];
            offsets[section] = header[4 + (section * 2)];
            var size = (long)counts[section] * ElementSizes[section];
            if (counts[section] < 0 || offsets[section] < HeaderSize || offsets[section] % 8 != 0 || offsets[section] + size > bytes.Length)
            {
                throw new InvalidDataException("The plan image is damaged: a section lies outside it.");
            }
        }

        var layout = new Layout(counts, offsets, bytes.Length);
        var tempo = Section<TempoEntry>(bytes, layout, TempoSection);
        var meter = Section<MeterEntry>(bytes, layout, MeterSection);
        var events = Section<PlanEvent>(bytes, layout, EventSection);
        var ranges = Section<PayloadRange>(bytes, layout, RangeSection);
        var payloadByteCount = counts[PayloadSection];
        var chases = Section<ChaseEntry>(bytes, layout, ChaseSection);
        var messages = Section<ChaseMessageEntry>(bytes, layout, ChaseMessageSection);

        foreach (var range in ranges)
        {
            if (range.Offset < 0 || range.Length < 0 || (long)range.Offset + range.Length > payloadByteCount)
            {
                throw new InvalidDataException("The plan image is damaged: a payload lies outside its bytes.");
            }
        }

        long previousTick = long.MinValue;
        foreach (var e in events)
        {
            if (e.PayloadIndex >= ranges.Length || e.PayloadIndex < -1 || e.Slot < 0)
            {
                throw new InvalidDataException("The plan image is damaged: an event refers to a payload or output that does not exist.");
            }

            // The engine's search for a position needs ticks in order; what it sends must be valid MIDI.
            if (e.Tick < previousTick || e.DurationTicks < 0 || e.ReleaseVelocity.Value > 127
                || (e.PayloadIndex < 0 && !(ChannelMessage.TryCreate(e.Message.Status, e.Message.Data1, e.Message.Data2, out var valid) && valid == e.Message)))
            {
                throw new InvalidDataException("The plan image is damaged: an event is out of order or is not a valid MIDI message.");
            }

            previousTick = e.Tick;
        }

        try
        {
            var ppqn = new Ppqn(header[2]);
            var tempoMap = new TempoMap(ppqn, Decode(tempo, t => new TempoChange(new Tick(t.Tick), new Tempo(t.MicrosecondsPerQuarterNote))));
            var meterMap = new MeterMap(ppqn, Decode(meter, m => new MeterChange(new Tick(m.Tick), new TimeSignature(m.Numerator, m.Denominator))));
            var payloadMemory = image.Slice(offsets[PayloadSection], payloadByteCount);
            var parameterChases = new ParameterChase[chases.Length];
            for (var i = 0; i < chases.Length; i++)
            {
                var chase = chases[i];
                if (chase.FirstMessage < 0 || chase.MessageCount < 0 || (long)chase.FirstMessage + chase.MessageCount > messages.Length)
                {
                    throw new InvalidDataException("The plan image is damaged: a chased change's messages lie outside it.");
                }

                var decoded = new ChaseMessage[chase.MessageCount];
                for (var k = 0; k < decoded.Length; k++)
                {
                    var entry = messages[chase.FirstMessage + k];
                    if (entry.PayloadIndex >= ranges.Length || entry.Slot < 0)
                    {
                        throw new InvalidDataException("The plan image is damaged: a chased message refers to a payload or output that does not exist.");
                    }

                    if (entry.PayloadIndex >= 0)
                    {
                        var range = ranges[entry.PayloadIndex];
                        decoded[k] = new ChaseMessage(entry.Slot, default, ByteBlock.Copy(payloadMemory.Span.Slice(range.Offset, range.Length)));
                    }
                    else
                    {
                        decoded[k] = new ChaseMessage(entry.Slot, ChannelMessage.TryCreate(entry.Status, entry.Data1, entry.Data2, out var message)
                            ? message
                            : throw new InvalidDataException("The plan image is damaged: a chased message is not a channel message."));
                    }
                }

                parameterChases[i] = new ParameterChase(chase.Tick, chase.Key, decoded);
            }

            return new PlaybackPlan(
                tempoMap,
                meterMap,
                image.Slice(offsets[EventSection], counts[EventSection] * ElementSizes[EventSection]),
                image.Slice(offsets[RangeSection], counts[RangeSection] * ElementSizes[RangeSection]),
                payloadMemory,
                parameterChases);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"The plan image is damaged: {ex.Message}", ex);
        }
    }

    private const int TempoSection = 0;
    private const int MeterSection = 1;
    private const int EventSection = 2;
    private const int RangeSection = 3;
    private const int PayloadSection = 4;
    private const int ChaseSection = 5;
    private const int ChaseMessageSection = 6;
    private const int SectionCount = 7;

    private static readonly int[] ElementSizes =
    [
        Unsafe.SizeOf<TempoEntry>(),
        Unsafe.SizeOf<MeterEntry>(),
        Unsafe.SizeOf<PlanEvent>(),
        Unsafe.SizeOf<PayloadRange>(),
        1,
        Unsafe.SizeOf<ChaseEntry>(),
        Unsafe.SizeOf<ChaseMessageEntry>(),
    ];

    // Magic, then version, event size, resolution, and a count and an offset per section.
    private static Span<int> Header(Span<byte> image) => MemoryMarshal.Cast<byte, int>(image[4..HeaderSize]);

    private static Span<T> Section<T>(Span<byte> image, Layout layout, int section)
        where T : struct =>
        MemoryMarshal.Cast<byte, T>(image.Slice(layout.Offsets[section], layout.Counts[section] * ElementSizes[section]));

    private static ReadOnlySpan<T> Section<T>(ReadOnlySpan<byte> image, Layout layout, int section)
        where T : struct =>
        MemoryMarshal.Cast<byte, T>(image.Slice(layout.Offsets[section], layout.Counts[section] * ElementSizes[section]));

    private static List<TResult> Decode<T, TResult>(ReadOnlySpan<T> entries, Func<T, TResult> decode)
    {
        var result = new List<TResult>(entries.Length);
        foreach (var entry in entries)
        {
            result.Add(decode(entry));
        }

        return result;
    }

    private static Layout Lay(PlaybackPlan plan)
    {
        var chasedPayloads = plan.ParameterChases.SelectMany(c => c.Messages).Where(m => m.Payload is not null).ToList();
        int[] counts =
        [
            plan.TempoMap.Changes.Length,
            plan.MeterMap.Changes.Length,
            plan.Events.Length,
            plan.PayloadCount + chasedPayloads.Count,
            plan.PayloadBytes.Length + chasedPayloads.Sum(m => m.Payload!.Length),
            plan.ParameterChases.Length,
            plan.ParameterChases.Sum(c => c.Messages.Length),
        ];
        var offsets = new int[SectionCount];
        long position = HeaderSize;
        for (var section = 0; section < SectionCount; section++)
        {
            offsets[section] = checked((int)position);
            position = Align(position + ((long)counts[section] * ElementSizes[section]));
        }

        return new Layout(counts, offsets, checked((int)position));
    }

    private static long Align(long position) => (position + 7) & ~7L;

    private sealed record Layout(int[] Counts, int[] Offsets, int Total);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TempoEntry(long Tick, int MicrosecondsPerQuarterNote);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct MeterEntry(long Tick, int Numerator, int Denominator);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ChaseEntry(long Tick, int Key, int FirstMessage, int MessageCount);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ChaseMessageEntry(int Slot, int PayloadIndex, byte Status, byte Data1, byte Data2);
}
