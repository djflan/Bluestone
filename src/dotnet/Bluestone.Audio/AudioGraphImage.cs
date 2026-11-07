using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Bluestone.Audio.Clips;
using Bluestone.Audio.Plugins;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Audio;

/// <summary>
/// An <see cref="AudioGraphDescription"/> as one flat block of bytes, for handing the project's audio graph from the UI
/// process to the engine process through shared memory (ADR 0044), like the playback plan's image (PlanImage).
/// </summary>
/// <remarks>
/// <para>
/// Layout (version 3), native-endian, which is little-endian on every supported platform, every section 8-aligned:
/// a 128-byte header (magic "BSGR", version, each section's count and offset, and the master gain), then nodes, clip
/// regions, plugin events, plugin parameter changes, strips, steps, retained keys, strings, bytes (UTF-8 names and
/// paths, and SysEx), plugin links (each plugin instance's block exchange file, generation, and shape, for the
/// engine to attach to), and chain junction meter points with the node indices each sums. A plugin event is stored compactly, with its SysEx in the bytes section, not as the 272-byte
/// <see cref="PluginEvent"/>.
/// </para>
/// <para>
/// Unlike a plan, a description is decoded into objects when read: it holds names, paths, and per-plugin schedules,
/// which the realizer turns into nodes anyway, and a plugin's schedule is only replaced when its events change. Reading
/// checks every count, offset, index, and value and refuses a damaged or foreign image with
/// <see cref="InvalidDataException"/>; the steps are replayed through <see cref="AudioGraphDescriptionBuilder"/>, so a
/// description read back is one that realizes.
/// </para>
/// </remarks>
public static class AudioGraphImage
{
    public const int Version = 3;

    private const uint Magic = 0x52475342; // "BSGR"
    private const int HeaderSize = 128;
    private const int MasterGainAt = 120;

    private const int NodeSection = 0;
    private const int RegionSection = 1;
    private const int EventSection = 2;
    private const int ParameterSection = 3;
    private const int StripSection = 4;
    private const int StepSection = 5;
    private const int RetainedSection = 6;
    private const int StringSection = 7;
    private const int ByteSection = 8;
    private const int LinkSection = 9;
    private const int MeterPointSection = 10;
    private const int MeterNodeSection = 11;
    private const int SectionCount = 12;
    private static readonly long MaxCollectWaitTicks = TimeSpan.FromMinutes(1).Ticks;

    private static readonly int[] ElementSizes =
    [
        Unsafe.SizeOf<NodeEntry>(),
        Unsafe.SizeOf<RegionEntry>(),
        Unsafe.SizeOf<EventEntry>(),
        Unsafe.SizeOf<ParameterEntry>(),
        Unsafe.SizeOf<StripEntry>(),
        Unsafe.SizeOf<StepEntry>(),
        Unsafe.SizeOf<Guid>(),
        Unsafe.SizeOf<StringEntry>(),
        1,
        Unsafe.SizeOf<LinkEntry>(),
        Unsafe.SizeOf<MeterPointEntry>(),
        sizeof(int),
    ];

    /// <summary>Writes <paramref name="description"/> to a new array.</summary>
    public static byte[] ToArray(AudioGraphDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        var tables = new Tables(description);
        var image = new byte[tables.Total];
        tables.WriteTo(image);
        return image;
    }

    /// <summary>The number of bytes <see cref="Write"/> needs for <paramref name="description"/>.</summary>
    public static int Measure(AudioGraphDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        return new Tables(description).Total;
    }

    /// <summary>Writes <paramref name="description"/> to the start of <paramref name="destination"/>; returns the bytes written.</summary>
    public static int Write(AudioGraphDescription description, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(description);
        var tables = new Tables(description);
        if (destination.Length < tables.Total)
        {
            throw new ArgumentException($"The graph needs {tables.Total} bytes; the destination has {destination.Length}.", nameof(destination));
        }

        tables.WriteTo(destination[..tables.Total]);
        return tables.Total;
    }

    /// <summary>The description in <paramref name="image"/>.</summary>
    /// <exception cref="InvalidDataException">The image is damaged, or was written by another version or build.</exception>
    public static AudioGraphDescription Read(ReadOnlySpan<byte> image)
    {
        if (image.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(image) != Magic)
        {
            throw new InvalidDataException("This is not an audio graph image.");
        }

        var header = MemoryMarshal.Cast<byte, int>(image[4..MasterGainAt]);
        if (header[0] != Version)
        {
            throw new InvalidDataException($"The audio graph image is version {header[0]}; this build reads version {Version}.");
        }

        var counts = new int[SectionCount];
        var offsets = new int[SectionCount];
        for (var section = 0; section < SectionCount; section++)
        {
            counts[section] = header[1 + (section * 2)];
            offsets[section] = header[2 + (section * 2)];
            var size = (long)counts[section] * ElementSizes[section];
            if (counts[section] < 0 || offsets[section] < HeaderSize || offsets[section] % 8 != 0 || offsets[section] + size > image.Length)
            {
                throw new InvalidDataException("The audio graph image is damaged: a section lies outside it.");
            }
        }

        var masterGain = BinaryPrimitives.ReadDoubleLittleEndian(image[MasterGainAt..]);
        var nodes = Section<NodeEntry>(image, counts, offsets, NodeSection);
        var regions = Section<RegionEntry>(image, counts, offsets, RegionSection);
        var events = Section<EventEntry>(image, counts, offsets, EventSection);
        var parameters = Section<ParameterEntry>(image, counts, offsets, ParameterSection);
        var strips = Section<StripEntry>(image, counts, offsets, StripSection);
        var steps = Section<StepEntry>(image, counts, offsets, StepSection);
        var retained = Section<Guid>(image, counts, offsets, RetainedSection);
        var strings = Section<StringEntry>(image, counts, offsets, StringSection);
        var bytes = image.Slice(offsets[ByteSection], counts[ByteSection]);
        var links = Section<LinkEntry>(image, counts, offsets, LinkSection);
        var meterPoints = Section<MeterPointEntry>(image, counts, offsets, MeterPointSection);
        var meterNodes = Section<int>(image, counts, offsets, MeterNodeSection);

        try
        {
            var builder = new AudioGraphDescriptionBuilder();
            foreach (var strip in strips)
            {
                builder.AddStrip(new DescribedStrip(strip.Key, new MixerStripSettings(Finite(strip.GainDecibels), Finite(strip.Pan), strip.Muted != 0, strip.Soloed != 0)));
            }

            var described = new DescribedNode[nodes.Length];
            for (var i = 0; i < nodes.Length; i++)
            {
                described[i] = Node(nodes[i], regions, events, parameters, strings, bytes, links);
            }

            var added = 0;
            foreach (var step in steps)
            {
                switch ((DescribedStepKind)step.Kind)
                {
                    case DescribedStepKind.AddNode:
                        // Nodes are added once each, in order.
                        if (step.A != added || added >= described.Length)
                        {
                            throw new InvalidDataException("The audio graph image is damaged: its nodes are added out of order.");
                        }

                        builder.AddNode(described[added++]);
                        break;
                    case DescribedStepKind.Connect:
                        builder.Connect(step.A, step.B);
                        break;
                    case DescribedStepKind.ConnectSidechain:
                        builder.ConnectSidechain(step.A, step.B);
                        break;
                    case DescribedStepKind.Monitor:
                        builder.Monitor(step.A);
                        break;
                    case DescribedStepKind.ConnectToStrip:
                        builder.ConnectToStrip(step.A, step.B);
                        break;
                    case DescribedStepKind.ConnectToMaster:
                        builder.ConnectToMaster(step.A);
                        break;
                    case DescribedStepKind.RouteStrip:
                        builder.RouteStrip(step.A, step.B);
                        break;
                    case DescribedStepKind.SendStrip:
                        builder.SendStrip(step.A, step.B, Finite(step.Level), step.PreFader != 0);
                        break;
                    default:
                        throw new InvalidDataException($"The audio graph image is damaged: unknown step {step.Kind}.");
                }
            }

            if (added != described.Length)
            {
                throw new InvalidDataException("The audio graph image is damaged: some of its nodes are never added.");
            }

            foreach (var point in meterPoints)
            {
                builder.AddMeterPoint(point.Key, Range(meterNodes, point.FirstNode, point.NodeCount).ToArray());
            }

            return builder.Build(Finite(masterGain), retained.ToArray());
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"The audio graph image is damaged: {ex.Message}", ex);
        }
    }

    private static DescribedNode Node(in NodeEntry node, ReadOnlySpan<RegionEntry> regions, ReadOnlySpan<EventEntry> events, ReadOnlySpan<ParameterEntry> parameters, ReadOnlySpan<StringEntry> strings, ReadOnlySpan<byte> bytes, ReadOnlySpan<LinkEntry> links)
    {
        if (node.Link != -1 && ((DescribedNodeKind)node.Kind != DescribedNodeKind.Plugin || (uint)node.Link >= (uint)links.Length))
        {
            throw new InvalidDataException("The audio graph image is damaged: a node's plugin link is missing.");
        }

        var name = Text(node.Name, strings, bytes);
        switch ((DescribedNodeKind)node.Kind)
        {
            case DescribedNodeKind.Clips:
                // Counts are checked against their sections before anything is sized by them.
                var nodeRegions = Range(regions, node.FirstRegion, node.RegionCount);
                var decoded = ImmutableArray.CreateBuilder<AudioClipRegion>(nodeRegions.Length);
                foreach (var region in nodeRegions)
                {
                    decoded.Add(new AudioClipRegion(Text(region.Path, strings, bytes), region.SongStartFrame, region.SongEndFrame, Finite(region.SourceSecondsAtStart))
                    {
                        LoopStartSeconds = double.IsNaN(region.LoopStartSeconds) ? null : Finite(region.LoopStartSeconds),
                        LoopLengthSeconds = double.IsNaN(region.LoopLengthSeconds) ? null : Finite(region.LoopLengthSeconds),
                        Envelope = new AudioClipEnvelope(Finite(region.Gain), region.FadeInFrames, region.FadeOutFrames, region.EqualPower != 0),
                    });
                }

                return new DescribedNode(DescribedNodeKind.Clips, node.Key, name) { Regions = decoded.MoveToImmutable() };
            case DescribedNodeKind.Plugin:
                var nodeEvents = Range(events, node.FirstEvent, node.EventCount);
                var nodeParameters = Range(parameters, node.FirstParameter, node.ParameterCount);
                var scheduled = new List<ScheduledEvent>(nodeEvents.Length);
                foreach (var e in nodeEvents)
                {
                    scheduled.Add(new ScheduledEvent(e.Frame, Event(e, bytes)));
                }

                var changes = new List<ScheduledParameterChange>(nodeParameters.Length);
                foreach (var p in nodeParameters)
                {
                    changes.Add(new ScheduledParameterChange(p.Frame, p.Parameter, Finite(p.Value)));
                }

                return new DescribedNode(DescribedNodeKind.Plugin, node.Key, name)
                {
                    Schedule = scheduled.Count == 0 && changes.Count == 0 ? PluginSchedule.Empty : new PluginSchedule(scheduled, changes),
                    Link = node.Link == -1 ? null : Link(links[node.Link], strings, bytes),
                };
            case DescribedNodeKind.Gain:
                return new DescribedNode(DescribedNodeKind.Gain, node.Key, name) { Gain = Finite(node.Gain) };
            default:
                throw new InvalidDataException($"The audio graph image is damaged: unknown node kind {node.Kind}.");
        }
    }

    // The exchange's shape is checked by ExchangeOptions (an ArgumentException, so a damaged image).
    private static DescribedPluginLink Link(in LinkEntry link, ReadOnlySpan<StringEntry> strings, ReadOnlySpan<byte> bytes)
    {
        var shape = new ExchangeOptions(link.MaxFrames, link.InputChannels, link.OutputChannels, link.SlotCount, link.EventCapacity, link.ParameterCapacity, link.SidechainChannels);
        if (link.PipelineDepth < 1 || link.PipelineDepth > shape.SlotCount || link.CollectWaitTicks < 0 || link.CollectWaitTicks > MaxCollectWaitTicks
            || link.LatencyFrames < 0 || link.Bypass is not (0 or 1) || link.Path < -1)
        {
            throw new InvalidDataException("The audio graph image is damaged: a plugin link is out of range.");
        }

        PluginDataPlane? plane = null;
        if (link.Path != -1)
        {
            var path = Text(link.Path, strings, bytes);
            plane = path.Length > 0 && Path.IsPathRooted(path)
                ? new PluginDataPlane(path, link.Generation, shape)
                : throw new InvalidDataException("The audio graph image is damaged: a plugin's exchange is not a full path.");
        }

        return new DescribedPluginLink(shape, link.PipelineDepth, TimeSpan.FromTicks(link.CollectWaitTicks), link.Bypass == 1, link.LatencyFrames, plane);
    }

    private static PluginEvent Event(in EventEntry e, ReadOnlySpan<byte> bytes)
    {
        if ((PluginEventKind)e.Kind == PluginEventKind.SystemExclusive)
        {
            return PluginEvent.TryCreateSystemExclusive(e.SampleOffset, Slice(bytes, e.SysExOffset, e.SysExLength), out var sysEx)
                ? sysEx
                : throw new InvalidDataException("The audio graph image is damaged: a SysEx event is too long.");
        }

        return (PluginEventKind)e.Kind switch
        {
            PluginEventKind.NoteOff => PluginEvent.NoteOff(e.SampleOffset, e.Channel, e.Data1, e.Data2),
            PluginEventKind.NoteOn => PluginEvent.NoteOn(e.SampleOffset, e.Channel, e.Data1, e.Data2),
            PluginEventKind.PolyPressure => PluginEvent.PolyPressure(e.SampleOffset, e.Channel, e.Data1, e.Data2),
            PluginEventKind.ControlChange => PluginEvent.ControlChange(e.SampleOffset, e.Channel, e.Data1, e.Data2),
            PluginEventKind.ProgramChange => PluginEvent.ProgramChange(e.SampleOffset, e.Channel, e.Data1),
            PluginEventKind.ChannelPressure => PluginEvent.ChannelPressure(e.SampleOffset, e.Channel, e.Data1),
            PluginEventKind.PitchBend => PluginEvent.PitchBend(e.SampleOffset, e.Channel, e.Data1 | (e.Data2 << 7)),
            _ => throw new InvalidDataException($"The audio graph image is damaged: unknown plugin event kind {e.Kind}."),
        };
    }

    private static string Text(int index, ReadOnlySpan<StringEntry> strings, ReadOnlySpan<byte> bytes)
    {
        if ((uint)index >= (uint)strings.Length)
        {
            throw new InvalidDataException("The audio graph image is damaged: a name or path is missing.");
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Slice(bytes, strings[index].Offset, strings[index].Length));
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("The audio graph image is damaged: a name or path is not UTF-8.", ex);
        }
    }

    private static ReadOnlySpan<T> Range<T>(ReadOnlySpan<T> items, int first, int count)
    {
        if (first < 0 || count < 0 || (long)first + count > items.Length)
        {
            throw new InvalidDataException("The audio graph image is damaged: a node's items lie outside their section.");
        }

        return items.Slice(first, count);
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> bytes, int offset, int length) =>
        offset >= 0 && length >= 0 && (long)offset + length <= bytes.Length
            ? bytes.Slice(offset, length)
            : throw new InvalidDataException("The audio graph image is damaged: bytes lie outside their section.");

    private static double Finite(double value) =>
        double.IsFinite(value) ? value : throw new InvalidDataException("The audio graph image is damaged: a number is not finite.");

    private static float Finite(float value) =>
        float.IsFinite(value) ? value : throw new InvalidDataException("The audio graph image is damaged: a number is not finite.");

    private static ReadOnlySpan<T> Section<T>(ReadOnlySpan<byte> image, int[] counts, int[] offsets, int section)
        where T : struct =>
        MemoryMarshal.Cast<byte, T>(image.Slice(offsets[section], counts[section] * ElementSizes[section]));

    private static long Align(long position) => (position + 7) & ~7L;

    /// <summary>The description flattened into the image's tables, ready to lay out.</summary>
    private sealed class Tables
    {
        private readonly AudioGraphDescription _description;
        private readonly List<NodeEntry> _nodes = [];
        private readonly List<RegionEntry> _regions = [];
        private readonly List<EventEntry> _events = [];
        private readonly List<ParameterEntry> _parameters = [];
        private readonly List<StringEntry> _strings = [];
        private readonly List<LinkEntry> _links = [];
        private readonly List<MeterPointEntry> _meterPoints = [];
        private readonly List<int> _meterNodes = [];
        private readonly List<byte> _bytes = [];
        private readonly int[] _counts;
        private readonly int[] _offsets = new int[SectionCount];

        public Tables(AudioGraphDescription description)
        {
            _description = description;
            foreach (var node in description.Nodes)
            {
                var entry = new NodeEntry { Key = node.Key, Kind = (int)node.Kind, Name = String(node.Name), FirstRegion = _regions.Count, FirstEvent = _events.Count, FirstParameter = _parameters.Count, Link = -1, Gain = node.Gain };
                if (node.Link is { } link)
                {
                    entry.Link = _links.Count;
                    _links.Add(new LinkEntry
                    {
                        CollectWaitTicks = link.CollectWait.Ticks,
                        Path = link.DataPlane is { } plane ? String(plane.Path) : -1,
                        Generation = link.DataPlane?.Generation ?? 0,
                        MaxFrames = link.Shape.MaxFrames,
                        InputChannels = link.Shape.InputChannels,
                        OutputChannels = link.Shape.OutputChannels,
                        SlotCount = link.Shape.SlotCount,
                        EventCapacity = link.Shape.EventCapacity,
                        ParameterCapacity = link.Shape.ParameterChangeCapacity,
                        SidechainChannels = link.Shape.SidechainChannels,
                        PipelineDepth = link.PipelineDepth,
                        LatencyFrames = link.LatencyFrames,
                        Bypass = link.BypassOnFailure ? 1 : 0,
                    });
                }

                foreach (var region in node.Regions)
                {
                    _regions.Add(new RegionEntry
                    {
                        Path = String(region.Path),
                        SongStartFrame = region.SongStartFrame,
                        SongEndFrame = region.SongEndFrame,
                        SourceSecondsAtStart = region.SourceSecondsAtStart,
                        LoopStartSeconds = region.LoopStartSeconds ?? double.NaN,
                        LoopLengthSeconds = region.LoopLengthSeconds ?? double.NaN,
                        Gain = region.Envelope.Gain,
                        EqualPower = region.Envelope.EqualPower ? 1 : 0,
                        FadeInFrames = region.Envelope.FadeInFrames,
                        FadeOutFrames = region.Envelope.FadeOutFrames,
                    });
                }

                foreach (var scheduled in node.Schedule.Events)
                {
                    var e = scheduled.Event;
                    var sysEx = e.Kind == PluginEventKind.SystemExclusive ? Bytes(e.SystemExclusiveData) : (Offset: 0, Length: 0);
                    _events.Add(new EventEntry { Frame = scheduled.Frame, SampleOffset = e.SampleOffset, Kind = (byte)e.Kind, Channel = e.Channel, Data1 = e.Data1, Data2 = e.Data2, SysExOffset = sysEx.Offset, SysExLength = sysEx.Length });
                }

                foreach (var change in node.Schedule.Parameters)
                {
                    _parameters.Add(new ParameterEntry { Frame = change.Frame, Parameter = change.Parameter, Value = change.Value });
                }

                entry.RegionCount = _regions.Count - entry.FirstRegion;
                entry.EventCount = _events.Count - entry.FirstEvent;
                entry.ParameterCount = _parameters.Count - entry.FirstParameter;
                _nodes.Add(entry);
            }

            foreach (var point in description.MeterPoints.IsDefault ? [] : description.MeterPoints)
            {
                _meterPoints.Add(new MeterPointEntry { Key = point.Key, FirstNode = _meterNodes.Count, NodeCount = point.Nodes.Length });
                _meterNodes.AddRange(point.Nodes);
            }

            _counts =
            [
                _nodes.Count,
                _regions.Count,
                _events.Count,
                _parameters.Count,
                description.Strips.Length,
                description.Steps.Length,
                description.Retained.Count,
                _strings.Count,
                _bytes.Count,
                _links.Count,
                _meterPoints.Count,
                _meterNodes.Count,
            ];
            long position = HeaderSize;
            for (var section = 0; section < SectionCount; section++)
            {
                _offsets[section] = checked((int)position);
                position = Align(position + ((long)_counts[section] * ElementSizes[section]));
            }

            Total = checked((int)position);
        }

        public int Total { get; }

        public void WriteTo(Span<byte> image)
        {
            image.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(image, Magic);
            var header = MemoryMarshal.Cast<byte, int>(image[4..MasterGainAt]);
            header[0] = Version;
            for (var section = 0; section < SectionCount; section++)
            {
                header[1 + (section * 2)] = _counts[section];
                header[2 + (section * 2)] = _offsets[section];
            }

            BinaryPrimitives.WriteDoubleLittleEndian(image[MasterGainAt..], _description.MasterGainDecibels);
            CollectionsMarshal.AsSpan(_nodes).CopyTo(Section<NodeEntry>(image, NodeSection));
            CollectionsMarshal.AsSpan(_regions).CopyTo(Section<RegionEntry>(image, RegionSection));
            CollectionsMarshal.AsSpan(_events).CopyTo(Section<EventEntry>(image, EventSection));
            CollectionsMarshal.AsSpan(_parameters).CopyTo(Section<ParameterEntry>(image, ParameterSection));
            var strips = Section<StripEntry>(image, StripSection);
            for (var i = 0; i < strips.Length; i++)
            {
                var strip = _description.Strips[i];
                strips[i] = new StripEntry { Key = strip.Key, GainDecibels = strip.Settings.GainDecibels, Pan = strip.Settings.Pan, Muted = strip.Settings.Muted ? 1 : 0, Soloed = strip.Settings.Soloed ? 1 : 0 };
            }

            var steps = Section<StepEntry>(image, StepSection);
            for (var i = 0; i < steps.Length; i++)
            {
                var step = _description.Steps[i];
                steps[i] = new StepEntry { Kind = (int)step.Kind, A = step.A, B = step.B, Level = step.Level, PreFader = step.PreFader ? 1 : 0 };
            }

            // Retained keys in a fixed order, so the same description always writes the same bytes.
            var retained = Section<Guid>(image, RetainedSection);
            var ordered = _description.Retained.Order().ToArray();
            ordered.CopyTo(retained);
            CollectionsMarshal.AsSpan(_strings).CopyTo(Section<StringEntry>(image, StringSection));
            CollectionsMarshal.AsSpan(_bytes).CopyTo(Section<byte>(image, ByteSection));
            CollectionsMarshal.AsSpan(_links).CopyTo(Section<LinkEntry>(image, LinkSection));
            CollectionsMarshal.AsSpan(_meterPoints).CopyTo(Section<MeterPointEntry>(image, MeterPointSection));
            CollectionsMarshal.AsSpan(_meterNodes).CopyTo(Section<int>(image, MeterNodeSection));
        }

        private Span<T> Section<T>(Span<byte> image, int section)
            where T : struct =>
            MemoryMarshal.Cast<byte, T>(image.Slice(_offsets[section], _counts[section] * ElementSizes[section]));

        private int String(string text)
        {
            var (offset, length) = Bytes(Encoding.UTF8.GetBytes(text));
            _strings.Add(new StringEntry { Offset = offset, Length = length });
            return _strings.Count - 1;
        }

        private (int Offset, int Length) Bytes(ReadOnlySpan<byte> bytes)
        {
            var offset = _bytes.Count;
            _bytes.AddRange(bytes);
            return (offset, bytes.Length);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NodeEntry
    {
        public Guid Key;
        public int Kind;
        public int Name;
        public int FirstRegion;
        public int RegionCount;
        public int FirstEvent;
        public int EventCount;
        public int FirstParameter;
        public int ParameterCount;
        public int Link;
        public float Gain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RegionEntry
    {
        public long SongStartFrame;
        public long SongEndFrame;
        public double SourceSecondsAtStart;
        public double LoopStartSeconds;
        public double LoopLengthSeconds;
        public long FadeInFrames;
        public long FadeOutFrames;
        public int Path;
        public float Gain;
        public int EqualPower;
        public int Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventEntry
    {
        public long Frame;
        public int SampleOffset;
        public int SysExOffset;
        public int SysExLength;
        public byte Kind;
        public byte Channel;
        public byte Data1;
        public byte Data2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ParameterEntry
    {
        public long Frame;
        public double Value;
        public uint Parameter;
        public int Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StripEntry
    {
        public Guid Key;
        public double GainDecibels;
        public double Pan;
        public int Muted;
        public int Soloed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StepEntry
    {
        public double Level;
        public int Kind;
        public int A;
        public int B;
        public int PreFader;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MeterPointEntry
    {
        public Guid Key;
        public int FirstNode;
        public int NodeCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinkEntry
    {
        public long CollectWaitTicks;
        public int Path;
        public uint Generation;
        public int MaxFrames;
        public int InputChannels;
        public int OutputChannels;
        public int SlotCount;
        public int EventCapacity;
        public int ParameterCapacity;
        public int PipelineDepth;
        public int LatencyFrames;
        public int Bypass;
        public int SidechainChannels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StringEntry
    {
        public int Offset;
        public int Length;
    }
}
