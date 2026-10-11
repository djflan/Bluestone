using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bluestone.Application.Routing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Files;
using Bluestone.Midi.Wire;
using Bluestone.Playback;
using Bluestone.Profiles;
using Bluestone.Signal;

namespace Bluestone.Tests.Unit.Playback;

/// <summary>
/// Comparable, hashable descriptions of what a MIDI 1.0 device receives from a sequence, for playback and
/// for SMF export. Each output is listed per slot (or file track) and channel, in dispatch order with
/// ticks, because order between different channels at the same tick does not reach any one instrument.
/// </summary>
internal static class PlanDump
{
    public static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
        }
    }

    public static string SamplesDirectory => Path.Combine(RepositoryRoot, "samples");

    /// <summary>
    /// <paramref name="project"/> with each track sending to an external instrument on a test
    /// endpoint: one per track (slot = track index), or all on one when <paramref name="shared"/>.
    /// <paramref name="output"/> adds channel, transposition, and voice settings per track.
    /// </summary>
    public static Project Routed(Project project, bool shared = false, Func<Track, TrackOutput>? output = null) =>
        project.Sequence.Tracks.Select((t, i) => (Track: t, Index: i)).Aggregate(project, (current, x) =>
            TrackOutputs.Write(current, x.Track.Id, (output?.Invoke(x.Track) ?? TrackOutput.None) with { Endpoint = SlotReference(shared ? 0 : x.Index) }));

    public static Project Routed(Sequence sequence, bool shared = false) => Routed(Project.CreateNew("dump") with { Sequence = sequence }, shared);

    public static EndpointReference SlotReference(int slot) => new("test", $"slot-{slot}", $"Slot {slot}");

    public static IReadOnlyList<EndpointDescriptor> SlotEndpoints(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new EndpointDescriptor(new EndpointId("test", $"slot-{i}"), $"Slot {i}", EndpointDirection.Output, EndpointTransport.Test, EndpointCapabilities.SystemExclusive))];

    /// <summary>The plan the controller would prepare for <paramref name="project"/>, with every test endpoint present.</summary>
    public static PlaybackPlan Compile(Project project, ProfileCatalog? profiles = null) =>
        PlaybackRouting.Prepare(project, DeviceCatalog.BuiltIn, profiles ?? ProfileCatalog.Empty, SlotEndpoints(Math.Max(1, project.Sequence.Tracks.Length))).Compile(project.Sequence);

    // Note releases are sent by the engine at the end of each note.
    public static IEnumerable<string> PlaybackStream(Sequence sequence) => PlaybackStream(Compile(Routed(sequence)));

    public static IEnumerable<string> PlaybackStream(PlaybackPlan plan)
    {
        var sent = new List<(int Slot, string Channel, long Tick, int Order, string Bytes)>();
        for (var i = 0; i < plan.Events.Length; i++)
        {
            var e = plan.Events[i];
            if (e.PayloadIndex >= 0)
            {
                sent.Add((e.Slot, "sys", e.Tick, i, Convert.ToHexString(plan.Payloads[e.PayloadIndex].Span)));
                continue;
            }

            sent.Add((e.Slot, e.Message.Channel.Number.ToString("00", CultureInfo.InvariantCulture), e.Tick, i, Hex(e.Message)));
            if (e.IsNote)
            {
                // The engine sends releases before anything else due at the same time.
                sent.Add((e.Slot, e.Message.Channel.Number.ToString("00", CultureInfo.InvariantCulture), e.Tick + e.DurationTicks, -1, Hex(ChannelMessage.NoteOff(e.Message.Channel, e.Message.Note, e.ReleaseVelocity))));
            }
        }

        return sent.OrderBy(s => s.Slot).ThenBy(s => s.Channel, StringComparer.Ordinal).ThenBy(s => s.Tick).ThenBy(s => s.Order)
            .Select(s => $"{s.Slot} {s.Channel} {s.Tick} {s.Bytes}");
    }

    public static IEnumerable<string> ExportStream(Sequence sequence)
    {
        var exported = SmfReader.Read(SmfWriter.Write(SmfExporter.Export(sequence).File)).File;
        return exported.Tracks.SelectMany((track, t) => track.Events.Select((e, i) => (t, e, i)))
            .Select(x => x.e switch
            {
                SmfChannelEvent c => (x.t, Channel: c.Message.Channel.Number.ToString("00", CultureInfo.InvariantCulture), x.e.Tick, x.i, Hex(c.Message)),
                SmfSysExEvent s => (x.t, Channel: "sys", x.e.Tick, x.i, "F0" + Convert.ToHexString(s.Data.Span)),
                SmfEscapeEvent s => (x.t, Channel: "sys", x.e.Tick, x.i, "F7" + Convert.ToHexString(s.Data.Span)),
                SmfMetaEvent m => (x.t, Channel: "meta", x.e.Tick, x.i, $"FF{m.Type:X2}" + Convert.ToHexString(m.Data.Span)),
                _ => (x.t, Channel: "?", x.e.Tick, x.i, string.Empty),
            })
            .OrderBy(s => s.t).ThenBy(s => s.Channel, StringComparer.Ordinal).ThenBy(s => s.Tick).ThenBy(s => s.i)
            .Select(s => $"{s.t} {s.Channel} {s.Tick} {s.Item5}");
    }

    public static string Hash(IEnumerable<string> lines) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))))[..16];

    private static string Hex(ChannelMessage message)
    {
        Span<byte> bytes = stackalloc byte[3];
        return Convert.ToHexString(bytes[..message.CopyTo(bytes)]);
    }
}
