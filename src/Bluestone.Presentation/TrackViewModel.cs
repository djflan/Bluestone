using System.Collections.ObjectModel;
using System.Globalization;
using Bluestone.Application.Editing;
using Bluestone.Application.Routing;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Midi.Endpoints;
using Bluestone.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>
/// One row in the track list: name, mute/solo, routing status, and automation lanes. Routing is edited
/// for the whole selection through <see cref="SelectionViewModel"/>.
/// </summary>
public sealed partial class TrackViewModel : ObservableObject
{
    /// <summary>The height of a track's own row, in device-independent pixels.</summary>
    public const double TrackRowHeight = 50;

    /// <summary>The height of each automation lane shown under a track.</summary>
    public const double AutomationLaneHeight = 36;

    private readonly MainViewModel _owner;
    private bool _syncing;

    internal TrackViewModel(MainViewModel owner, TrackId id)
    {
        _owner = owner;
        Id = id;
    }

    public TrackId Id { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsSoloed { get; set; }

    /// <summary>Armed for recording. View state only: arming is not saved or undoable.</summary>
    [ObservableProperty]
    public partial bool IsArmed { get; set; }

    partial void OnIsArmedChanged(bool value)
    {
        if (!_syncing && value != (_owner.ArmedTrack == this))
        {
            _owner.ToggleArm(this);
        }
    }

    /// <summary>Whether the track's automation lanes are shown. View state only: not saved or undoable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowHeight))]
    public partial bool IsAutomationExpanded { get; set; }

    partial void OnIsAutomationExpandedChanged(bool value) => _owner.OnTrackLayoutChanged();

    public ObservableCollection<AutomationLaneViewModel> Lanes { get; } = [];

    /// <summary>The track's row plus its lanes when they are shown; nothing while a folded group hides it.</summary>
    public double RowHeight => !IsShown ? 0 : TrackRowHeight + (IsAutomationExpanded ? Lanes.Count * AutomationLaneHeight : 0);

    /// <summary>Whether this group track's members are hidden under it. Saved with the project, and undoable (ADR 0030).</summary>
    [ObservableProperty]
    public partial bool IsFolded { get; set; }

    partial void OnIsFoldedChanged(bool value)
    {
        if (!_syncing)
        {
            _owner.Groups.Fold(this, value);
        }
    }

    /// <summary>False while a group around the track is folded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowHeight))]
    public partial bool IsShown { get; private set; } = true;

    /// <summary>The name of the group the track is in, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInGroup))]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    public partial string? GroupName { get; private set; }

    public bool IsInGroup => GroupName is not null;

    /// <summary>How far the header is indented to show nesting, in device-independent pixels.</summary>
    [ObservableProperty]
    public partial double Indent { get; private set; }

    /// <summary>Silenced because a group around it is muted (the track's own mute is off).</summary>
    [ObservableProperty]
    public partial bool IsMutedByGroup { get; private set; }

    /// <summary>How many tracks are directly in this group track.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MembersText))]
    public partial int MemberCount { get; private set; }

    public bool IsGroup => Role == TrackRole.Group;

    /// <summary>Grouping commands, for the track's context menu.</summary>
    public GroupsViewModel Groups => _owner.Groups;

    /// <summary>How many tracks the group holds, for the group's header.</summary>
    public string MembersText => string.Create(CultureInfo.InvariantCulture, $"{MemberCount} track{(MemberCount == 1 ? string.Empty : "s")}");

    /// <summary>Adds a lane for <paramref name="option"/> on the track's channel, and shows the lanes.</summary>
    [RelayCommand]
    private void AddLane(AutomationOption option) => _owner.AddAutomationLane(this, option);

    [RelayCommand]
    private void RemoveLane(AutomationLaneViewModel lane) => _owner.Execute(AutomationCommands.RemoveLane(Id, lane.Id));

    /// <summary>One-based position in the track list.</summary>
    [ObservableProperty]
    public partial int Number { get; private set; }

    /// <summary>Index into the theme's track colour palette.</summary>
    public int ColorIndex => (Number - 1) % 8;

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>The track's role: what it is for (ADR 0021).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGroup))]
    public partial TrackRole Role { get; private set; }

    /// <summary>The track's output settings as the inspector edits them (see <see cref="TrackOutputs"/>).</summary>
    public TrackOutput Route { get; private set; } = TrackOutput.None;

    /// <summary>The installed profile of the track's instrument, if any.</summary>
    public DeviceProfile? ResolvedProfile { get; private set; }

    /// <summary>The track's output as a choice: an available endpoint, a disconnected placeholder, or none.</summary>
    public OutputOption Output { get; private set; } = OutputOption.None;

    public ProfileOption Profile { get; private set; } = ProfileOption.None;

    [ObservableProperty]
    public partial RouteHealth Health { get; private set; }

    public bool IsReady => Health == RouteHealth.Ready;

    public bool NeedsAttention => Health == RouteHealth.Attention;

    public bool IsOffline => Health == RouteHealth.Offline;

    /// <summary>A word for the route's health, so status never depends on colour alone.</summary>
    public string HealthText => Health switch
    {
        RouteHealth.Ready => "Ready",
        RouteHealth.Attention => "Check",
        _ => "Offline",
    };

    /// <summary>
    /// The output and, when it is not ready, a word for why: "Synth", "Synth · Check", "No output". A track in a
    /// group shows the group, which is where its output goes.
    /// </summary>
    public string StatusLine => GroupName is { } group ? $"→ {group}" : Health switch
    {
        RouteHealth.Ready => Output.Name,
        RouteHealth.Attention => $"{Output.Name} · Check",
        _ when Route.Endpoint is null => "No output",
        _ => $"{Output.Name} · Offline",
    };

    /// <summary>"Profile → Output", showing the two independent choices side by side.</summary>
    [ObservableProperty]
    public partial string RouteText { get; private set; } = string.Empty;

    public IReadOnlyList<string> Problems { get; private set; } = [];

    public bool CanInitialize => ResolvedProfile is { Initialization.Length: > 0 } && Health != RouteHealth.Offline;

    internal void Sync(int number, Track track, ResolvedTrackOutput? resolved, IReadOnlyList<OutputOption> outputs)
    {
        _syncing = true;
        try
        {
            Number = number;
            Name = track.Name;
            IsMuted = track.IsMuted;
            IsSoloed = track.IsSoloed;
            IsFolded = track.IsFolded;
            Summary = Summarize(track);
            SyncLanes(track);
            Role = track.Role;
            Route = resolved?.Output ?? TrackOutput.None;
            ResolvedProfile = resolved?.Instrument?.Profile.Profile;
            Output = ResolveOutput(outputs, resolved);
            Profile = Route.Profile is not { } reference
                ? ProfileOption.None
                : ResolvedProfile is { } installed
                    ? new ProfileOption(installed.Id, installed.Name, true)
                    : new ProfileOption(reference.ProfileId, $"{reference.DisplayName ?? reference.ProfileId} (not installed)", false);

            Health = resolved switch
            {
                null or { CanPlay: false, Live: null } => RouteHealth.Offline,
                { Problems.Length: > 0 } => RouteHealth.Attention,
                _ => RouteHealth.Ready,
            };
            Problems = resolved?.Problems ?? [];
            RouteText = $"{Profile.Name} → {Output.Name}";
            OnPropertyChanged(nameof(HealthText));
            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(NeedsAttention));
            OnPropertyChanged(nameof(IsOffline));
            OnPropertyChanged(nameof(ColorIndex));
        }
        finally
        {
            _syncing = false;
        }
    }

    internal void SyncGroup(string? group, int depth, bool shown, bool mutedByGroup, int members)
    {
        _syncing = true;
        try
        {
            GroupName = group;
            Indent = depth * GroupsViewModel.IndentPerLevel;
            IsShown = shown;
            IsMutedByGroup = mutedByGroup;
            MemberCount = members;
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnNameChanged(string value)
    {
        if (!_syncing && value.Length is > 0 and <= Track.MaxNameLength)
        {
            _owner.Execute(ProjectCommands.RenameTrack(Id, value));
        }
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (!_syncing)
        {
            _owner.Execute(ProjectCommands.SetMuted(Id, value));
        }
    }

    partial void OnIsSoloedChanged(bool value)
    {
        if (!_syncing)
        {
            _owner.Execute(ProjectCommands.SetSoloed(Id, value));
        }
    }

    private void SyncLanes(Track track)
    {
        if (Lanes.Select(l => (l.Id, l.Target)).SequenceEqual(track.Automation.Select(l => (l.Id, l.Target))))
        {
            return;
        }

        Lanes.Clear();
        foreach (var lane in track.Automation)
        {
            Lanes.Add(new AutomationLaneViewModel(this, lane.Id, lane.Target));
        }

        OnPropertyChanged(nameof(RowHeight));
        _owner.OnTrackLayoutChanged();
    }

    private OutputOption ResolveOutput(IReadOnlyList<OutputOption> outputs, ResolvedTrackOutput? resolved)
    {
        if (Route.Endpoint is not { } reference)
        {
            return OutputOption.None;
        }

        if (resolved?.Port?.Endpoint.Endpoint is { } bound && outputs.FirstOrDefault(o => o.Id == bound.Id) is { } match)
        {
            return match;
        }

        return new OutputOption(new EndpointId(reference.ProviderId, reference.EndpointKey), reference.DisplayName ?? reference.EndpointKey, "Disconnected", false);
    }

    private static string Summarize(Track track)
    {
        var notes = track.ArrangedEvents.OfType<NoteEvent>().ToList();
        var channels = track.ArrangedEvents.OfType<ChannelEvent>().Select(c => c.Channel.Number)
            .Distinct()
            .Order()
            .ToList();
        var channelText = channels.Count switch
        {
            0 => "no channel data",
            1 => string.Create(CultureInfo.InvariantCulture, $"ch {channels[0]}"),
            _ => string.Create(CultureInfo.InvariantCulture, $"ch {string.Join(", ", channels.Take(4))}{(channels.Count > 4 ? "…" : string.Empty)}"),
        };
        var clips = track.Clips.Length <= 1 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" · {track.Clips.Length} clips");
        return string.Create(CultureInfo.InvariantCulture, $"{notes.Count} notes · {channelText}{clips}");
    }
}
