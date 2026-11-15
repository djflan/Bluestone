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
    }

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

    public static IReadOnlyList<MixerMeterPresentationOption> MeterPresentations { get; } =
    [
        new(MixerMeterPresentation.Bar, "Bar"),
        new(MixerMeterPresentation.Round, "Round"),
    ];

    public static IReadOnlyList<int> VuReferenceChoices { get; } = Enumerable.Range(-36, 37).ToArray();

    /// <summary>Legacy peak/RMS source for isolated projections that are not attached to an audio API.</summary>
    public Func<TrackId?, MeterReading?>? MeterSource { get; set; }

    [ObservableProperty]
    public partial MixerMeterMode MeterMode { get; set; } = MixerMeterMode.PeakRms;

    [ObservableProperty]
    public partial MixerMeterPresentation MeterPresentation { get; set; } = MixerMeterPresentation.Bar;

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
            MeterPresentation = settings.Presentation;
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

            row.Sync(project, wanted[i]);
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

    partial void OnMeterPresentationChanged(MixerMeterPresentation value)
    {
        if (!Enum.IsDefined(value))
        {
            MeterPresentation = MixerMeterPresentation.Bar;
            return;
        }

        SaveSettings();
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
    private void SetMeterPresentation(MixerMeterPresentation presentation) => MeterPresentation = presentation;

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
                Presentation = MeterPresentation,
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

    internal void Post(Action action) => _owner.Post(action);

    internal TrackViewModel? TrackRow(TrackId id) => _owner.Tracks.FirstOrDefault(track => track.Id == id);

    internal void SelectTrack(TrackId id)
    {
        if (_owner.Tracks.FirstOrDefault(track => track.Id == id) is { } track)
        {
            _owner.Select(track);
        }
    }

    internal void OpenTrackChain(TrackId id)
    {
        SelectTrack(id);
        _owner.Workspace.RevealSelectedChain();
    }

    internal static string FormatGain(double decibels) =>
        string.Create(CultureInfo.InvariantCulture, $"{decibels:+0.0;-0.0;0.0} dB");
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
    public partial double MeterLeftPeakHold { get; private set; } = MeterLevels.FloorDecibels;

    [ObservableProperty]
    public partial double MeterRightPeakHold { get; private set; } = MeterLevels.FloorDecibels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMeterWarmingUp), nameof(MeterStatus))]
    public partial bool IsMeterReady { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMeterWarmingUp), nameof(MeterStatus))]
    public partial bool HasMeterReading { get; private set; }

    public bool IsMeterWarmingUp => HasMeterReading && MeterMode == MixerMeterMode.LufsS && !IsMeterReady;

    public string MeterStatus => !HasMeterReading ? "Unavailable" : IsMeterWarmingUp ? "Warming up" : string.Empty;

    internal void UpdateMeter(MixerMeterReading? reading, MixerMeterMode fallbackMode, long timestamp)
    {
        Meter.Update(reading?.PeakRms, timestamp);
        MeterLeftPeakHold = Meter.HoldLeft;
        MeterRightPeakHold = Meter.HoldRight;
        MeterMode = reading?.Mode ?? fallbackMode;
        MeterLeftValue = reading?.LeftValue ?? double.NaN;
        MeterRightValue = reading?.RightValue ?? double.NaN;
        IsMeterReady = reading?.IsReady ?? false;
        HasMeterReading = reading is not null;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText), nameof(AccessibilitySummary))]
    public partial string Name { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibilitySummary))]
    public partial TrackRole Role { get; private set; }

    public string RoleLabel => Role switch
    {
        TrackRole.Group => "GROUP",
        TrackRole.Effect => "EFFECT",
        _ => string.Empty,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibilitySummary))]
    public partial bool HasAudioStrip { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibilitySummary))]
    public partial string Status { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibilitySummary))]
    public partial string OutputSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibilitySummary))]
    public partial string ChainSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText), nameof(AccessibilitySummary))]
    public partial double Gain { get; set; }

    public string GainText => MixerConsoleViewModel.FormatGain(Gain);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanText))]
    public partial double Pan { get; set; }

    public string PanText => Math.Abs(Pan) < 0.005 ? "C" : string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(Pan):0.00} {(Pan < 0 ? "L" : "R")}");

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsSoloed { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; internal set; }

    [ObservableProperty]
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

    internal void Sync(Project project, Track track)
    {
        _syncing = true;
        try
        {
            Name = track.Name;
            Role = track.Role;
            HasAudioStrip = track.AudioStrip is not null;
            var trackRow = _owner.TrackRow(track.Id);
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

            OnPropertyChanged(nameof(RoleLabel));
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
    public partial MixerEffectOption? Target { get; set; }

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText))]
    public partial double Gain { get; set; }

    public string GainText => MixerConsoleViewModel.FormatGain(Gain);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanText))]
    public partial double Pan { get; set; }

    public string PanText => Math.Abs(Pan) < 0.005 ? "C" : string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(Pan):0.00} {(Pan < 0 ? "L" : "R")}");

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsSoloed { get; set; }

    [ObservableProperty]
    public partial string ChainSummary { get; private set; } = "No devices";

    [RelayCommand]
    private void Select() => _owner.SelectMaster();

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
