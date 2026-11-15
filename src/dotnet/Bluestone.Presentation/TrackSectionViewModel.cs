using System.Collections.ObjectModel;
using Bluestone.Application.Editing;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Routing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>
/// The inspector's Track section: what plays the selected tracks (the chain's instrument), their devices, and where their audio
/// goes (the ordinary audio output). Links open the chain in a Device View or the
/// Browser on its instruments. External MIDI settings stay in <see cref="SelectionViewModel"/>.
/// </summary>
public sealed partial class TrackSectionViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private IReadOnlyList<TrackViewModel> _tracks = [];
    private bool _syncing;

    internal TrackSectionViewModel(MainViewModel owner) => _owner = owner;

    /// <summary>What the single selected track sounds through; empty for several tracks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstrument), nameof(InstrumentText), nameof(HasNoteDestination), nameof(HasSends), nameof(SendsText))]
    public partial TrackSound Sound { get; private set; } = TrackSound.Empty;

    public bool HasInstrument => Sound.Instrument is not null;

    /// <summary>The chain's instrument (and how many more it layers), with its state when it is not playing normally.</summary>
    public string InstrumentText => Sound.InstrumentSummary is not { } name ? "None"
        : Sound.IsInstrumentUnavailable ? $"{name} (unavailable)"
        : Sound.IsInstrumentBypassed ? $"{name} (bypassed)"
        : name;

    public bool HasNoteDestination => Sound.NoteDestination is not null;

    /// <summary>The track's output also sends audio elsewhere, beyond the primary output shown in "Audio to".</summary>
    public bool HasSends => Sound.AudioSendCount > 0;

    public string SendsText => Sound.AudioSendCount == 1 ? "+1 send in Mixer" : $"+{Sound.AudioSendCount} sends in Mixer";

    /// <summary>Only a single audio-capable track has an editable output; a group member is routed by its group.</summary>
    [ObservableProperty]
    public partial bool CanChooseAudioDestination { get; private set; } = true;

    /// <summary>Valid output destinations for the selected track: its parent Group, or Master.</summary>
    public ObservableCollection<AudioDestinationOption> AudioDestinationChoices { get; } = [];

    [ObservableProperty]
    public partial AudioDestinationOption? AudioDestination { get; set; }

    internal void Sync(IReadOnlyList<TrackViewModel> tracks)
    {
        _syncing = true;
        try
        {
            _tracks = tracks;
            Sound = tracks.Count == 1 ? tracks[0].Sound : TrackSound.Empty;
            var selected = tracks.Count == 1 ? _owner.Project.Sequence.FindTrack(tracks[0].Id) : null;
            CanChooseAudioDestination = selected?.AudioStrip is not null;
            SyncAudioDestinations();
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnAudioDestinationChanged(AudioDestinationOption? value)
    {
        if (!_syncing && value is { IsMixed: false, Destination: { } destination } && _tracks.Count == 1)
        {
            _owner.Execute(RoutingCommands.SetAudioOutput(_tracks[0].Id, destination, _owner.Devices.Lookup));
        }
    }

    /// <summary>Shows the chain's instrument, selected, in a Device View.</summary>
    [RelayCommand]
    private void ShowInstrument() => _owner.Workspace.RevealSelectedChain(Sound.InstrumentDevice);

    /// <summary>Shows the selected track's chain in a Device View.</summary>
    [RelayCommand]
    private void ShowDevices() => _owner.Workspace.RevealSelectedChain();

    /// <summary>Opens the Browser on its instruments, ready to insert into the selected track's chain.</summary>
    [RelayCommand]
    private void AddInstrument() => _owner.Workspace.BrowseInstruments();

    private void SyncAudioDestinations()
    {
        var project = _owner.Project;
        AudioDestinationChoices.Clear();
        if (_tracks.Count == 1 && project.Sequence.FindTrack(_tracks[0].Id) is { } track)
        {
            if (track.Group is { } group && project.Sequence.FindTrack(group) is { } parent)
            {
                AudioDestinationChoices.Add(new AudioDestinationOption(SignalNode.Track(parent.Id), parent.Name));
            }
            else
            {
                AudioDestinationChoices.Add(new AudioDestinationOption(SignalNode.Master, "Master"));
            }
        }

        var current = _tracks.Select(t => project.Connections.FirstOrDefault(connection =>
                connection.Kind == SignalKind.Audio
                && connection.AudioPurpose == AudioRoutePurpose.Output
                && connection.Source == SignalNode.Track(t.Id))?.Destination)
            .Distinct()
            .ToList();
        if (_tracks.Count > 1 && current.Count > 1)
        {
            AudioDestination = AudioDestinationOption.Mixed;
        }
        else if (_tracks.Count == 1 && current.Count == 1)
        {
            AudioDestination = AudioDestinationChoices.FirstOrDefault(option => option.Destination == current[0]);
        }
        else
        {
            AudioDestination = null;
        }
    }
}
