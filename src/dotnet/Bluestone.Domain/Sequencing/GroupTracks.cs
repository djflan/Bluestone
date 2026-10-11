namespace Bluestone.Domain.Sequencing;

/// <summary>
/// Group membership and what it means for playing (ADR 0030): a group's mute and solo reach every track
/// inside it, at any depth. Membership names a track's group by ID; a group that is missing, or a loop of
/// groups (which no command creates), simply ends the walk.
/// </summary>
public static class GroupTracks
{
    /// <summary>The groups around <paramref name="track"/>, nearest first.</summary>
    public static IEnumerable<Track> Ancestors(Sequence sequence, Track track)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(track);
        var seen = new HashSet<TrackId> { track.Id };
        var current = track;
        while (current.Group is { } id && seen.Add(id) && sequence.FindTrack(id) is { } group)
        {
            yield return group;
            current = group;
        }
    }

    /// <summary>The tracks directly in <paramref name="group"/>, in track order.</summary>
    public static IEnumerable<Track> Members(Sequence sequence, TrackId group)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return sequence.Tracks.Where(t => t.Group == group);
    }

    /// <summary>Whether <paramref name="track"/> is inside <paramref name="group"/>, directly or through nested groups.</summary>
    public static bool IsInside(Sequence sequence, Track track, TrackId group) => Ancestors(sequence, track).Any(g => g.Id == group);

    /// <summary>How deep <paramref name="track"/> is nested: 0 outside any group.</summary>
    public static int Depth(Sequence sequence, Track track) => Ancestors(sequence, track).Count();

    /// <summary>
    /// Whether each track's own content plays, by track index. Without groups this is the old rule: while any
    /// track is soloed only soloed tracks play (solo overrides mute), otherwise every unmuted track plays.
    /// With groups the nearest explicit state wins, walking out from the track through its groups: while
    /// anything is soloed a track plays if it or a group around it is soloed before any of them is muted;
    /// otherwise it plays unless it or a group around it is muted.
    /// </summary>
    public static bool[] Playing(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var anySolo = sequence.Tracks.Any(t => t.IsSoloed);
        var result = new bool[sequence.Tracks.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Plays(sequence, sequence.Tracks[i], anySolo);
        }

        return result;
    }

    /// <summary>Whether <paramref name="track"/>'s own content plays. See <see cref="Playing"/>.</summary>
    public static bool Plays(Sequence sequence, Track track)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return Plays(sequence, track, sequence.Tracks.Any(t => t.IsSoloed));
    }

    /// <summary>Whether a group around <paramref name="track"/> mutes it: a group is muted and nothing nearer is soloed.</summary>
    public static bool IsMutedByGroup(Sequence sequence, Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.IsMuted || track.IsSoloed)
        {
            return false;
        }

        foreach (var group in Ancestors(sequence, track))
        {
            if (group.IsSoloed)
            {
                return false;
            }

            if (group.IsMuted)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Plays(Sequence sequence, Track track, bool anySolo)
    {
        if (track.Group is null)
        {
            return anySolo ? track.IsSoloed : !track.IsMuted;
        }

        if (anySolo)
        {
            if (track.IsSoloed)
            {
                return true;
            }

            if (track.IsMuted)
            {
                return false;
            }

            foreach (var group in Ancestors(sequence, track))
            {
                if (group.IsSoloed)
                {
                    return true;
                }

                if (group.IsMuted)
                {
                    return false;
                }
            }

            return false;
        }

        return !track.IsMuted && !Ancestors(sequence, track).Any(g => g.IsMuted);
    }
}
