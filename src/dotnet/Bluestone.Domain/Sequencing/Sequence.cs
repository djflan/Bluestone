using System.Collections.Immutable;
using Bluestone.Domain.Time;

namespace Bluestone.Domain.Sequencing;

/// <summary>A named position on the timeline.</summary>
public sealed record Marker(Tick Position, string Name);

/// <summary>
/// An immutable sequence: resolution, conductor maps (tempo and meter), ordered tracks, and markers.
/// </summary>
public sealed class Sequence
{
    private Sequence(Ppqn ppqn, TempoMap tempoMap, MeterMap meterMap, ImmutableArray<Track> tracks, ImmutableArray<Marker> markers)
    {
        Ppqn = ppqn;
        TempoMap = tempoMap;
        MeterMap = meterMap;
        Tracks = tracks;
        Markers = markers;
    }

    /// <exception cref="ArgumentException">The maps use a different resolution, or two tracks share an ID.</exception>
    public Sequence(TempoMap tempoMap, MeterMap meterMap, IEnumerable<Track> tracks, IEnumerable<Marker> markers)
    {
        ArgumentNullException.ThrowIfNull(tempoMap);
        ArgumentNullException.ThrowIfNull(meterMap);
        if (tempoMap.Ppqn != meterMap.Ppqn)
        {
            throw new ArgumentException("Tempo and meter maps must use the same resolution.", nameof(meterMap));
        }

        Ppqn = tempoMap.Ppqn;
        TempoMap = tempoMap;
        MeterMap = meterMap;
        Tracks = ValidateTracks(tracks);
        Markers = SortMarkers(markers);
    }

    public static Sequence CreateEmpty(Ppqn ppqn) =>
        new(TempoMap.Constant(ppqn, Tempo.Default), MeterMap.Constant(ppqn, TimeSignature.CommonTime), [], []);

    public Ppqn Ppqn { get; }

    public TempoMap TempoMap { get; }

    public MeterMap MeterMap { get; }

    /// <summary>Tracks in display order. Track order breaks ties between simultaneous events on different tracks.</summary>
    public ImmutableArray<Track> Tracks { get; }

    /// <summary>Markers in ascending position order.</summary>
    public ImmutableArray<Marker> Markers { get; }

    /// <summary>The latest tick affected by any track, or zero if the sequence is empty.</summary>
    public Tick EndPosition => Tracks.IsEmpty ? Tick.Zero : Tracks.Max(t => t.EndPosition);

    public Track? FindTrack(TrackId id) => Tracks.FirstOrDefault(t => t.Id == id);

    /// <summary>Replaces the track with the same ID in place, or appends it if new.</summary>
    public Sequence WithTrack(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        for (var i = 0; i < Tracks.Length; i++)
        {
            if (Tracks[i].Id == track.Id)
            {
                return new Sequence(Ppqn, TempoMap, MeterMap, Tracks.SetItem(i, track), Markers);
            }
        }

        return new Sequence(Ppqn, TempoMap, MeterMap, Tracks.Add(track), Markers);
    }

    /// <summary>Inserts a new track at <paramref name="index"/> (0 to the track count).</summary>
    /// <exception cref="ArgumentException">A track with the same ID already exists.</exception>
    public Sequence InsertTrack(int index, Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, Tracks.Length);
        if (FindTrack(track.Id) is not null)
        {
            throw new ArgumentException($"Track {track.Id} is already in the sequence.", nameof(track));
        }

        return new Sequence(Ppqn, TempoMap, MeterMap, Tracks.Insert(index, track), Markers);
    }

    public Sequence WithoutTrack(TrackId id) =>
        new(Ppqn, TempoMap, MeterMap, Tracks.RemoveAll(t => t.Id == id), Markers);

    public Sequence WithTempoMap(TempoMap tempoMap) => new(tempoMap, MeterMap, Tracks, Markers);

    public Sequence WithMeterMap(MeterMap meterMap) => new(TempoMap, meterMap, Tracks, Markers);

    public Sequence WithMarkers(IEnumerable<Marker> markers) => new(Ppqn, TempoMap, MeterMap, Tracks, SortMarkers(markers));

    private static ImmutableArray<Track> ValidateTracks(IEnumerable<Track> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var result = tracks.ToImmutableArray();
        var ids = new HashSet<TrackId>();
        foreach (var track in result)
        {
            ArgumentNullException.ThrowIfNull(track, nameof(tracks));
            if (!ids.Add(track.Id))
            {
                throw new ArgumentException($"Track {track.Id} appears more than once.", nameof(tracks));
            }
        }

        return result;
    }

    private static ImmutableArray<Marker> SortMarkers(IEnumerable<Marker> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        return [.. markers.OrderBy(m => m.Position)];
    }
}
