using System.Collections.ObjectModel;
using System.Globalization;
using Bluestone.Application.Editing;
using Bluestone.Domain.Sequencing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>
/// Group tracks in the track list (ADR 0030): making a group from the selected tracks, putting them in a group
/// or taking them out (which routes them, as one undo step), and which rows folded groups hide.
/// </summary>
public sealed partial class GroupsViewModel : ObservableObject
{
    /// <summary>How far a member's header is indented per level of nesting, in device-independent pixels.</summary>
    public const double IndentPerLevel = 12;

    private readonly MainViewModel _owner;

    internal GroupsViewModel(MainViewModel owner) => _owner = owner;

    /// <summary>The group tracks, in track order: where the selected tracks can be put.</summary>
    public ObservableCollection<TrackViewModel> Available { get; } = [];

    /// <summary>Tracks hidden under a folded group, so the arrangement can leave out their rows.</summary>
    [ObservableProperty]
    public partial IReadOnlySet<TrackId> HiddenTracks { get; private set; } = new HashSet<TrackId>();

    /// <summary>Whether any selected track is in a group, so "Remove from Group" has something to do.</summary>
    [ObservableProperty]
    public partial bool CanRemoveSelected { get; private set; }

    private Sequence Sequence => _owner.Project.Sequence;

    /// <summary>Makes a new group just above the first selected track, puts the selected tracks in it, and selects it.</summary>
    [RelayCommand]
    private void GroupSelected()
    {
        var members = _owner.SelectedTracks.Select(t => t.Id).ToList();
        if (members.Count == 0)
        {
            return;
        }

        var id = TrackId.New();
        var number = Sequence.Tracks.Count(t => t.Role == TrackRole.Group) + 1;
        if (_owner.Execute(GroupCommands.CreateGroup(id, string.Create(CultureInfo.InvariantCulture, $"Group {number}"), members, _owner.Devices.Lookup))
            && _owner.Tracks.FirstOrDefault(t => t.Id == id) is { } created)
        {
            _owner.Select(created);
        }
    }

    /// <summary>Puts the selected tracks (other than <paramref name="group"/> itself) in <paramref name="group"/>.</summary>
    [RelayCommand]
    private void AddSelectedTo(TrackViewModel? group)
    {
        if (group is null)
        {
            return;
        }

        var tracks = _owner.SelectedTracks.Where(t => t.Id != group.Id).Select(t => t.Id).ToList();
        if (tracks.Count > 0)
        {
            _owner.Execute(GroupCommands.SetGroup(tracks, group.Id, _owner.Devices.Lookup));
        }
    }

    /// <summary>Takes the selected tracks out of their groups, putting their own outputs back.</summary>
    [RelayCommand]
    private void RemoveSelected()
    {
        var tracks = _owner.SelectedTracks.Where(t => t.IsInGroup).Select(t => t.Id).ToList();
        if (tracks.Count > 0)
        {
            _owner.Execute(GroupCommands.SetGroup(tracks, null, _owner.Devices.Lookup));
        }
    }

    internal void Fold(TrackViewModel group, bool folded)
    {
        if (!_owner.Execute(GroupCommands.SetFolded(group.Id, folded)) || !folded)
        {
            return;
        }

        // A selection that disappears under the fold moves to the group, so the inspector never edits a hidden track.
        if (_owner.SelectedTracks.Any(t => HiddenTracks.Contains(t.Id)))
        {
            _owner.Select(group);
        }
    }

    /// <summary>Brings membership, nesting, folding, and group mute into the track rows.</summary>
    internal void Sync()
    {
        var sequence = Sequence;
        var hidden = new HashSet<TrackId>();
        foreach (var row in _owner.Tracks)
        {
            if (sequence.FindTrack(row.Id) is not { } track)
            {
                continue;
            }

            var groups = GroupTracks.Ancestors(sequence, track).ToList();
            var shown = !groups.Any(g => g.IsFolded);
            if (!shown)
            {
                hidden.Add(track.Id);
            }

            row.SyncGroup(groups.Count > 0 ? groups[0].Name : null, groups.Count, shown, GroupTracks.IsMutedByGroup(sequence, track), GroupTracks.Members(sequence, track.Id).Count());
        }

        if (!hidden.SetEquals(HiddenTracks))
        {
            HiddenTracks = hidden;
            _owner.OnTrackLayoutChanged();
        }

        var available = _owner.Tracks.Where(t => t.IsGroup).ToList();
        if (!available.SequenceEqual(Available))
        {
            Available.Clear();
            foreach (var group in available)
            {
                Available.Add(group);
            }
        }

        SyncSelection();
    }

    internal void SyncSelection() => CanRemoveSelected = _owner.SelectedTracks.Any(t => t.IsInGroup);
}
