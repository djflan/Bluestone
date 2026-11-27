using System.Collections.ObjectModel;
using System.Globalization;
using Bluestone.Application.Audio;
using Bluestone.Application.Editing;
using Bluestone.Audio;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>A track-owned strip output: its parent Group, or Master when the track is ungrouped.</summary>
public sealed record MixerRouteOption(SignalNode Destination, string Name)
{
    public override string ToString() => Name;
}

/// <summary>An Effect track a strip can send to.</summary>
public sealed record MixerEffectOption(TrackId Track, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The track-owned mixer console (ADR 0050). Track rows follow the arrangement by stable TrackId; the Master is a
/// separate, mandatory strip. Audio edits go through the project's strip and routing commands, never the legacy Mixer.
/// </summary>
public sealed partial class MixerConsoleViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private IAudioPlayback? _audio;
    private MixerMeterDisplaySettingsStore? _settingsStore;
    private bool _loadingSettings;

    internal MixerConsoleViewModel(MainViewModel owner)
    {
        _owner = owner;
        Master = new MixerMasterStripViewModel(this);
        _owner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ArmedTrack))
            {
                foreach (var row in Tracks)
                {
                    row.SyncArm();
                }
            }
        };
    }

    /// <summary>How many insert rows a strip shows before it summarizes the rest as "+N more".</summary>
    public const int VisibleInsertRows = 3;

    /// <summary>How many send knobs a strip shows; the rest are reached from the strip's send list.</summary>
    public const int VisibleSendKnobs = 2;

    public ObservableCollection<MixerTrackStripViewModel> Tracks { get; } = [];

    public MixerMasterStripViewModel Master { get; }

    public WorkspaceView Host => _owner.Workspace.Mixer;

    public static IReadOnlyList<MixerMeterModeOption> MeterModes { get; } =
    [
        new(MixerMeterMode.PeakRms, "Peak + RMS"),
        new(MixerMeterMode.Vu, "VU"),
        new(MixerMeterMode.K20, "K-20"),
        new(MixerMeterMode.K14, "K-14"),
        new(MixerMeterMode.K12, "K-12"),
        new(MixerMeterMode.LufsS, "LUFS-S"),
    ];

    public static IReadOnlyList<int> VuReferenceChoices { get; } = Enumerable.Range(-36, 37).ToArray();

    /// <summary>Legacy peak/RMS source for isolated projections that are not attached to an audio API.</summary>
    public Func<TrackId?, MeterReading?>? MeterSource { get; set; }

    [ObservableProperty]
    public partial MixerMeterMode MeterMode { get; set; } = MixerMeterMode.PeakRms;

    [ObservableProperty]
    public partial int VuReferenceDbfs { get; set; } = -18;

    public string Summary => Tracks.Count switch
    {
        0 => "Master · no tracks",
        1 => "1 track · Master",
        var count => $"{count} tracks · Master",
    };

    /// <summary>Loads application-local settings. The audio API receives the DSP settings when attached.</summary>
    public void UseSettingsStore(MixerMeterDisplaySettingsStore store)
    {
        _settingsStore = store ?? throw new ArgumentNullException(nameof(store));
        var settings = store.Load();
        _loadingSettings = true;
        try
        {
            MeterMode = settings.Mode;
            VuReferenceDbfs = settings.VuReferenceDbfs;
        }
        finally
        {
            _loadingSettings = false;
        }

        ApplyAudioSettings(save: false);
    }

    /// <summary>Connects the live audio API and applies settings to current and future mixer meters.</summary>
    public void UseAudio(IAudioPlayback audio)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        ApplyAudioSettings(save: false);
    }

    internal void Sync(Project project)
    {
        var wanted = project.Sequence.Tracks;
        for (var i = Tracks.Count - 1; i >= 0; i--)
        {
            if (!wanted.Any(track => track.Id == Tracks[i].Id))
            {
                Tracks.RemoveAt(i);
            }
        }

        for (var i = 0; i < wanted.Length; i++)
        {
            var row = Tracks.FirstOrDefault(candidate => candidate.Id == wanted[i].Id);
            if (row is null)
            {
                row = new MixerTrackStripViewModel(this, wanted[i].Id);
                Tracks.Insert(Math.Min(i, Tracks.Count), row);
            }
            else if (Tracks.IndexOf(row) != i)
            {
                Tracks.Move(Tracks.IndexOf(row), i);
            }

            row.Sync(project, wanted[i], i);
        }

        Master.Sync(project);
        SyncSelection();
        OnPropertyChanged(nameof(Summary));
    }

    internal void SyncSelection()
    {
        var selected = _owner.SelectedTracks.Select(track => track.Id).ToHashSet();
        foreach (var row in Tracks)
        {
            row.IsSelected = selected.Contains(row.Id);
        }

        Master.IsSelected = _owner.Selection.IsMasterSelected;
    }

    internal void SelectMaster()
    {
        _owner.SelectedTracks.Clear();
        _owner.Selection.SelectMasterScope();
        SyncSelection();
    }

    /// <summary>Updates the selected display reading and keeps the separate Peak + RMS meter current while visible.</summary>
    public void RefreshMeters(long timestamp)
    {
        Master.UpdateMeter(MixerMeterOf(null), MeterMode, timestamp);
        for (var i = 0; i < Tracks.Count; i++)
        {
            var row = Tracks[i];
            row.UpdateMeter(row.HasAudioStrip ? MixerMeterOf(row.Id) : null, MeterMode, timestamp);
        }
    }

    private MixerMeterReading? MixerMeterOf(TrackId? track)
    {
        if (_audio is { } audio)
        {
            return audio.MixerMeterOf(track);
        }

        if (MeterSource?.Invoke(track) is not { } reading)
        {
            return null;
        }

        return new MixerMeterReading(
            reading,
            MixerMeterMode.PeakRms,
            MeterReading.ToDecibels(reading.PeakLeft),
            MeterReading.ToDecibels(reading.PeakRight),
            true);
    }

    partial void OnMeterModeChanged(MixerMeterMode value)
    {
        if (!Enum.IsDefined(value))
        {
            MeterMode = MixerMeterMode.PeakRms;
            return;
        }

        ApplyAudioSettings();
    }

    partial void OnVuReferenceDbfsChanged(int value)
    {
        if (value is < -36 or > 0)
        {
            VuReferenceDbfs = Math.Clamp(value, -36, 0);
            return;
        }

        ApplyAudioSettings();
    }

    [RelayCommand]
    private void SetMeterMode(MixerMeterMode mode) => MeterMode = mode;

    [RelayCommand]
    private void SetVuReferenceDbfs(int referenceDbfs) => VuReferenceDbfs = referenceDbfs;

    private void ApplyAudioSettings(bool save = true)
    {
        _audio?.SetMixerMeterSettings(new MixerMeterSettings(MeterMode, VuReferenceDbfs));
        if (save)
        {
            SaveSettings();
        }
    }

    private void SaveSettings()
    {
        if (_loadingSettings || _settingsStore is not { } store)
        {
            return;
        }

        try
        {
            store.Save(new MixerMeterDisplaySettings
            {
                Mode = MeterMode,
                VuReferenceDbfs = VuReferenceDbfs,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A preference that could not be saved still applies for this session.
        }
    }

    internal DeviceDefinitionLookup Definitions => _owner.Devices.Lookup;

    internal bool Execute(IProjectCommand command) => _owner.Execute(command);

    /// <summary>Executes a reset as its own undo step: it never merges into the drag or wheel edit just before it.</summary>
    internal bool ExecuteAlone(IProjectCommand command) => _owner.Execute(new ProjectCommand(command.Label, command.Apply));

    internal void Post(Action action) => _owner.Post(action);

    internal bool IsArmedTrack(TrackId id) => _owner.ArmedTrack?.Id == id;

    internal TrackViewModel? TrackRow(TrackId id) => _owner.Tracks.FirstOrDefault(track => track.Id == id);

    internal void SelectTrack(TrackId id)
    {
        if (_owner.Tracks.FirstOrDefault(track => track.Id == id) is { } track)
        {
            _owner.Select(track);
        }
    }

    internal void OpenTrackChain(TrackId id, DeviceId? device = null)
    {
        SelectTrack(id);
        _owner.Workspace.RevealSelectedChain(device);
    }

    internal static string FormatGain(double decibels) =>
        string.Create(CultureInfo.InvariantCulture, $"{decibels:+0.0;-0.0;0.0} dB");

    internal static string FormatGainValue(double decibels) =>
        string.Create(CultureInfo.InvariantCulture, $"{decibels:+0.0;-0.0;0.0}");

    /// <summary>"&lt;C&gt;" at center, otherwise the side and whole percent with no space: "L41", "R35".</summary>
    internal static string FormatPan(double pan)
    {
        var percent = (int)Math.Round(Math.Abs(pan) * 100, MidpointRounding.AwayFromZero);
        return percent == 0
            ? "<C>"
            : string.Create(CultureInfo.InvariantCulture, $"{(pan < 0 ? 'L' : 'R')}{percent}");
    }
}

/// <summary>A mixer console row keyed by the owning arrangement track's stable identity.</summary>
public abstract partial class MixerMeterStripViewModel : ObservableObject
{
    public MeterLevels Meter { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMeterWarmingUp), nameof(MeterStatus))]
    public partial MixerMeterMode MeterMode { get; private set; } = MixerMeterMode.PeakRms;

    [ObservableProperty]
    public partial double MeterLeftValue { get; private set; } = double.NaN;

    [ObservableProperty]
    public partial double MeterRightValue { get; private set; } = double.NaN;

    [ObservableProperty]
    public partial double MeterLeftRmsValue { get; private set; } = double.NaN;

    [ObservableProperty]
    public partial double MeterRightRmsValue { get; private set; } = double.NaN;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeterPeakHold))]
    public partial double MeterLeftPeakHold { get; private set; } = MeterLevels.FloorDecibels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeterPeakHold))]
    public partial double MeterRightPeakHold { get; private set; } = MeterLevels.FloorDecibels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMeterWarmingUp), nameof(MeterStatus))]
    public partial bool IsMeterReady { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMeterWarmingUp), nameof(MeterStatus))]
    public partial bool HasMeterReading { get; private set; }

    public bool IsMeterWarmingUp => HasMeterReading && MeterMode == MixerMeterMode.LufsS && !IsMeterReady;

    public string MeterStatus => !HasMeterReading ? "Unavailable" : IsMeterWarmingUp ? "Warming up" : string.Empty;

    /// <summary>
    /// The strip's single meter fill, or NaN with no reading. In Peak + RMS mode it is the louder RMS in dBFS (the held peak
    /// is the marker); in every other mode it is the louder channel of the selected mode's value.
    /// </summary>
    [ObservableProperty]
    public partial double MeterValue { get; private set; } = double.NaN;

    /// <summary>The higher of the two held peaks, in dBFS.</summary>
    public double MeterPeakHold => LouderOf(MeterLeftPeakHold, MeterRightPeakHold);

    /// <summary>
    /// The held peak in dBFS for the strip readout ("-3.6"), or an em dash with no reading or only silence. It changes, and
    /// notifies, only when the shown tenths change; the strings come from a precomputed table so metering never allocates.
    /// </summary>
    [ObservableProperty]
    public partial string PeakText { get; private set; } = NoPeakText;

    private const string NoPeakText = "—";
    private const int PeakTextMinTenths = (int)(MeterLevels.FloorDecibels * 10);
    private const int PeakTextMaxTenths = 600;

    private static readonly string[] PeakTexts = [.. Enumerable.Range(PeakTextMinTenths, PeakTextMaxTenths - PeakTextMinTenths + 1)
        .Select(tenths => (tenths / 10.0).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture))];

    private static string PeakTextOf(bool hasReading, double hold)
    {
        if (!hasReading || double.IsNaN(hold) || hold <= MeterLevels.FloorDecibels)
        {
            return NoPeakText;
        }

        var tenths = (int)Math.Clamp(Math.Round(hold * 10, MidpointRounding.AwayFromZero), PeakTextMinTenths, PeakTextMaxTenths);
        return PeakTexts[tenths - PeakTextMinTenths];
    }

    /// <summary>The strip's insert devices in chain order.</summary>
    public ObservableCollection<MixerInsertViewModel> Inserts { get; } = [];

    /// <summary>The inserts the strip shows: at most <see cref="MixerConsoleViewModel.VisibleInsertRows"/>.</summary>
    public ObservableCollection<MixerInsertViewModel> VisibleInserts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverflowText), nameof(HasOverflow))]
    public partial int OverflowInsertCount { get; private set; }

    public bool HasOverflow => OverflowInsertCount > 0;

    public string OverflowText => HasOverflow ? string.Create(CultureInfo.InvariantCulture, $"+{OverflowInsertCount} more") : string.Empty;

    public bool HasNoInserts => Inserts.Count == 0;

    public string InsertCountText => string.Create(CultureInfo.InvariantCulture, $"{Inserts.Count} DEVICE{(Inserts.Count == 1 ? string.Empty : "S")}");

    private static double LouderOf(double left, double right) =>
        double.IsNaN(left) ? right : double.IsNaN(right) ? left : Math.Max(left, right);

    internal abstract void OpenInsert(DeviceId device);

    private protected void SyncInserts(MixerConsoleViewModel console, DeviceChain? chain)
    {
        var devices = chain?.Devices ?? [];
        var countChanged = Inserts.Count != devices.Length;

        // Rows follow their device, so a focused or bound row keeps its device when others are added, removed, or moved.
        for (var i = 0; i < devices.Length; i++)
        {
            var existing = -1;
            for (var j = i; j < Inserts.Count; j++)
            {
                if (Inserts[j].DeviceId == devices[i].Id)
                {
                    existing = j;
                    break;
                }
            }

            if (existing < 0)
            {
                Inserts.Insert(i, new MixerInsertViewModel(console, this));
            }
            else if (existing != i)
            {
                Inserts.Move(existing, i);
            }

            Inserts[i].Sync(devices[i]);
        }

        while (Inserts.Count > devices.Length)
        {
            Inserts.RemoveAt(Inserts.Count - 1);
        }

        OverflowInsertCount = Inserts.Count - SyncWindow(Inserts, VisibleInserts, MixerConsoleViewModel.VisibleInsertRows);
        if (countChanged)
        {
            OnPropertyChanged(nameof(HasNoInserts));
            OnPropertyChanged(nameof(InsertCountText));
        }
    }

    /// <summary>Keeps <paramref name="visible"/> as the first <paramref name="limit"/> items of <paramref name="all"/>, in place.</summary>
    /// <returns>How many items are visible.</returns>
    private protected static int SyncWindow<T>(ObservableCollection<T> all, ObservableCollection<T> visible, int limit)
        where T : class
    {
        var count = Math.Min(all.Count, limit);
        while (visible.Count > count)
        {
            visible.RemoveAt(visible.Count - 1);
        }

        for (var i = 0; i < count; i++)
        {
            if (i == visible.Count)
            {
                visible.Add(all[i]);
            }
            else if (!ReferenceEquals(visible[i], all[i]))
            {
                visible[i] = all[i];
            }
        }

        return count;
    }

    internal void UpdateMeter(MixerMeterReading? reading, MixerMeterMode fallbackMode, long timestamp)
    {
        Meter.Update(reading?.PeakRms, timestamp);
        MeterLeftPeakHold = Meter.HoldLeft;
        MeterRightPeakHold = Meter.HoldRight;
        MeterMode = reading?.Mode ?? fallbackMode;
        MeterLeftValue = reading?.LeftValue ?? double.NaN;
        MeterRightValue = reading?.RightValue ?? double.NaN;
        var showRms = reading is { Mode: MixerMeterMode.PeakRms };
        MeterLeftRmsValue = showRms ? Meter.RmsLeft : double.NaN;
        MeterRightRmsValue = showRms ? Meter.RmsRight : double.NaN;
        IsMeterReady = reading?.IsReady ?? false;
        HasMeterReading = reading is not null;
        MeterValue = reading is null
            ? double.NaN
            : MeterMode == MixerMeterMode.PeakRms
                ? Math.Max(Meter.RmsLeft, Meter.RmsRight)
                : LouderOf(MeterLeftValue, MeterRightValue);
        PeakText = PeakTextOf(HasMeterReading, MeterPeakHold);
        IsClipped = Meter.Clipped;
    }

    /// <summary>A peak passed 0 dBFS since the last <see cref="ResetClipCommand"/>, in every meter mode.</summary>
    [ObservableProperty]
    public partial bool IsClipped { get; private set; }

    [RelayCommand]
    private void ResetClip()
    {
        Meter.ResetClip();
        IsClipped = Meter.Clipped;
    }
}

public sealed partial class MixerTrackStripViewModel : MixerMeterStripViewModel
{
    private readonly MixerConsoleViewModel _owner;
    private bool _syncing;

    internal MixerTrackStripViewModel(MixerConsoleViewModel owner, TrackId id)
    {
        _owner = owner;
        Id = id;
        MinimumGain = AudioStrip.MinGainDecibels;
        MaximumGain = AudioStrip.MaxGainDecibels;
    }

    public TrackId Id { get; }

    public double MinimumGain { get; }

    public double MaximumGain { get; }

    public ObservableCollection<MixerRouteOption> Outputs { get; } = [];

    public ObservableCollection<MixerEffectOption> SendTargets { get; } = [];

    public ObservableCollection<MixerSendViewModel> Sends { get; } = [];

    /// <summary>The send knobs the strip shows: at most <see cref="MixerConsoleViewModel.VisibleSendKnobs"/>, the same instances as <see cref="Sends"/>.</summary>
    public ObservableCollection<MixerSendViewModel> VisibleSends { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSendOverflow))]
    public partial int OverflowSendCount { get; private set; }

    public bool HasSendOverflow => OverflowSendCount > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText), nameof(AccessibilitySummary), nameof(GainAutomationName), nameof(PanAutomationName),
        nameof(MuteAutomationName), nameof(SoloAutomationName), nameof(ArmAutomationName), nameof(SelectAutomationName))]
    public partial string Name { get; private set; } = string.Empty;

    public string GainAutomationName => $"{Name} gain";

    public string PanAutomationName => $"{Name} pan";

    public string MuteAutomationName => $"Mute {Name}";

    public string SoloAutomationName => $"Solo {Name}";

    public string ArmAutomationName => $"Record arm {Name}";

    public string SelectAutomationName => IsSelected ? $"{Name}, selected" : $"Select {Name}";

    public string OutputAutomationName => $"Output: {(OutputSummary.Length > 0 ? OutputSummary : Output?.Name ?? "none")}";

    public string SendsAutomationName => Sends.Count switch
    {
        0 => "No sends, add a send",
        1 => "1 send, show all or add",
        var count => string.Create(CultureInfo.InvariantCulture, $"{count} sends, show all or add"),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibilitySummary), nameof(RoleCaption), nameof(CanArm))]
    public partial TrackRole Role { get; private set; }

    public string RoleLabel => Role switch
    {
        TrackRole.Group => "GROUP",
        TrackRole.Effect => "EFFECT",
        _ => string.Empty,
    };

    /// <summary>The strip header's role caption: "AUDIO", "HYBRID", "FX RETURN", or "GROUP · 3" with the group's direct members.</summary>
    public string RoleCaption => Role switch
    {
        TrackRole.Group => string.Create(CultureInfo.InvariantCulture, $"GROUP · {GroupChildCount}"),
        TrackRole.Effect => "FX RETURN",
        TrackRole.Instrument => "INSTRUMENT",
        TrackRole.Hybrid => "HYBRID",
        _ => "AUDIO",
    };

    /// <summary>One-based position in the track list, as the arrangement numbers it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberText), nameof(ColorIndex))]
    public partial int Number { get; private set; }

    public string NumberText => Number.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>Index into the theme's track colour palette; the same value as the arrangement row's.</summary>
    public int ColorIndex => Number < 1 ? 0 : (Number - 1) % 8;

    /// <summary>How many tracks are directly in this group track; zero for other roles.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleCaption))]
    public partial int GroupChildCount { get; private set; }

    /// <summary>Whether the track can be armed for recording (every role but Group, as in the arrangement).</summary>
    public bool CanArm => Role != TrackRole.Group && _owner.TrackRow(Id) is not null;

    /// <summary>Armed for recording; delegates to the arrangement's single armed track.</summary>
    [ObservableProperty]
    public partial bool IsArmed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibilitySummary))]
    public partial bool HasAudioStrip { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibilitySummary))]
    public partial string Status { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibilitySummary), nameof(OutputAutomationName))]
    public partial string OutputSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibilitySummary))]
    public partial string ChainSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText), nameof(GainValueText), nameof(AccessibilitySummary))]
    public partial double Gain { get; set; }

    public string GainText => MixerConsoleViewModel.FormatGain(Gain);

    /// <summary>The gain as a bare number ("-7.2", "+1.5", "0.0") for the readout under its "GAIN dB" label.</summary>
    public string GainValueText => MixerConsoleViewModel.FormatGainValue(Gain);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanText))]
    public partial double Pan { get; set; }

    public string PanText => MixerConsoleViewModel.FormatPan(Pan);

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsSoloed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectAutomationName))]
    public partial bool IsSelected { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputAutomationName))]
    public partial MixerRouteOption? Output { get; set; }

    [ObservableProperty]
    public partial MixerEffectOption? NewSendTarget { get; set; }

    [ObservableProperty]
    public partial bool IsRoutingExpanded { get; set; }

    public bool CanChooseOutput => HasAudioStrip;

    public bool CanAddSend => HasAudioStrip && NewSendTarget is not null;

    public string AccessibilitySummary =>
        $"{Name}, {(RoleLabel.Length == 0 ? Role.ToString() : RoleLabel)}, {Status}, " +
        (HasAudioStrip
            ? $"audio strip, {GainText}, pan {Pan:+0.00;-0.00;center}, {OutputSummary}, {ChainSummary}"
            : $"MIDI-only, {OutputSummary}");

    internal void Sync(Project project, Track track, int position)
    {
        _syncing = true;
        try
        {
            Name = track.Name;
            Role = track.Role;
            HasAudioStrip = track.AudioStrip is not null;
            var trackRow = _owner.TrackRow(track.Id);
            Number = trackRow is { Number: > 0 } numbered ? numbered.Number : position + 1;
            GroupChildCount = track.Role == TrackRole.Group ? project.Sequence.Tracks.Count(candidate => candidate.Group == track.Id) : 0;
            IsArmed = track.Role != TrackRole.Group && _owner.IsArmedTrack(track.Id);
            var primaryOutput = project.Connections.FirstOrDefault(connection =>
                connection.Kind == SignalKind.Audio
                && connection.AudioPurpose == AudioRoutePurpose.Output
                && connection.Source == SignalNode.Track(track.Id));
            if (HasAudioStrip && track.AudioStrip is { } strip)
            {
                Gain = strip.GainDecibels;
                Pan = strip.Pan;
                IsMuted = strip.IsMuted;
                IsSoloed = strip.IsSoloed;

                var target = track.Group is { } group
                    ? project.Sequence.FindTrack(group)
                    : null;
                var validDestination = target is null ? SignalNode.Master : SignalNode.Track(target.Id);
                var outputOptions = new List<MixerRouteOption>
                {
                    new(validDestination, target?.Name ?? "Master"),
                };
                Replace(Outputs, outputOptions);
                Output = Outputs.FirstOrDefault(option => option.Destination == primaryOutput?.Destination);
                OutputSummary = primaryOutput is null
                    ? track.Group is { } parent && project.Sequence.FindTrack(parent) is { } parentTrack
                        ? $"Output needs repair · {parentTrack.Name}"
                        : "Output needs repair · Master"
                    : TrackSound.NameOf(project, primaryOutput.Destination);
                Status = trackRow is { UsesHardware: true } ? $"MIDI + audio · {trackRow.Output.Name}" : "Audio strip";

            }
            else
            {
                Gain = 0;
                Pan = 0;
                IsMuted = false;
                IsSoloed = false;
                Outputs.Clear();
                Output = null;
                OutputSummary = trackRow is { UsesHardware: true }
                    ? $"MIDI → {trackRow.Output.Name}"
                    : trackRow?.Sound.NoteDestination is { } destination ? $"MIDI → {destination}" : "MIDI · no hardware destination";
                Status = "MIDI only · no audio strip";
            }

            var chain = project.ChainOf(track.Id);
            var deviceCount = chain?.Devices.Length ?? 0;
            var bypassed = chain?.Devices.Count(device => device.IsBypassed) ?? 0;
            ChainSummary = deviceCount == 0 ? "No devices" : $"{deviceCount} device{(deviceCount == 1 ? string.Empty : "s")}{(bypassed > 0 ? $" · {bypassed} bypassed" : string.Empty)}";
            SyncInserts(_owner, chain);

            var effectOptions = project.Sequence.Tracks
                .Where(candidate => candidate.Id != track.Id && candidate.Role == TrackRole.Effect && candidate.AudioStrip is not null)
                .Select(candidate => new MixerEffectOption(candidate.Id, candidate.Name))
                .ToList();
            Replace(SendTargets, effectOptions);

            var sends = project.Connections.Where(connection =>
                connection.Kind == SignalKind.Audio
                && connection.AudioPurpose == AudioRoutePurpose.Send
                && connection.Source == SignalNode.Track(track.Id)).ToList();
            while (Sends.Count > sends.Count)
            {
                Sends.RemoveAt(Sends.Count - 1);
            }

            for (var i = 0; i < sends.Count; i++)
            {
                if (i == Sends.Count)
                {
                    Sends.Add(new MixerSendViewModel(this));
                }

                var send = sends[i];
                var targetTrack = send.Destination.Kind == SignalNodeKind.Track
                    ? project.Sequence.FindTrack(send.Destination.AsTrack())
                    : null;
                var targetOption = targetTrack is null
                    ? new MixerEffectOption(default, "Missing Effect")
                    : effectOptions.FirstOrDefault(option => option.Track == targetTrack.Id) ?? new MixerEffectOption(targetTrack.Id, targetTrack.Name);
                Sends[i].Sync(send, targetOption, effectOptions);
            }

            var sendCountChanged = OverflowSendCount + VisibleSends.Count != Sends.Count;
            OverflowSendCount = Sends.Count - SyncWindow(Sends, VisibleSends, MixerConsoleViewModel.VisibleSendKnobs);
            if (sendCountChanged)
            {
                OnPropertyChanged(nameof(SendsAutomationName));
            }

            OnPropertyChanged(nameof(RoleLabel));
            OnPropertyChanged(nameof(CanArm));
            OnPropertyChanged(nameof(CanChooseOutput));
            OnPropertyChanged(nameof(CanAddSend));
            OnPropertyChanged(nameof(GainText));
            OnPropertyChanged(nameof(AccessibilitySummary));
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnGainChanged(double value)
    {
        OnPropertyChanged(nameof(GainText));
        if (!_syncing && HasAudioStrip)
        {
            _owner.Execute(AudioStripCommands.SetTrackGain(Id, Math.Clamp(value, MinimumGain, MaximumGain), $"mixer-track:{Id}:gain"));
        }
    }

    partial void OnPanChanged(double value)
    {
        if (!_syncing && HasAudioStrip)
        {
            _owner.Execute(AudioStripCommands.SetTrackPan(Id, Math.Clamp(value, -1, 1), $"mixer-track:{Id}:pan"));
        }
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (!_syncing && HasAudioStrip)
        {
            _owner.Execute(AudioStripCommands.SetTrackMuted(Id, value));
        }
    }

    partial void OnIsSoloedChanged(bool value)
    {
        if (!_syncing && HasAudioStrip)
        {
            _owner.Execute(AudioStripCommands.SetTrackSoloed(Id, value));
        }
    }

    partial void OnOutputChanged(MixerRouteOption? value)
    {
        if (!_syncing && HasAudioStrip && value is not null)
        {
            _owner.Execute(RoutingCommands.SetAudioOutput(Id, value.Destination, _owner.Definitions));
        }
    }

    partial void OnNewSendTargetChanged(MixerEffectOption? value) => OnPropertyChanged(nameof(CanAddSend));

    partial void OnIsArmedChanged(bool value)
    {
        if (_syncing)
        {
            return;
        }

        if (!CanArm || _owner.TrackRow(Id) is not { } row)
        {
            SyncArm();
            return;
        }

        // The arrangement row owns arming; it toggles the single armed track and the console resyncs every strip.
        row.IsArmed = value;
        SyncArm();
    }

    internal void SyncArm()
    {
        var armed = CanArm && _owner.IsArmedTrack(Id);
        if (IsArmed == armed)
        {
            return;
        }

        _syncing = true;
        try
        {
            IsArmed = armed;
        }
        finally
        {
            _syncing = false;
        }
    }

    internal override void OpenInsert(DeviceId device) => _owner.OpenTrackChain(Id, device);

    /// <summary>Returns the fader to unity as its own undo step.</summary>
    [RelayCommand]
    private void ResetGain()
    {
        if (HasAudioStrip)
        {
            _owner.ExecuteAlone(AudioStripCommands.SetTrackGain(Id, 0));
        }
    }

    /// <summary>Centres the pan as its own undo step.</summary>
    [RelayCommand]
    private void ResetPan()
    {
        if (HasAudioStrip)
        {
            _owner.ExecuteAlone(AudioStripCommands.SetTrackPan(Id, 0));
        }
    }

    [RelayCommand]
    private void Select() => _owner.SelectTrack(Id);

    [RelayCommand]
    private void OpenChain() => _owner.OpenTrackChain(Id);

    [RelayCommand]
    private void AddSend()
    {
        if (NewSendTarget is not { } target || !HasAudioStrip)
        {
            return;
        }

        if (_owner.Execute(RoutingCommands.AddAudioSend(Id, target.Track, 0, false, _owner.Definitions)))
        {
            NewSendTarget = null;
        }

        OnPropertyChanged(nameof(CanAddSend));
    }

    internal void UpdateSend(SignalConnection send, TrackId target, double level, bool preFader, string? mergeKey) =>
        _owner.Execute(RoutingCommands.UpdateAudioSend(send.Id, target, level, preFader, _owner.Definitions, mergeKey));

    internal void ResetSendLevel(SignalConnection send, TrackId target) =>
        _owner.ExecuteAlone(RoutingCommands.UpdateAudioSend(send.Id, target, 0, send.SendPreFader, _owner.Definitions));

    internal void RemoveSend(ConnectionId send) => _owner.Execute(RoutingCommands.RemoveAudioSend(send));

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted)
    {
        if (target.SequenceEqual(wanted))
        {
            return;
        }

        target.Clear();
        foreach (var item in wanted)
        {
            target.Add(item);
        }
    }
}

/// <summary>One send from a track strip to an Effect return.</summary>
public sealed partial class MixerSendViewModel : ObservableObject
{
    private readonly MixerTrackStripViewModel _source;
    private bool _syncing;
    private SignalConnection _connection = SignalConnection.Create(SignalKind.Audio, SignalNode.Master, SignalNode.Master);

    internal MixerSendViewModel(MixerTrackStripViewModel source)
    {
        _source = source;
        MinimumLevel = AudioStrip.MinGainDecibels;
        MaximumLevel = AudioStrip.MaxGainDecibels;
    }

    public ObservableCollection<MixerEffectOption> Targets { get; } = [];

    public double MinimumLevel { get; }

    public double MaximumLevel { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelText))]
    public partial double Level { get; set; }

    [ObservableProperty]
    public partial bool IsPreFader { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetName), nameof(LevelAutomationName))]
    public partial MixerEffectOption? Target { get; set; }

    /// <summary>The destination Effect's name, for the send knob's caption.</summary>
    public string TargetName => Target?.Name ?? string.Empty;

    public string LevelAutomationName => $"Send to {TargetName} level";

    public string LevelText => MixerConsoleViewModel.FormatGain(Level);

    internal void Sync(SignalConnection connection, MixerEffectOption target, IReadOnlyList<MixerEffectOption> options)
    {
        _syncing = true;
        try
        {
            _connection = connection;
            Level = connection.SendLevelDecibels;
            IsPreFader = connection.SendPreFader;
            var targets = options.Contains(target) ? options : [.. options, target];
            if (!Targets.SequenceEqual(targets))
            {
                Targets.Clear();
                foreach (var option in targets)
                {
                    Targets.Add(option);
                }
            }

            Target = Targets.FirstOrDefault(option => option.Track == target.Track);
            OnPropertyChanged(nameof(LevelText));
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnLevelChanged(double value)
    {
        OnPropertyChanged(nameof(LevelText));
        Update($"mixer-send:{_connection.Id}:level");
    }

    partial void OnIsPreFaderChanged(bool value) => Update($"mixer-send:{_connection.Id}:tap");

    partial void OnTargetChanged(MixerEffectOption? value)
    {
        if (!_syncing && value is not null)
        {
            Update($"mixer-send:{_connection.Id}:target");
        }
    }

    [RelayCommand]
    private void Remove() => _source.RemoveSend(_connection.Id);

    /// <summary>Returns the send to 0 dB as its own undo step.</summary>
    [RelayCommand]
    private void ResetLevel()
    {
        if (Target is { } target)
        {
            _source.ResetSendLevel(_connection, target.Track);
        }
    }

    private void Update(string mergeKey)
    {
        if (!_syncing && Target is { } target)
        {
            _source.UpdateSend(_connection, target.Track, Math.Clamp(Level, MinimumLevel, MaximumLevel), IsPreFader, mergeKey);
        }
    }
}

/// <summary>The always-present, project-owned Master strip.</summary>
public sealed partial class MixerMasterStripViewModel : MixerMeterStripViewModel
{
    private readonly MixerConsoleViewModel _owner;
    private bool _syncing;

    internal MixerMasterStripViewModel(MixerConsoleViewModel owner)
    {
        _owner = owner;
        MinimumGain = AudioStrip.MinGainDecibels;
        MaximumGain = AudioStrip.MaxGainDecibels;
    }

    public double MinimumGain { get; }

    public double MaximumGain { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; internal set; }

    public string GainAutomationName { get; } = "Master gain";

    public string PanAutomationName { get; } = "Master pan";

    public string MuteAutomationName { get; } = "Mute Master";

    public string SoloAutomationName { get; } = "Solo Master";

    public string SelectAutomationName => IsSelected ? "Master, selected" : "Select Master";

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(SelectAutomationName));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText), nameof(GainValueText))]
    public partial double Gain { get; set; }

    public string GainText => MixerConsoleViewModel.FormatGain(Gain);

    /// <summary>The gain as a bare number for the readout under its "GAIN dB" label.</summary>
    public string GainValueText => MixerConsoleViewModel.FormatGainValue(Gain);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanText))]
    public partial double Pan { get; set; }

    public string PanText => MixerConsoleViewModel.FormatPan(Pan);

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsSoloed { get; set; }

    [ObservableProperty]
    public partial string ChainSummary { get; private set; } = "No devices";

    [RelayCommand]
    private void Select() => _owner.SelectMaster();

    // The Device View has no Master reveal yet, so opening a Master insert selects the Master (its inspector scope).
    internal override void OpenInsert(DeviceId device) => _owner.SelectMaster();

    /// <summary>Returns the Master fader to unity as its own undo step.</summary>
    [RelayCommand]
    private void ResetGain() => _owner.ExecuteAlone(AudioStripCommands.SetMasterGain(0));

    /// <summary>Centres the Master pan as its own undo step.</summary>
    [RelayCommand]
    private void ResetPan() => _owner.ExecuteAlone(AudioStripCommands.SetMasterPan(0));

    internal void Sync(Project project)
    {
        _syncing = true;
        try
        {
            Gain = project.MasterStrip.GainDecibels;
            Pan = project.MasterStrip.Pan;
            IsMuted = project.MasterStrip.IsMuted;
            IsSoloed = project.MasterStrip.IsSoloed;
            var chain = project.Chains.FirstOrDefault(candidate => candidate.Owner.Kind == ChainOwnerKind.Master);
            var count = chain?.Devices.Length ?? 0;
            ChainSummary = count == 0 ? "No devices" : $"{count} device{(count == 1 ? string.Empty : "s")}";
            SyncInserts(_owner, chain);
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnGainChanged(double value)
    {
        OnPropertyChanged(nameof(GainText));
        if (!_syncing)
        {
            _owner.Execute(AudioStripCommands.SetMasterGain(Math.Clamp(value, MinimumGain, MaximumGain)));
        }
    }

    partial void OnPanChanged(double value)
    {
        if (!_syncing)
        {
            _owner.Execute(AudioStripCommands.SetMasterPan(Math.Clamp(value, -1, 1)));
        }
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (!_syncing)
        {
            _owner.Execute(AudioStripCommands.SetMasterMuted(value));
        }
    }

    partial void OnIsSoloedChanged(bool value)
    {
        if (!_syncing)
        {
            _owner.Execute(AudioStripCommands.SetMasterSoloed(value));
        }
    }
}

/// <summary>One insert device on a mixer strip, in chain order.</summary>
public sealed partial class MixerInsertViewModel : ObservableObject
{
    private readonly MixerConsoleViewModel _console;
    private readonly MixerMeterStripViewModel _strip;
    private bool _syncing;

    internal MixerInsertViewModel(MixerConsoleViewModel console, MixerMeterStripViewModel strip)
    {
        _console = console;
        _strip = strip;
    }

    [ObservableProperty]
    public partial DeviceId DeviceId { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PowerAutomationName))]
    public partial string Name { get; private set; } = string.Empty;

    public string PowerAutomationName => $"{Name} power";

    /// <summary>True when the device processes; false when it is bypassed. Setting it bypasses or restores the device.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    internal void Sync(DeviceInstance device)
    {
        _syncing = true;
        try
        {
            DeviceId = device.Id;
            Name = device.DisplayName;
            IsEnabled = !device.IsBypassed;
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (_syncing)
        {
            return;
        }

        if (!_console.Execute(DeviceCommands.SetBypassed(DeviceId, !value, _console.Definitions)))
        {
            // The project refused the change, so the toggle shows the device's real state again.
            _syncing = true;
            try
            {
                IsEnabled = !value;
            }
            finally
            {
                _syncing = false;
            }
        }
    }

    /// <summary>Selects the strip and reveals its chain with this device selected.</summary>
    [RelayCommand]
    private void Open() => _strip.OpenInsert(DeviceId);
}
