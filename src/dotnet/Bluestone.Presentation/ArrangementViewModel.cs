using Bluestone.Application.Editing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bluestone.Presentation;

/// <summary>
/// The arrangement's clip selection and the edits made to clips there: moving and copying (also to
/// other tracks), resizing, splitting, duplicating, deleting, and creating clips. Every change is one
/// undoable command, however many tracks it touches.
/// </summary>
public sealed partial class ArrangementViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private HashSet<ClipId> _selected = [];

    internal ArrangementViewModel(MainViewModel owner) => _owner = owner;

    /// <summary>Raised when the clip selection changes, so views can redraw.</summary>
    public event EventHandler? Changed;

    public IReadOnlySet<ClipId> SelectedClips => _selected;

    public bool HasClipSelection => _selected.Count > 0;

    private Sequence Sequence => _owner.Project.Sequence;

    public void SelectClip(ClipId clip, SelectionMode mode = SelectionMode.Replace)
    {
        switch (mode)
        {
            case SelectionMode.Replace:
                if (_selected.Count == 1 && _selected.Contains(clip))
                {
                    return;
                }

                _selected = [clip];
                break;
            case SelectionMode.Add:
                if (!_selected.Add(clip))
                {
                    return;
                }

                break;
            case SelectionMode.Toggle:
                if (!_selected.Remove(clip))
                {
                    _selected.Add(clip);
                }

                break;
        }

        RaiseChanged();
    }

    public void ClearSelection()
    {
        if (_selected.Count > 0)
        {
            _selected = [];
            RaiseChanged();
        }
    }

    /// <summary>
    /// Moves the selected clips by <paramref name="deltaTicks"/> and <paramref name="laneDelta"/> tracks
    /// (in display order), or with <paramref name="copy"/> places copies there. Clips keep their spacing:
    /// nothing moves before tick 0 or past the first or last track.
    /// </summary>
    public void MoveSelection(long deltaTicks, int laneDelta, bool copy)
    {
        var chosen = SelectedByTrack();
        if (chosen.Count == 0)
        {
            return;
        }

        var tracks = Sequence.Tracks;
        var lanes = chosen.Select(c => tracks.IndexOf(c.Track)).ToList();
        laneDelta = Math.Clamp(laneDelta, -lanes.Min(), tracks.Length - 1 - lanes.Max());
        deltaTicks = Math.Max(deltaTicks, -chosen.Min(c => c.Clips.Min(clip => clip.Start.Value)));
        if (deltaTicks == 0 && laneDelta == 0)
        {
            return;
        }

        var count = chosen.Sum(c => c.Clips.Count);
        var label = copy ? count == 1 ? "Copy Clip" : "Copy Clips" : count == 1 ? "Move Clip" : "Move Clips";

        var commands = chosen.Select((c, i) =>
        {
            var ids = c.Clips.Select(clip => clip.Id).ToList();
            var to = tracks[lanes[i] + laneDelta].Id;
            return copy ? ClipCommands.CopyClips(c.Track.Id, ids, deltaTicks, to) : ClipCommands.MoveClips(c.Track.Id, ids, deltaTicks, to);
        });
        if (laneDelta > 0)
        {
            // Lower tracks first, so clips land on a track only after that track's selected clips have
            // gone (or been copied), and cannot cut them.
            commands = commands.Reverse();
        }

        _owner.Execute(ProjectCommands.Batch(label, commands.ToList()));
    }

    /// <summary>Moves a clip's edges, revealing or hiding its content.</summary>
    public void ResizeClip(ClipId clip, long startTick, long endTick)
    {
        if (TrackOf(clip) is { } track)
        {
            _owner.Execute(ClipCommands.ResizeClip(track.Id, clip, new Tick(Math.Max(0, startTick)), new Tick(Math.Max(1, endTick))));
        }
    }

    /// <summary>Splits the selected clips at the playhead; with none selected, the clips under it on the selected tracks.</summary>
    public void SplitAtPlayhead()
    {
        var at = new Tick(Math.Max(0, _owner.PlayheadTick));
        var targets = _selected.Count > 0
            ? SelectedByTrack()
            : [.. _owner.SelectedTracks
                .Select(t => Sequence.FindTrack(t.Id))
                .OfType<Track>()
                .Select(t => (Track: t, Clips: t.ClipAt(at) is { } clip ? new List<Clip> { clip } : []))
                .Where(c => c.Clips.Count > 0)];
        var commands = targets
            .Select(c => (c.Track, Clips: c.Clips.Where(clip => clip.Start < at && at < clip.End).Select(clip => clip.Id).ToList()))
            .Where(c => c.Clips.Count > 0)
            .Select(c => ClipCommands.SplitClips(c.Track.Id, c.Clips, at))
            .ToList();
        if (commands.Count > 0)
        {
            _owner.Execute(ProjectCommands.Batch(commands.Count == 1 ? commands[0].Label : "Split Clips", commands));
        }
    }

    /// <summary>Repeats the selected clips right after themselves, per track.</summary>
    public void DuplicateSelection() =>
        Run("Duplicate Clip", "Duplicate Clips", (track, ids) => ClipCommands.DuplicateClips(track, ids));

    public void DeleteSelection()
    {
        Run("Delete Clip", "Delete Clips", (track, ids) => ClipCommands.DeleteClips(track, ids));
        ClearSelection();
    }

    /// <summary>Creates an empty one-bar clip at the bar containing <paramref name="tick"/> (within the free space there) and selects it.</summary>
    public ClipId? CreateClip(TrackId track, long tick)
    {
        if (Sequence.FindTrack(track) is not { } target)
        {
            return null;
        }

        var position = new Tick(Math.Max(0, tick));
        if (target.ClipAt(position) is not null)
        {
            return null;
        }

        var meter = Sequence.MeterMap;
        var start = Tick.Max(meter.BarStart(position), target.GapAt(position).From);
        var clip = NoteClip.Create(start, meter.NextBarStart(position) - start);
        _owner.Execute(ClipCommands.CreateClip(track, clip));
        SelectClip(clip.Id);
        return clip.Id;
    }

    /// <summary>The name a clip shows: its own, or else its track's. Null if there is no such clip.</summary>
    public string? DisplayName(ClipId clip) => TrackOf(clip) is { } track ? track.ClipName(track.FindClip(clip)!) : null;

    /// <summary>
    /// Names a clip, as typed over its header. Leaving the name empty, or as the track's name it was
    /// showing, keeps the clip following its track's name.
    /// </summary>
    public void RenameClip(ClipId clip, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (TrackOf(clip) is not { } track)
        {
            return;
        }

        // The command trims and bounds the name; here only the decision to keep following the track.
        if (track.FindClip(clip)!.Name.Length == 0 && name.Trim() == track.Name.Trim())
        {
            return;
        }

        _owner.Execute(ClipCommands.RenameClip(track.Id, clip, name));
    }

    /// <summary>Drops clips that no longer exist from the selection, e.g. after undo.</summary>
    internal void Sync()
    {
        if (_selected.Count > 0 && _selected.RemoveWhere(id => TrackOf(id) is null) > 0)
        {
            RaiseChanged();
        }
    }

    /// <summary>Drops selected clips that are not on <paramref name="tracks"/>, e.g. when the track selection moves away from them.</summary>
    internal void KeepOnTracks(IReadOnlyCollection<TrackId> tracks)
    {
        if (_selected.Count > 0 && _selected.RemoveWhere(id => TrackOf(id) is not { } track || !tracks.Contains(track.Id)) > 0)
        {
            RaiseChanged();
        }
    }

    private void Run(string single, string plural, Func<TrackId, IReadOnlyCollection<ClipId>, IProjectCommand> command)
    {
        var chosen = SelectedByTrack();
        if (chosen.Count > 0)
        {
            var count = chosen.Sum(c => c.Clips.Count);
            _owner.Execute(ProjectCommands.Batch(count == 1 ? single : plural, [.. chosen.Select(c => command(c.Track.Id, [.. c.Clips.Select(clip => clip.Id)]))]));
        }
    }

    private Track? TrackOf(ClipId clip) => Sequence.Tracks.FirstOrDefault(t => t.FindClip(clip) is not null);

    /// <summary>The selected clips grouped by track, in track and timeline order.</summary>
    private List<(Track Track, List<Clip> Clips)> SelectedByTrack() =>
        [.. Sequence.Tracks
            .Select(t => (Track: t, Clips: t.Clips.Where(c => _selected.Contains(c.Id)).ToList()))
            .Where(c => c.Clips.Count > 0)];

    private void RaiseChanged()
    {
        OnPropertyChanged(nameof(HasClipSelection));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
