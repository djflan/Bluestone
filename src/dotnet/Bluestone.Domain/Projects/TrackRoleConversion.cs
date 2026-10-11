using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Domain.Projects;

/// <summary>What kind of content is being added to a track.</summary>
public enum ClipContent
{
    Notes,
    Audio,
}

public enum RoleChangeOutcome
{
    /// <summary>The role already fits.</summary>
    NoChange,

    /// <summary>Straightforward and safe: apply it and tell the user what happened.</summary>
    Automatic,

    /// <summary>Safe only with a change the user has not asked for. Show <see cref="RoleChangePlan.Message"/> and apply on consent.</summary>
    NeedsConfirmation,

    /// <summary>Would hide or lose something. Nothing is changed; the message says what to do first.</summary>
    Refused,
}

/// <summary>
/// What changing a track's role would do, worked out before anything is changed. A plan is the whole
/// truth: <see cref="Apply"/> does exactly what <see cref="Message"/> says.
/// </summary>
public sealed record RoleChangePlan
{
    public required TrackId Track { get; init; }

    public required TrackRole From { get; init; }

    public required TrackRole To { get; init; }

    public required RoleChangeOutcome Outcome { get; init; }

    /// <summary>Plain language for the user: why, and what changes. Empty for <see cref="RoleChangeOutcome.NoChange"/>.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>The conversion also adds a mixer channel and an audio connection from the track to it.</summary>
    public bool AddsAudioPath { get; init; }

    /// <summary>Applies the plan.</summary>
    /// <exception cref="InvalidOperationException">The plan was refused, so there is nothing safe to apply.</exception>
    public Project Apply(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (Outcome == RoleChangeOutcome.Refused)
        {
            throw new InvalidOperationException(Message);
        }

        if (Outcome == RoleChangeOutcome.NoChange || project.Sequence.FindTrack(Track) is not { } track)
        {
            return project;
        }

        var result = project with { Sequence = project.Sequence.WithTrack(track.WithRole(To)) };
        if (!AddsAudioPath)
        {
            return result;
        }

        var channel = MixerChannel.Create(track.Name);
        return result with
        {
            Mixer = result.Mixer.With(channel),
            Connections = result.Connections.Add(SignalConnection.Create(SignalKind.Audio, SignalNode.Track(Track), SignalNode.Mixer(channel.Id))),
        };
    }
}

/// <summary>
/// Keeps a track's role and what it holds in agreement without ever discarding anything (ADR 0021). Adding
/// a note clip to an audio track, or an audio clip to an instrument track, makes it a hybrid track. When
/// that would leave content without a path (audio with nowhere to be heard) the plan says so and asks;
/// when a role cannot hold clips (effect, group) the plan refuses rather than converting silently.
/// </summary>
public static class TrackRoleConversion
{
    /// <summary>The role the clips on <paramref name="track"/> imply, or null when it holds none.</summary>
    public static TrackRole? Implied(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var notes = track.Clips.Any(c => c is NoteClip);
        var audio = track.Clips.Any(c => c is AudioClip);
        return (notes, audio) switch
        {
            (true, true) => TrackRole.Hybrid,
            (true, false) => TrackRole.Instrument,
            (false, true) => TrackRole.Audio,
            _ => null,
        };
    }

    /// <summary>What adding a clip of <paramref name="content"/> to <paramref name="track"/> does to its role.</summary>
    public static RoleChangePlan ForAddedClip(Project project, TrackId track, ClipContent content, DeviceDefinitionLookup definitions)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(definitions);
        var found = project.Sequence.FindTrack(track) ?? throw new KeyNotFoundException($"Track {track} is not in the project.");
        var role = found.Role;
        RoleChangePlan Plan(TrackRole to, RoleChangeOutcome outcome, string message = "", bool audioPath = false) =>
            new() { Track = track, From = role, To = to, Outcome = outcome, Message = message, AddsAudioPath = audioPath };

        if (role is TrackRole.Effect or TrackRole.Group)
        {
            return Plan(role, RoleChangeOutcome.Refused, $"{Name(role)} tracks do not hold clips. Put the {(content == ClipContent.Notes ? "notes" : "audio")} on an instrument, audio, or hybrid track.");
        }

        if (content == ClipContent.Notes)
        {
            if (role is TrackRole.Instrument or TrackRole.Hybrid)
            {
                return Plan(role, RoleChangeOutcome.NoChange);
            }

            return Plan(TrackRole.Hybrid, RoleChangeOutcome.Automatic, HasEventDestination(project, found, definitions)
                ? "This audio track now also holds notes, so it became a hybrid track."
                : "This audio track now also holds notes, so it became a hybrid track. The notes have no instrument yet: add one to its devices or route them to another track.");
        }

        if (role is TrackRole.Audio or TrackRole.Hybrid)
        {
            return Plan(role, RoleChangeOutcome.NoChange);
        }

        return HasAudioPath(project, found)
            ? Plan(TrackRole.Hybrid, RoleChangeOutcome.Automatic, "This instrument track now also holds audio, so it became a hybrid track.")
            : Plan(
                TrackRole.Hybrid,
                RoleChangeOutcome.NeedsConfirmation,
                "This instrument track has no audio output, so audio on it could not be heard. Make it a hybrid track and give it a mixer channel? Its notes, devices, and routing are not changed.",
                audioPath: true);
    }

    /// <summary>What setting <paramref name="track"/>'s role to <paramref name="requested"/> would do.</summary>
    public static RoleChangePlan ForRoleChange(Project project, TrackId track, TrackRole requested, DeviceDefinitionLookup definitions)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(definitions);
        var found = project.Sequence.FindTrack(track) ?? throw new KeyNotFoundException($"Track {track} is not in the project.");
        var role = found.Role;
        RoleChangePlan Plan(RoleChangeOutcome outcome, string message = "") =>
            new() { Track = track, From = role, To = requested, Outcome = outcome, Message = message };

        if (requested == role)
        {
            return Plan(RoleChangeOutcome.NoChange);
        }

        var notes = found.Clips.Any(c => c is NoteClip);
        var audio = found.Clips.Any(c => c is AudioClip);
        if (requested is TrackRole.Effect or TrackRole.Group && found.Clips.Length > 0)
        {
            return Plan(RoleChangeOutcome.Refused, $"This track holds clips, and {Name(requested).ToLowerInvariant()} tracks hold none. Move or remove its clips first.");
        }

        if (role == TrackRole.Group && project.Sequence.Tracks.Any(t => t.Group == track))
        {
            return Plan(RoleChangeOutcome.Refused, "This group still has tracks in it. Move them out first.");
        }

        if (requested == TrackRole.Instrument && audio)
        {
            return Plan(RoleChangeOutcome.Refused, "This track holds audio clips, which an instrument track cannot. Make it a hybrid track, or move the audio away first.");
        }

        if (requested == TrackRole.Audio && notes)
        {
            return Plan(RoleChangeOutcome.Refused, "This track holds note clips, which an audio track cannot. Make it a hybrid track, or move the notes away first.");
        }

        if (requested == TrackRole.Audio && HasEventProcessing(project, found, definitions))
        {
            return Plan(RoleChangeOutcome.NeedsConfirmation, "This track has an instrument or MIDI effects, which an audio track does not use. They stay in its devices but will receive no notes. Change it anyway?");
        }

        return Plan(RoleChangeOutcome.Automatic);
    }

    /// <summary>
    /// Brings roles in line with content, only ever widening: a track whose clips its role cannot hold takes
    /// the role they imply. Used when a project is opened, so a hand-edited or imported project never hides a clip.
    /// </summary>
    public static (Project Project, ImmutableArray<string> Changes) Reconcile(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var sequence = project.Sequence;
        var changes = ImmutableArray.CreateBuilder<string>();
        foreach (var track in project.Sequence.Tracks)
        {
            if (Implied(track) is not { } implied || Fits(track.Role, implied))
            {
                continue;
            }

            sequence = sequence.WithTrack(track.WithRole(implied));
            changes.Add($"\"{track.Name}\" was a {Name(track.Role).ToLowerInvariant()} track holding {(implied == TrackRole.Hybrid ? "notes and audio" : implied == TrackRole.Audio ? "audio" : "notes")}; it is now a {Name(implied).ToLowerInvariant()} track.");
        }

        return changes.Count == 0 ? (project, []) : (project with { Sequence = sequence }, changes.ToImmutable());
    }

    // A hybrid track can hold anything, so it fits every implied role; any other role fits only itself.
    private static bool Fits(TrackRole role, TrackRole implied) => role == implied || role == TrackRole.Hybrid;

    private static string Name(TrackRole role) => role switch
    {
        TrackRole.Instrument => "Instrument",
        TrackRole.Audio => "Audio",
        TrackRole.Hybrid => "Hybrid",
        TrackRole.Effect => "Effect",
        _ => "Group",
    };

    // An audio path exists when something the track produces is connected as audio, from the end of its
    // chain or from any device in it.
    private static bool HasAudioPath(Project project, Track track) =>
        project.Connections.Any(c => c.Kind == SignalKind.Audio && Owns(project, track, c.Source));

    private static bool HasEventDestination(Project project, Track track, DeviceDefinitionLookup definitions) =>
        project.Connections.Any(c => c.Kind == SignalKind.Events && Owns(project, track, c.Source))
        || HasEventProcessing(project, track, definitions, instrumentOnly: true);

    private static bool HasEventProcessing(Project project, Track track, DeviceDefinitionLookup definitions, bool instrumentOnly = false) =>
        project.ChainOf(track.Id)?.Devices.Any(d => definitions(d.Definition.Id) is { } definition
            && definition.Consumes.HasFlag(SignalKinds.Events)
            && (!instrumentOnly || definition.Category == DeviceCategory.Instrument)) == true;

    private static bool Owns(Project project, Track track, SignalNode source) => source.Kind switch
    {
        SignalNodeKind.Track => source.AsTrack() == track.Id,
        SignalNodeKind.Device => project.FindDevice(source.AsDevice()) is { Chain.Owner: { Kind: ChainOwnerKind.Track } owner } && owner.Track == track.Id,
        _ => false,
    };
}
