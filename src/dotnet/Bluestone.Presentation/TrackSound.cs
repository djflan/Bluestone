using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Presentation;

/// <summary>
/// What a track sounds through, read from the project's one routing model: the instrument in its chain, where its audio
/// and notes go, and what it holds. The track header and the inspector describe a track with this before any MIDI detail,
/// so a track played by a plugin reads as that plugin, not as "No output".
/// </summary>
public sealed record TrackSound
{
    public static TrackSound Empty { get; } = new();

    /// <summary>The first instrument in the track's chain, by its display name.</summary>
    public string? Instrument { get; init; }

    /// <summary>The device that is <see cref="Instrument"/>.</summary>
    public DeviceId? InstrumentDevice { get; init; }

    /// <summary>Every instrument in the chain, in order: a chain may layer several (ADR 0037).</summary>
    public ImmutableArray<DeviceId> InstrumentDevices { get; init; } = [];

    /// <summary>Every instrument is bypassed, so the chain passes notes on but makes no sound of its own.</summary>
    public bool IsInstrumentBypassed { get; init; }

    /// <summary>An instrument is a plugin that is not installed, crashed, or stopped responding (set from runtime status).</summary>
    public bool IsInstrumentUnavailable { get; init; }

    /// <summary>"Surge XT", or "Surge XT +1" when the chain layers more instruments.</summary>
    public string? InstrumentSummary => Instrument is not { } name ? null
        : InstrumentDevices.Length > 1 ? $"{name} +{InstrumentDevices.Length - 1}"
        : name;

    /// <summary>
    /// The first device in the chain whose definition is not installed. Its kind is not stored in the project, so it may be
    /// what played the track: the track is flagged rather than reported as having no instrument.
    /// </summary>
    public string? MissingDevice { get; init; }

    /// <summary>How many devices the track's chain holds.</summary>
    public int DeviceCount { get; init; }

    public bool HoldsNotes { get; init; }

    public bool HoldsAudio { get; init; }

    /// <summary>Where the track's output audio goes ("Master", a mixer channel's or a track's name), or null.</summary>
    public string? AudioDestination { get; init; }

    /// <summary>The destination node of <see cref="AudioDestination"/>: the track's primary audio output.</summary>
    public SignalNode? AudioNode { get; init; }

    /// <summary>Further audio connections from the track's output (sends), beyond the primary one.</summary>
    public int AudioSendCount { get; init; }

    /// <summary>Where the track's notes go other than to hardware (a rack or another track), or null.</summary>
    public string? NoteDestination { get; init; }

    /// <summary>"3 devices", "1 device", or "No devices".</summary>
    public string DevicesText => DeviceCount switch
    {
        0 => "No devices",
        1 => "1 device",
        _ => $"{DeviceCount} devices",
    };

    public static TrackSound Of(Project project, TrackId track, DeviceDefinitionLookup definitions)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(definitions);
        if (project.Sequence.FindTrack(track) is not { } found)
        {
            return Empty;
        }

        var chain = project.ChainOf(track);
        var instruments = chain?.Devices.Where(d => definitions(d.Definition.Id)?.Category == DeviceCategory.Instrument).ToImmutableArray() ?? [];
        var instrument = instruments.FirstOrDefault();
        var output = SignalNode.Track(track);
        var audioOutputs = project.Connections.Where(c => c.Kind == SignalKind.Audio && c.Source == output).ToList();
        var audio = audioOutputs.FirstOrDefault();
        var notes = project.Connections.FirstOrDefault(c => c.Kind == SignalKind.Events && c.Source == output && c.Destination.Kind != SignalNodeKind.ExternalInstrument);
        var missing = chain?.Devices.FirstOrDefault(d => definitions(d.Definition.Id) is null);
        return new TrackSound
        {
            MissingDevice = missing?.DisplayName,
            Instrument = instrument?.DisplayName,
            InstrumentDevice = instrument?.Id,
            InstrumentDevices = [.. instruments.Select(d => d.Id)],
            IsInstrumentBypassed = instruments.Length > 0 && instruments.All(d => d.IsBypassed),
            DeviceCount = chain?.Devices.Length ?? 0,
            HoldsNotes = found.Clips.Any(c => c is NoteClip),
            HoldsAudio = found.Clips.Any(c => c is AudioClip),
            AudioDestination = audio is null ? null : NameOf(project, audio.Destination),
            AudioNode = audio?.Destination,
            AudioSendCount = Math.Max(0, audioOutputs.Count - 1),
            NoteDestination = notes is null ? null : NameOf(project, notes.Destination),
        };
    }

    /// <summary>A destination's short name, as the track header and inspector show it.</summary>
    public static string NameOf(Project project, SignalNode node)
    {
        ArgumentNullException.ThrowIfNull(project);
        return node.Kind switch
        {
            SignalNodeKind.Master => "Master",
            SignalNodeKind.MixerChannel => project.Mixer.Find(node.AsMixerChannel())?.Name ?? "Missing channel",
            SignalNodeKind.Track => project.Sequence.FindTrack(node.AsTrack())?.Name ?? "Missing track",
            SignalNodeKind.Rack => project.FindChain(node.AsRack()) is { } rack ? DeviceViewViewModel.RackName(rack) : "Missing rack",
            SignalNodeKind.Device => project.FindDevice(node.AsDevice())?.Device.DisplayName ?? "Missing device",
            SignalNodeKind.ExternalInstrument => project.FindInstrument(node.AsInstrument())?.Name ?? "Missing instrument",
            _ => SignalRoutingValidator.Describe(project, node),
        };
    }
}
