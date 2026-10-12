using System.Collections.ObjectModel;
using Bluestone.Application.Editing;
using Bluestone.Domain.Routing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>
/// The inspector's Track section: what plays the selected tracks (the chain's instrument), their devices, and where their audio
/// goes (the primary audio output, <see cref="TrackSetup.PrimaryAudioOutput"/>). Links open the chain in a Device View or the
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

    public string SendsText => Sound.AudioSendCount == 1 ? "+1 send in Connections" : $"+{Sound.AudioSendCount} sends in Connections";

    /// <summary>A track in a group sends its sound through the group (ADR 0030), so its destination is not chosen here.</summary>
    [ObservableProperty]
    public partial bool CanChooseAudioDestination { get; private set; } = true;

    /// <summary>Where the selected tracks' audio goes: the master, a mixer channel, another track, or nowhere.</summary>
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
            CanChooseAudioDestination = !tracks.Any(t => t.IsInGroup);
            SyncAudioDestinations();
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnAudioDestinationChanged(AudioDestinationOption? value)
    {
        if (!_syncing && value is { IsMixed: false })
        {
            _owner.Execute(TrackSetup.SetAudioDestination([.. _tracks.Select(t => t.Id)], value.Destination, _owner.Devices.Lookup));
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
        AudioDestinationChoices.Add(new AudioDestinationOption(SignalNode.Master, "Master"));
        foreach (var channel in project.Mixer.Channels)
        {
            AudioDestinationChoices.Add(new AudioDestinationOption(SignalNode.Mixer(channel.Id), channel.Name));
        }

        AudioDestinationChoices.Add(AudioDestinationOption.None);
        var current = _tracks.Select(t => t.Sound.AudioNode).Distinct().ToList();
        if (current.Count > 1)
        {
            AudioDestinationChoices.Insert(0, AudioDestinationOption.Mixed);
            AudioDestination = AudioDestinationOption.Mixed;
        }
        else if (current.Count == 1)
        {
            var node = current[0];
            var match = AudioDestinationChoices.FirstOrDefault(o => o.Destination == node);
            if (match is null && node is { } other)
            {
                // Another track or a rack: listed so the inspector shows it, and can be chosen again.
                match = new AudioDestinationOption(other, TrackSound.NameOf(project, other));
                AudioDestinationChoices.Insert(AudioDestinationChoices.Count - 1, match);
            }

            AudioDestination = match;
        }
        else
        {
            AudioDestination = null;
        }
    }
}
