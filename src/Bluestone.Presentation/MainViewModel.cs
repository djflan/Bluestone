using System.Collections.ObjectModel;
using System.Globalization;
using Bluestone.Application.Editing;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Infrastructure.Projects;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Files;
using Bluestone.Playback;
using Bluestone.Profiles;
using Bluestone.Signal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

public enum MessageSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>The tabs of the lower pane.</summary>
public enum LowerPane
{
    PianoRoll,
    EventList,
    Monitor,
    Messages,
}

/// <summary>An input choice; <see cref="Id"/> is null for "all inputs".</summary>
public sealed record InputOption(EndpointId? Id, string Name)
{
    public static readonly InputOption All = new(null, "All MIDI Inputs");

    public override string ToString() => Name;
}

/// <summary>A line in the messages panel. Severity is always also spelled out in text.</summary>
public sealed record MessageItem(MessageSeverity Severity, string Source, string Text)
{
    public string SeverityText => Severity.ToString();
}

/// <summary>
/// The main window's state and commands. A thin client of <see cref="ProjectSession"/> and
/// <see cref="PlaybackController"/>: it owns no musical data and never schedules MIDI.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxMessages = 500;
    private const int MaxMonitorEntries = 400;

    private readonly ProjectSession _session;
    private readonly PlaybackController _playback;
    private readonly EndpointDirectory _endpoints;
    private readonly IUserInteraction _ui;
    private readonly PluginDeviceHost? _plugins;
    private readonly IUiDispatcher _dispatcher;
    private readonly MidiMonitor? _monitor;
    private IReadOnlyList<EndpointDescriptor> _outputDescriptors = [];
    private int _refreshing;
    private int _refreshAgain;
    private long _lastInputCount;
    private int _inputActivityFrames;

    public MainViewModel(ProjectSession session, PlaybackController playback, EndpointDirectory endpoints, IUserInteraction ui, IUiDispatcher dispatcher, MidiMonitor? monitor = null, ComputerKeyboardViewModel? keyboard = null, PluginDeviceHost? plugins = null)
    {
        Keyboard = keyboard;
        _plugins = plugins;
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _monitor = monitor;
        Project = session.Project;
        Selection = new SelectionViewModel(this);
        DeviceStrip = new DeviceStripViewModel(this);
        Connections = new ConnectionsViewModel(this);
        Racks = new RacksViewModel(this);
        Mixer = new MixerViewModel(this);
        Editor = new EditorViewModel(this);
        Arrangement = new ArrangementViewModel(this);
        Groups = new GroupsViewModel(this);
        EventList = new EventListViewModel(this, Editor);
        SelectedTracks.CollectionChanged += (_, _) =>
        {
            DeviceStrip.LeaveRack();
            OnPropertyChanged(nameof(SelectedTrack));
            SyncSelection();
            SyncEditor();
            Arrangement.KeepOnTracks([.. SelectedTracks.Select(t => t.Id)]);
            Groups.SyncSelection();
            ApplyThru();
        };
        ApplyMetronome();
        if (keyboard is not null)
        {
            keyboard.ChannelSource = () => (_playback.Recorder.RecordingTrack ?? RecordTarget?.Id) is { } track ? _playback.ChannelFor(track).Index : 0;
            keyboard.RefreshChannel();
        }

        // Session work may finish on a thread-pool thread; view state is only touched on the UI thread.
        _session.Changed += (_, _) => _dispatcher.Run(() =>
        {
            OnSessionChanged();
            Keyboard?.RefreshChannel();
        });
        _endpoints.EndpointsChanged += (_, _) => _dispatcher.Post(() => _ = RefreshAsync());
        if (_plugins is not null)
        {
            _playback.UseDevices(_plugins.Extend(DeviceCatalog.BuiltIn));
            // A plugin MIDI effect that stops or comes back changes what goes into the plan, so recompile it too.
            _plugins.StatusChanged += (_, device) => _dispatcher.Post(() =>
            {
                SyncSelection();
                if (_plugins.ShapesThePlan(device))
                {
                    _ = RefreshAsync();
                }
            });

            // A parameter the plugin changed itself (in its editor) is stored in the project, which stays the authority.
            _plugins.ParameterEdited += (_, edit) => _dispatcher.Post(() => _plugins.RecordEdit(edit));

            // A rescan that finds or loses plugins changes the catalog; devices whose plugin was found start.
            _plugins.AvailableChanged += (_, _) => _dispatcher.Post(() => _ = OnPluginsChangedAsync());
        }

        foreach (var failure in _playback.Profiles.Failures)
        {
            AddMessage(MessageSeverity.Warning, "Profiles", $"{Path.GetFileName(failure.Source)} was not loaded: {(failure.Diagnostics.Count > 0 ? failure.Diagnostics[0].ToString() : "unknown error")}");
        }
    }

    public ObservableCollection<TrackViewModel> Tracks { get; } = [];

    public ObservableCollection<OutputOption> Outputs { get; } = [];

    public ObservableCollection<MessageItem> Messages { get; } = [];

    public ObservableCollection<MonitorEntry> MonitorEntries { get; } = [];

    public string? MonitorName => _monitor?.EndpointName;

    [ObservableProperty]
    public partial Project Project { get; private set; }

    /// <summary>
    /// The selected tracks, in selection order. The list view binds to this collection directly, so
    /// changes made here (select all, add track) are reflected in the UI and vice versa.
    /// </summary>
    public ObservableCollection<TrackViewModel> SelectedTracks { get; } = [];

    /// <summary>The inspector for <see cref="SelectedTracks"/>.</summary>
    public SelectionViewModel Selection { get; }

    /// <summary>The piano roll for the first selected track.</summary>
    public EditorViewModel Editor { get; }

    /// <summary>The clip selection and clip edits in the arrangement.</summary>
    public ArrangementViewModel Arrangement { get; }

    /// <summary>Group tracks: grouping the selection, membership, and folding.</summary>
    public GroupsViewModel Groups { get; }

    /// <summary>The selected track's device chain.</summary>
    public DeviceStripViewModel DeviceStrip { get; }

    /// <summary>The selected track's (or edited rack's) connections: the routing inspector.</summary>
    public ConnectionsViewModel Connections { get; }

    /// <summary>The project's racks: shared device chains.</summary>
    public RacksViewModel Racks { get; }

    /// <summary>The project's mixer channels.</summary>
    public MixerViewModel Mixer { get; }

    /// <summary>The plugins view: scanned plugins, failures, and quarantined modules. Null when the host does not offer it.</summary>
    public PluginsViewModel? Plugins { get; init; }

    /// <summary>The devices chains can use: built-ins, and plugins as data (they run in worker processes).</summary>
    internal DeviceCatalog Devices => _playback.Devices;

    internal IUserInteraction UserInteraction => _ui;

    /// <summary>A device's status for the strip: a plugin's from its worker, otherwise from the catalog.</summary>
    internal DeviceStatus DeviceStatus(DeviceInstance device) =>
        _plugins?.StatusOf(device) is { } runtime
            ? new DeviceStatus(runtime.Text, runtime.NeedsAttention, runtime.CanRestart, runtime.CanOpenEditor)
            : Presentation.DeviceStatus.Of(device, Devices);

    /// <summary>Restarts a crashed, hung, or quarantined plugin device and restores its last saved state.</summary>
    internal async Task RestartDeviceAsync(DeviceId device)
    {
        if (_plugins is null)
        {
            return;
        }

        try
        {
            if (!await _plugins.RestartAsync(device))
            {
                AddMessage(MessageSeverity.Warning, "Plugins", "The plugin did not start again. Its settings are kept; try again, or remove the device.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddMessage(MessageSeverity.Warning, "Plugins", $"The plugin could not be restarted: {ex.Message}");
        }

        SyncSelection();
    }

    /// <summary>Opens a plugin device's own editor in a window of its worker process.</summary>
    internal async Task OpenDeviceEditorAsync(DeviceId device)
    {
        if (_plugins is null)
        {
            return;
        }

        if (await _plugins.OpenEditorAsync(device) is { } problem)
        {
            AddMessage(MessageSeverity.Warning, "Plugins", $"The plugin's editor could not be opened: {problem}");
        }

        SyncSelection();
    }

    // Starts workers for plugin devices that need one and stops those whose device is gone. A failure is a message,
    // never a crash and never a fall-back to Bluestone's own process.
    private async Task SyncPluginsAsync()
    {
        if (_plugins is null)
        {
            return;
        }

        try
        {
            await _plugins.SyncAsync(Project);
            _playback.UseDevices(_plugins.Extend(DeviceCatalog.BuiltIn));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddMessage(MessageSeverity.Warning, "Plugins", $"Plugins could not be started: {ex.Message}");
        }
    }

    private async Task OnPluginsChangedAsync()
    {
        await SyncPluginsAsync();
        SyncSelection();
        await RefreshAsync();
    }

    /// <summary>
    /// Changes the role of every selected track. A change that needs consent is asked about first; a refused one
    /// is explained and nothing changes. Clips, devices, automation, and routing are never discarded (ADR 0021).
    /// </summary>
    internal async Task ChangeRoleAsync(TrackRole role)
    {
        foreach (var track in SelectedTracks.Select(t => t.Id).ToList())
        {
            var plan = TrackRoleConversion.ForRoleChange(Project, track, role, Devices.Lookup);
            switch (plan.Outcome)
            {
                case RoleChangeOutcome.Refused:
                    AddMessage(MessageSeverity.Warning, "Track Role", plan.Message);
                    break;
                case RoleChangeOutcome.NeedsConfirmation:
                    if (await _ui.ConfirmAsync("Change Track Role", plan.Message, "Change", destructive: false))
                    {
                        Execute(TrackRoleCommands.SetRole(track, role, Devices.Lookup, confirmed: true));
                    }

                    break;
                case RoleChangeOutcome.Automatic:
                    Execute(TrackRoleCommands.SetRole(track, role, Devices.Lookup));
                    break;
            }
        }

        SyncSelection();
    }

    /// <summary>The tracks whose automation lanes are shown, so the arrangement can lay out its rows.</summary>
    [ObservableProperty]
    public partial IReadOnlySet<TrackId> ExpandedTracks { get; private set; } = new HashSet<TrackId>();

    /// <summary>The event list for the first selected track.</summary>
    public EventListViewModel EventList { get; }

    /// <summary>Plays notes from the computer keyboard, or null when the host does not offer it.</summary>
    public ComputerKeyboardViewModel? Keyboard { get; }

    public ObservableCollection<InputOption> Inputs { get; } = [InputOption.All];

    /// <summary>Choices for the metronome output: <see cref="OutputOption.None"/> means the record or selected track's output.</summary>
    public ObservableCollection<OutputOption> MetronomeOutputs { get; } = [];

    /// <summary>The first selected track, or null.</summary>
    public TrackViewModel? SelectedTrack => SelectedTracks.Count > 0 ? SelectedTracks[0] : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial string ProjectName { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial bool IsDirty { get; private set; }

    public string WindowTitle => $"{ProjectName}{(IsDirty ? " — edited" : string.Empty)} · Bluestone";

    [ObservableProperty]
    public partial bool IsPlaying { get; private set; }

    [ObservableProperty]
    public partial long PlayheadTick { get; private set; }

    [ObservableProperty]
    public partial string PositionText { get; private set; } = "1.1.000";

    [ObservableProperty]
    public partial string TimeText { get; private set; } = "0:00.000";

    [ObservableProperty]
    public partial string TempoText { get; private set; } = "120.0";

    [ObservableProperty]
    public partial string MeterText { get; private set; } = "4/4";

    [ObservableProperty]
    public partial bool IsLoopEnabled { get; private set; }

    [ObservableProperty]
    public partial string TimingText { get; private set; } = "Idle";

    [ObservableProperty]
    public partial string OutputsText { get; private set; } = string.Empty;

    public string PlayButtonText => IsPlaying ? "Pause" : "Play";

    [ObservableProperty]
    public partial bool IsRecording { get; private set; }

    [ObservableProperty]
    public partial bool IsCountingIn { get; private set; }

    /// <summary>True for a moment after MIDI input arrives, for the activity indicator.</summary>
    [ObservableProperty]
    public partial bool HasInputActivity { get; private set; }

    /// <summary>
    /// Keys to show pressed on the piano roll, one bit per note number: the edited track's notes
    /// sounding at the playhead, and notes held down on MIDI inputs.
    /// </summary>
    [ObservableProperty]
    public partial UInt128 SoundingNotes { get; private set; }

    /// <summary>Notes of the take being recorded, for drawing live.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Application.Recording.RecordingNote> RecordingPreview { get; private set; } = [];

    /// <summary>Index of the track being recorded, or -1.</summary>
    [ObservableProperty]
    public partial int RecordingLane { get; private set; } = -1;

    /// <summary>The track armed for recording; when none is armed, recording goes to the selected track.</summary>
    [ObservableProperty]
    public partial TrackViewModel? ArmedTrack { get; private set; }

    [ObservableProperty]
    public partial bool IsMetronomeEnabled { get; set; } = true;

    /// <summary>Click during playback as well as while recording.</summary>
    [ObservableProperty]
    public partial bool ClickWhilePlaying { get; set; }

    [ObservableProperty]
    public partial OutputOption? MetronomeOutput { get; set; }

    [ObservableProperty]
    public partial bool IsCountInEnabled { get; set; } = true;

    /// <summary>Bars of count-in when <see cref="IsCountInEnabled"/>: 1 or 2.</summary>
    [ObservableProperty]
    public partial int CountInBars { get; set; } = 1;

    /// <summary>Replace what is under a take instead of merging with it.</summary>
    [ObservableProperty]
    public partial bool IsReplaceRecording { get; set; }

    /// <summary>Echo MIDI input to the armed or selected track's output.</summary>
    [ObservableProperty]
    public partial bool IsThruEnabled { get; set; } = true;

    [ObservableProperty]
    public partial InputOption? SelectedInput { get; set; } = InputOption.All;

    [ObservableProperty]
    public partial bool IsInspectorVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool IsEditorVisible { get; set; } = true;

    [ObservableProperty]
    public partial LowerPane LowerPane { get; set; } = LowerPane.PianoRoll;

    partial void OnIsEditorVisibleChanged(bool value) => EventList.IsActive = value && LowerPane == LowerPane.EventList;

    partial void OnLowerPaneChanged(LowerPane value) => EventList.IsActive = IsEditorVisible && value == LowerPane.EventList;

    partial void OnIsMetronomeEnabledChanged(bool value) => ApplyMetronome();

    partial void OnClickWhilePlayingChanged(bool value) => ApplyMetronome();

    partial void OnMetronomeOutputChanged(OutputOption? value)
    {
        ApplyMetronome();
        _ = RefreshAsync();
    }

    partial void OnIsThruEnabledChanged(bool value) => ApplyThru();

    partial void OnSelectedInputChanged(InputOption? value)
    {
        _playback.SelectInputs(value?.Id is { } id ? [id] : null);
        _ = RefreshAsync();
    }

    partial void OnCountInBarsChanged(int value)
    {
        var clamped = Math.Clamp(value, 1, 4);
        if (clamped != value)
        {
            CountInBars = clamped;
        }
    }

    [RelayCommand]
    private void ToggleMetronome() => IsMetronomeEnabled = !IsMetronomeEnabled;

    [RelayCommand]
    private void ToggleCountIn() => IsCountInEnabled = !IsCountInEnabled;

    [RelayCommand]
    private void ToggleInspector() => IsInspectorVisible = !IsInspectorVisible;

    /// <summary>Shows a lower pane tab, or hides the pane if that tab is already showing.</summary>
    [RelayCommand]
    private void ShowPane(LowerPane pane)
    {
        if (IsEditorVisible && LowerPane == pane)
        {
            IsEditorVisible = false;
            return;
        }

        LowerPane = pane;
        IsEditorVisible = true;
    }

    [RelayCommand]
    private void ToggleEditor() => IsEditorVisible = !IsEditorVisible;

    public const double MinZoom = 6;
    public const double MaxZoom = 160;
    public const double ZoomStep = 1.25;

    /// <summary>Timeline scale in pixels per quarter note, shared by the slider, menu, keys, and gestures.</summary>
    [ObservableProperty]
    public partial double Zoom { get; set; } = 40;

    partial void OnZoomChanged(double value)
    {
        var clamped = Math.Clamp(value, MinZoom, MaxZoom);
        if (clamped != value)
        {
            Zoom = clamped;
        }
    }

    /// <summary>Multiplies the zoom by <paramref name="factor"/>, staying within range.</summary>
    public void ZoomBy(double factor) => Zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);

    [RelayCommand]
    private void ZoomIn() => ZoomBy(ZoomStep);

    [RelayCommand]
    private void ZoomOut() => ZoomBy(1 / ZoomStep);

    /// <summary>Loads routes and outputs for the initial project.</summary>
    public Task InitializeAsync() => SyncAndRefreshAsync();

    /// <summary>Runs an edit. An edit the model refuses (it would break routing or hide content) changes nothing and is explained in a message.</summary>
    internal bool Execute(IProjectCommand command)
    {
        try
        {
            return _session.Execute(command);
        }
        catch (CommandRefusedException ex)
        {
            AddMessage(MessageSeverity.Warning, command.Label, ex.Message);

            // Controls bound two ways still show the refused value; show the project's again.
            SyncSelection();
            return false;
        }
    }

    /// <summary>The channel a track's output sends on, when it overrides the track's own.</summary>
    internal MidiChannel? RouteChannel(TrackId track) => TrackOutputs.Read(Project, track).Channel;

    internal void OnTrackLayoutChanged()
    {
        var expanded = Tracks.Where(t => t.IsAutomationExpanded && t.Lanes.Count > 0).Select(t => t.Id).ToHashSet();
        if (!expanded.SetEquals(ExpandedTracks))
        {
            ExpandedTracks = expanded;
        }
    }

    /// <summary>Replaces an automation lane's points as one undo step, e.g. after a point is dragged in the arrangement.</summary>
    public void SetAutomationPoints(TrackId track, AutomationLaneId lane, string label, IReadOnlyCollection<AutomationPoint> points) =>
        Execute(AutomationCommands.SetPoints(track, lane, label, points));

    /// <summary>Adds an automation lane to a track, on its route's channel or else its first channel, and shows its lanes.</summary>
    internal void AddAutomationLane(TrackViewModel track, AutomationOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        var channel = track.Route.Channel ?? Project.Sequence.FindTrack(track.Id)?.FirstChannel ?? MidiChannel.FromIndex(0);
        Execute(AutomationCommands.AddLane(track.Id, AutomationLane.Create(option.On(channel))));
        track.IsAutomationExpanded = true;
    }

    internal EndpointDescriptor? FindEndpoint(EndpointId id) => _outputDescriptors.FirstOrDefault(e => e.Id == id);

    internal DeviceProfile? FindProfile(string id) => _playback.Profiles.Find(id);

    /// <summary>Applies a routing change to every selected track as one undoable step.</summary>
    internal void ApplyToSelection(string label, Func<TrackOutput, TrackOutput> change)
    {
        var tracks = SelectedTracks.ToList();
        if (tracks.Count == 0)
        {
            return;
        }

        var commands = tracks.Select(t => ProjectCommands.SetTrackOutput(t.Id, change(TrackOutputs.Read(Project, t.Id))));
        Execute(ProjectCommands.Batch(tracks.Count == 1 ? label : $"{label} ({tracks.Count} Tracks)", commands));
    }

    /// <summary>Makes <paramref name="track"/> the only selected track.</summary>
    public void Select(TrackViewModel track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (SelectedTracks.Count == 1 && SelectedTracks[0] == track)
        {
            return;
        }

        SelectedTracks.Clear();
        SelectedTracks.Add(track);
    }

    /// <summary>Moves the selection up or down; with <paramref name="extend"/>, adds the next track instead.</summary>
    public void SelectAdjacent(int delta, bool extend)
    {
        if (Tracks.Count == 0)
        {
            return;
        }

        // Rows folded away under a group are skipped.
        var shown = Tracks.Where(t => t.IsShown || SelectedTracks.Contains(t)).ToList();
        var anchor = SelectedTracks.Count > 0 ? SelectedTracks[^1] : null;
        var index = anchor is null ? (delta > 0 ? 0 : shown.Count - 1) : Math.Clamp(shown.IndexOf(anchor) + delta, 0, shown.Count - 1);
        var target = shown[index];
        if (extend)
        {
            if (!SelectedTracks.Contains(target))
            {
                SelectedTracks.Add(target);
            }
        }
        else
        {
            Select(target);
        }
    }

    /// <summary>Polls engine state for display. Called by a UI timer (about 30 Hz); never schedules MIDI.</summary>
    public void OnFrame()
    {
        var engine = _playback.Engine;
        var sequence = Project.Sequence;
        var tick = engine.Position;
        IsPlaying = engine.State == TransportState.Playing;
        OnPropertyChanged(nameof(PlayButtonText));
        IsRecording = _playback.Recorder.IsRecording;
        IsCountingIn = engine.IsCountingIn;
        if (IsRecording && _playback.Recorder.RecordingTrack is { } recording)
        {
            RecordingLane = Tracks.IndexOf(Tracks.FirstOrDefault(t => t.Id == recording)!);
            RecordingPreview = _playback.Recorder.Preview(tick, Project.Loop);
        }
        else if (RecordingLane >= 0 || RecordingPreview.Count > 0)
        {
            RecordingLane = -1;
            RecordingPreview = [];
        }

        var received = _playback.Recorder.MessagesReceived;
        if (received != _lastInputCount)
        {
            _lastInputCount = received;
            _inputActivityFrames = 4;
        }

        HasInputActivity = _inputActivityFrames-- > 0;
        PlayheadTick = tick.Value;
        SoundingNotes = _playback.Recorder.HeldNotes
            | (IsPlaying && !IsCountingIn && Editor.Track is { } edited && IsAudible(sequence, edited) ? NotesAt(edited, tick.Value) : 0);
        PositionText = Formatting.Position(sequence.MeterMap.ToBarBeatTick(tick));
        TimeText = Formatting.Time(sequence.TempoMap.TimeAt(tick));
        TempoText = Formatting.Tempo(sequence.TempoMap.TempoAt(tick));
        MeterText = sequence.MeterMap.SignatureAt(tick).ToString();

        var stats = engine.Statistics.Snapshot();
        TimingText = stats.Dispatched == 0
            ? "No messages sent yet"
            : string.Create(CultureInfo.InvariantCulture, $"{stats.Dispatched} sent · p95 ≤ {Formatting.Milliseconds(stats.Percentile(95))} · {stats.Late} late · {stats.Dropped} dropped");

        if (_monitor is not null)
        {
            foreach (var entry in _monitor.Drain())
            {
                MonitorEntries.Add(entry);
            }

            while (MonitorEntries.Count > MaxMonitorEntries)
            {
                MonitorEntries.RemoveAt(0);
            }
        }
    }

    /// <summary>The pitches of <paramref name="track"/>'s notes that sound at <paramref name="tick"/>, one bit per note number.</summary>
    internal static UInt128 NotesAt(Track track, long tick)
    {
        UInt128 notes = 0;
        foreach (var e in track.ArrangedEvents)
        {
            if (e.Position.Value > tick)
            {
                break;
            }

            if (e is NoteEvent note && note.EndPosition.Value > tick && note.Velocity.IsAudible)
            {
                notes |= UInt128.One << note.Note.Value;
            }
        }

        return notes;
    }

    private static bool IsAudible(Sequence sequence, Track track) => GroupTracks.Plays(sequence, track);

    public Task AutosaveAsync() => _session.AutosaveAsync();

    /// <summary>Asks about unsaved changes before the window closes. Returns false to keep it open.</summary>
    public async Task<bool> ConfirmCloseAsync() => await ResolveUnsavedChangesAsync();

    public void SeekTo(long tick)
    {
        var target = new Tick(Math.Max(0, tick));
        if (IsPlaying)
        {
            _playback.Seek(target);
        }
        else
        {
            _playback.Engine.Seek(target);
        }

        PlayheadTick = target.Value;
    }

    [RelayCommand]
    private async Task NewProjectAsync()
    {
        if (await ResolveUnsavedChangesAsync())
        {
            _playback.Stop();
            _session.New();
        }
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (await ResolveUnsavedChangesAsync() && await _ui.PickOpenFileAsync("Open Project", [FileFilters.Project]) is { } path)
        {
            await OpenProjectAsync(path);
        }
    }

    /// <summary>True for files Bluestone can open by dropping them on the window: projects and MIDI files.</summary>
    public static bool CanOpenFile(string path) => IsProject(path) || IsMidi(path);

    /// <summary>
    /// Opens a dropped project, or imports a dropped MIDI file, after resolving unsaved changes.
    /// </summary>
    public async Task OpenFileAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!CanOpenFile(path))
        {
            AddMessage(MessageSeverity.Warning, "Open", $"{Path.GetFileName(path)} is not a Bluestone project or MIDI file.");
            return;
        }

        if (!await ResolveUnsavedChangesAsync())
        {
            return;
        }

        if (IsProject(path))
        {
            await OpenProjectAsync(path);
        }
        else
        {
            await ImportMidiFileAsync(path);
        }
    }

    private static bool IsProject(string path) => HasExtension(path, FileFilters.Project);

    private static bool IsMidi(string path) => HasExtension(path, FileFilters.Midi);

    private static bool HasExtension(string path, FileFilter filter) =>
        filter.Extensions.Any(e => Path.GetExtension(path).Equals("." + e, StringComparison.OrdinalIgnoreCase));

    private Task OpenProjectAsync(string path) => RunAsync("Open", async () =>
    {
        _playback.Stop();
        var report = await _session.OpenAsync(path);
        if (report.RecoveredFromBackup)
        {
            AddMessage(MessageSeverity.Warning, "Open", $"The project file was damaged ({report.Problem}). Bluestone opened the backup copy; save to repair the file.");
        }

        if (report.RecoveryAvailable && await _ui.ConfirmAsync("Restore unsaved work?", "Bluestone found autosaved changes newer than this project. Restore them?", "Restore", destructive: false))
        {
            await _session.RestoreRecoveryAsync();
            AddMessage(MessageSeverity.Info, "Open", "Restored autosaved changes. Save to keep them.");
        }
    });

    [RelayCommand]
    private Task SaveAsync() => _session.FilePath is null ? SaveAsAsync() : RunAsync("Save", async () =>
    {
        await CapturePluginStateAsync();
        await _session.SaveAsync();
    });

    // Plugins hold their own state while they run; it goes into the project before the project is written.
    private Task CapturePluginStateAsync() => _plugins?.CaptureAllAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (await _ui.PickSaveFileAsync("Save Project", ProjectName + ".bluestone", FileFilters.Project) is { } path)
        {
            await RunAsync("Save", async () =>
            {
                await CapturePluginStateAsync();
                await _session.SaveAsync(path);
            });
        }
    }

    [RelayCommand]
    private async Task ImportMidiAsync()
    {
        if (await ResolveUnsavedChangesAsync() && await _ui.PickOpenFileAsync("Import MIDI File", [FileFilters.Midi]) is { } path)
        {
            await ImportMidiFileAsync(path);
        }
    }

    private Task ImportMidiFileAsync(string path) => RunAsync("Import", async () =>
    {
        _playback.Stop();
        var report = await _session.ImportMidiAsync(path);
        ReportMidi("Import", report.ImportDiagnostics);
        AddMessage(MessageSeverity.Info, "Import", $"Imported {Path.GetFileName(path)} with {_session.Project.Sequence.Tracks.Length} tracks. Choose an output for each track to hear it.");
    });

    [RelayCommand]
    private async Task ExportMidiAsync()
    {
        if (await _ui.PickSaveFileAsync("Export MIDI File", ProjectName + ".mid", FileFilters.Midi) is { } path)
        {
            await RunAsync("Export", async () =>
            {
                ReportMidi("Export", await _session.ExportMidiAsync(path));
                AddMessage(MessageSeverity.Info, "Export", $"Exported {Path.GetFileName(path)}.");
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => _session.History.Undo();

    private bool CanUndo() => _session.History.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => _session.History.Redo();

    private bool CanRedo() => _session.History.CanRedo;

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        if (_playback.Engine.State == TransportState.Playing)
        {
            StopTransport();
            return;
        }

        await RunAsync("Play", () => _playback.PlayAsync());
    }

    /// <summary>Stops; when already stopped, returns to the start.</summary>
    [RelayCommand]
    private void Stop()
    {
        if (_playback.Engine.State == TransportState.Playing)
        {
            StopTransport();
        }
        else
        {
            SeekTo(0);
        }
    }

    /// <summary>The track a new take goes to: the armed track, else the first selected track.</summary>
    public TrackViewModel? RecordTarget => ArmedTrack ?? SelectedTrack;

    /// <summary>
    /// Starts recording on the armed (or selected) track: from a stop with the count-in, or punching in
    /// while playing. While recording, punches out and keeps playing.
    /// </summary>
    [RelayCommand]
    private async Task RecordAsync()
    {
        if (_playback.Recorder.IsRecording)
        {
            ReportTake(_playback.FinishRecording());
            return;
        }

        if (RecordTarget is not { } target)
        {
            AddMessage(MessageSeverity.Warning, "Record", "Add or select a track to record into.");
            return;
        }

        if (target.IsOffline)
        {
            AddMessage(MessageSeverity.Info, "Record", $"\"{target.Name}\" has no output, so you will not hear what you play. Choose an output in the inspector.");
        }

        var options = new RecordOptions(IsCountInEnabled ? CountInBars : 0, IsReplaceRecording);
        await RunAsync("Record", () => _playback.RecordAsync(target.Id, options));
        ApplyThru();
        IsRecording = true;
    }

    /// <summary>Arms <paramref name="track"/> for recording, or disarms it if it is armed.</summary>
    public void ToggleArm(TrackViewModel track)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArmedTrack = ArmedTrack == track ? null : track;
        foreach (var t in Tracks)
        {
            t.IsArmed = t == ArmedTrack;
        }

        ApplyThru();
    }

    [RelayCommand]
    private void ToggleArmSelected()
    {
        if (SelectedTrack is { } track)
        {
            ToggleArm(track);
        }
    }

    /// <summary>Sets the tempo at the playhead's tempo segment from text such as "96" or "132.5".</summary>
    public void CommitTempo(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm) || bpm is < 10 or > 999)
        {
            OnPropertyChanged(nameof(TempoText));
            return;
        }

        var tempo = Project.Sequence.TempoMap;
        var at = tempo.Changes.Last(c => c.Position.Value <= PlayheadTick).Position;
        Execute(ProjectCommands.SetTempo(at, Tempo.FromBeatsPerMinute(bpm)));
        TempoText = Formatting.Tempo(Tempo.FromBeatsPerMinute(bpm));
    }

    /// <summary>Sets the time signature at the playhead's meter segment from text such as "3/4".</summary>
    public void CommitMeter(string text)
    {
        var parts = text.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var numerator)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var denominator)
            || numerator is < 1 or > 32 || denominator is not (2 or 4 or 8 or 16)
            || !new TimeSignature(numerator, denominator).TryGetTicksPerBeat(Project.Sequence.Ppqn, out _))
        {
            OnPropertyChanged(nameof(MeterText));
            return;
        }

        var meter = Project.Sequence.MeterMap;
        var at = meter.Changes.Last(c => c.Position.Value <= PlayheadTick).Position;
        Execute(ProjectCommands.SetTimeSignature(at, new TimeSignature(numerator, denominator)));
    }

    internal void Audition(TrackId track, Domain.Midi.NoteNumber note, Domain.Midi.Velocity velocity) => _playback.Audition(track, note, velocity);

    internal void PlayKey(TrackId track, Domain.Midi.NoteNumber note, Domain.Midi.Velocity velocity) => _playback.PlayKey(track, note, velocity);

    internal void EndAudition() => _playback.EndAudition();

    private void StopTransport()
    {
        var recording = _playback.Recorder.IsRecording;
        var take = _playback.Stop();
        if (recording)
        {
            ReportTake(take);
        }
    }

    private void ReportTake(Application.Recording.RecordedTake? take)
    {
        IsRecording = false;
        RecordingPreview = [];
        RecordingLane = -1;
        if (take is null)
        {
            return;
        }

        var notes = take.Events.Count(e => e is NoteEvent);
        var others = take.Events.Length - notes;
        var name = Tracks.FirstOrDefault(t => t.Id == take.Track)?.Name ?? "the track";
        if (take.NotAdded is { } reason)
        {
            AddMessage(MessageSeverity.Warning, "Record", $"The take was not added to {name}: {reason}");
            return;
        }

        AddMessage(MessageSeverity.Info, "Record", take.Events.IsEmpty
            ? "Nothing was played, so the take was discarded."
            : string.Create(CultureInfo.InvariantCulture, $"Recorded {notes} note{(notes == 1 ? string.Empty : "s")}{(others > 0 ? $" and {others} other event{(others == 1 ? string.Empty : "s")}" : string.Empty)} on {name}."));

        // Recorded controllers on a target an automation lane controls are kept but do not play.
        if (Project.Sequence.FindTrack(take.Track) is { } track)
        {
            var replaced = take.Events.OfType<ChannelEvent>().Count(e => TrackRendering.Replaces(track, e, RouteChannel(track.Id)));
            if (replaced > 0)
            {
                AddMessage(MessageSeverity.Warning, "Record", string.Create(CultureInfo.InvariantCulture, $"{replaced} recorded event{(replaced == 1 ? " is" : "s are")} replaced by {name}'s automation lanes and will not play. Delete or clear the lane to hear them."));
            }
        }
    }

    private void ApplyMetronome()
    {
        var mode = !IsMetronomeEnabled ? MetronomeMode.Off : ClickWhilePlaying ? MetronomeMode.Always : MetronomeMode.WhileRecording;
        _playback.SetMetronome(new MetronomeSettings(mode, MetronomeOutput?.Id));
    }

    private void ApplyThru()
    {
        _playback.SetThruTrack(IsThruEnabled ? (_playback.Recorder.RecordingTrack ?? RecordTarget?.Id) : null);
        Keyboard?.RefreshChannel();
    }

    private void SyncEditor()
    {
        var track = SelectedTrack is { } selected ? Project.Sequence.FindTrack(selected.Id) : null;
        Editor.Sync(track, SelectedTrack?.ColorIndex ?? 0);
    }

    [RelayCommand]
    private void ReturnToStart() => SeekTo(0);

    [RelayCommand]
    private void Panic()
    {
        _playback.Panic();
        AddMessage(MessageSeverity.Info, "Panic", "Sent All Notes Off, All Sound Off, and sustain off on every channel of every output.");
    }

    /// <summary>Sets the loop (cycle) range, or turns it off with null, e.g. from the ruler.</summary>
    public void SetLoop(TickRange? loop) => Execute(ProjectCommands.SetLoop(loop));

    /// <summary>Turns the loop off, or loops four bars starting at the bar containing the playhead.</summary>
    [RelayCommand]
    private void ToggleLoop()
    {
        if (Project.Loop is not null)
        {
            Execute(ProjectCommands.SetLoop(null));
            return;
        }

        var meter = Project.Sequence.MeterMap;
        var start = meter.BarStart(new Tick(PlayheadTick));
        var bar = meter.ToBarBeatTick(start).Bar;
        var end = meter.TryGetTick(new BarBeatTick(bar + 4, 1, 0), out var e) ? e : start + new TickSpan(16L * Project.Sequence.Ppqn.TicksPerQuarterNote);
        Execute(ProjectCommands.SetLoop(new TickRange(start, end)));
    }

    [RelayCommand]
    private void AddTrack()
    {
        var track = Track.Create(string.Create(CultureInfo.InvariantCulture, $"Track {Project.Sequence.Tracks.Length + 1}"));
        Execute(ProjectCommands.AddTrack(track));
        if (Tracks.FirstOrDefault(t => t.Id == track.Id) is { } added)
        {
            Select(added);
        }
    }

    [RelayCommand]
    private void SelectAllTracks()
    {
        foreach (var track in Tracks.Where(t => !SelectedTracks.Contains(t)).ToList())
        {
            SelectedTracks.Add(track);
        }
    }

    [RelayCommand]
    private void DuplicateTracks()
    {
        if (SelectedTracks.Count > 0)
        {
            Execute(ProjectCommands.DuplicateTracks([.. SelectedTracks.Select(t => t.Id)]));
        }
    }

    /// <summary>Mutes the selected tracks, or unmutes them if they are all muted.</summary>
    [RelayCommand]
    private void ToggleMute()
    {
        if (SelectedTracks.Count > 0)
        {
            Execute(ProjectCommands.SetMuted([.. SelectedTracks.Select(t => t.Id)], !SelectedTracks.All(t => t.IsMuted)));
        }
    }

    /// <summary>Solos the selected tracks, or unsolos them if they are all soloed.</summary>
    [RelayCommand]
    private void ToggleSolo()
    {
        if (SelectedTracks.Count > 0)
        {
            Execute(ProjectCommands.SetSoloed([.. SelectedTracks.Select(t => t.Id)], !SelectedTracks.All(t => t.IsSoloed)));
        }
    }

    [RelayCommand]
    private void PreviousBar() => MoveByBars(-1);

    [RelayCommand]
    private void NextBar() => MoveByBars(1);

    [RelayCommand]
    private void GoToEnd() => SeekTo(Project.Sequence.EndPosition.Value);

    /// <summary>Moves to the start of an adjacent bar. Going back from mid-bar first returns to the bar's start.</summary>
    private void MoveByBars(int bars)
    {
        var meter = Project.Sequence.MeterMap;
        var position = new Tick(PlayheadTick);
        var start = meter.BarStart(position);
        var bar = meter.ToBarBeatTick(start).Bar;
        var targetBar = bars < 0 && start < position ? bar : Math.Max(1, bar + bars);
        if (meter.TryGetTick(new BarBeatTick(targetBar, 1, 0), out var target))
        {
            SeekTo(target.Value);
        }
    }

    [RelayCommand]
    private async Task DeleteTracksAsync()
    {
        var tracks = SelectedTracks.Select(t => Project.Sequence.FindTrack(t.Id)).OfType<Track>().ToList();
        if (tracks.Count == 0)
        {
            return;
        }

        var events = tracks.Sum(t => t.Clips.OfType<NoteClip>().Sum(c => c.Content.Items.Length));
        var points = tracks.Sum(t => t.Automation.Sum(l => l.Points.Length));
        var subject = tracks.Count == 1 ? $"\"{tracks[0].Name}\"" : $"{tracks.Count} tracks";
        var contents = points == 0 ? $"{events} events" : events == 0 ? $"{points} automation points" : $"{events} events and {points} automation points";
        if (events + points > 0 && !await _ui.ConfirmAsync(
                tracks.Count == 1 ? "Delete track?" : "Delete tracks?",
                $"{subject} {(tracks.Count == 1 ? "has" : "have")} {contents}. You can undo this.",
                "Delete",
                destructive: true))
        {
            return;
        }

        Execute(ProjectCommands.RemoveTracks([.. tracks.Select(t => t.Id)]));
    }

    /// <summary>Routes every track to the output shown in the inspector, except tracks in a group: they go through their group.</summary>
    [RelayCommand]
    private void UseOutputForAllTracks()
    {
        if (Selection.Output is not { Id: { } id, IsAvailable: true, IsMixed: false } || FindEndpoint(id) is not { } endpoint)
        {
            return;
        }

        var reference = Application.Routing.RouteResolver.ReferenceTo(endpoint);
        foreach (var track in Project.Sequence.Tracks.Where(t => t.Group is null))
        {
            Execute(ProjectCommands.SetTrackOutput(track.Id, TrackOutputs.Read(Project, track.Id) with { Endpoint = reference }));
        }
    }

    /// <summary>Sends the selected track's profile setup messages, asking first for anything that resets the instrument.</summary>
    [RelayCommand]
    private async Task InitializeInstrumentAsync()
    {
        if (SelectedTracks.Count != 1 || SelectedTrack is not { } track)
        {
            return;
        }

        await RunAsync("Initialize", async () =>
        {
            var sent = await _playback.InitializeInstrumentAsync(track.Id, template => _ui.ConfirmAsync(
                $"Send {template.Name}?",
                $"{template.Description ?? template.Name}\n\nThis resets settings on the instrument connected to \"{track.Output?.Name}\".",
                "Send",
                destructive: true));
            AddMessage(MessageSeverity.Info, "Initialize", string.Create(CultureInfo.InvariantCulture, $"Sent {sent} setup message{(sent == 1 ? string.Empty : "s")} to {track.Output?.Name}."));
        });
    }

    [RelayCommand]
    private void ClearMessages() => Messages.Clear();

    private void OnSessionChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        _ = SyncAndRefreshAsync();
    }

    private async Task SyncAndRefreshAsync()
    {
        Project = _session.Project;
        ProjectName = Project.Name;
        IsDirty = _session.IsDirty;
        IsLoopEnabled = Project.Loop is not null;
        SyncTracks();
        SyncEditor();
        Arrangement.Sync();
        await SyncPluginsAsync();
        await RefreshAsync();
    }

    /// <summary>Re-resolves routes and outputs, then updates every track's routing display.</summary>
    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0)
        {
            // A refresh is running; it goes round once more when it finishes, so it picks up the latest state.
            Volatile.Write(ref _refreshAgain, 1);
            return;
        }

        try
        {
            do
            {
                try
                {
                    await _playback.RefreshAsync();
                    foreach (var problem in _playback.OutputProblems)
                    {
                        AddMessage(MessageSeverity.Warning, "Outputs", problem);
                    }
                }
                catch (EndpointUnavailableException ex)
                {
                    AddMessage(MessageSeverity.Warning, "Outputs", ex.Message);
                }
            }
            while (Interlocked.Exchange(ref _refreshAgain, 0) != 0);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }

        SyncOutputs();
        SyncInputs();
        SyncTracks();
        Keyboard?.RefreshChannel();
    }

    private void SyncInputs()
    {
        // Built-in buses are listed so they can be chosen, but "All MIDI Inputs" leaves them out.
        var available = _playback.InputEndpoints;
        var wanted = new List<InputOption> { InputOption.All };
        wanted.AddRange(available.Select(e => new InputOption(e.Id, e.DisplayName)));
        if (SelectedInput is { Id: not null } chosen && !wanted.Contains(chosen))
        {
            wanted.Add(chosen with { Name = $"{chosen.Name} (disconnected)" });
        }

        if (!wanted.SequenceEqual(Inputs))
        {
            var selected = SelectedInput;
            Inputs.Clear();
            foreach (var option in wanted)
            {
                Inputs.Add(option);
            }

            SelectedInput = Inputs.FirstOrDefault(o => o.Id == selected?.Id) ?? InputOption.All;
        }

        var outputs = new List<OutputOption> { OutputOption.None with { Name = "Track Output", Detail = "The record or selected track's output" } };
        outputs.AddRange(Outputs);
        if (!outputs.SequenceEqual(MetronomeOutputs))
        {
            var selected = MetronomeOutput;
            MetronomeOutputs.Clear();
            foreach (var option in outputs)
            {
                MetronomeOutputs.Add(option);
            }

            MetronomeOutput = MetronomeOutputs.FirstOrDefault(o => o.Id == selected?.Id) ?? MetronomeOutputs[0];
        }

        foreach (var problem in _playback.InputProblems)
        {
            AddMessage(MessageSeverity.Warning, "Inputs", problem);
        }
    }

    private void SyncOutputs()
    {
        _outputDescriptors = _endpoints.GetEndpoints(EndpointDirection.Output);
        Outputs.Clear();
        foreach (var endpoint in _outputDescriptors)
        {
            Outputs.Add(new OutputOption(endpoint.Id, endpoint.DisplayName, Describe(endpoint.Transport), true));
        }

        OutputsText = string.Create(CultureInfo.InvariantCulture, $"{Outputs.Count} output{(Outputs.Count == 1 ? string.Empty : "s")} available");
    }

    private void SyncSelection()
    {
        Selection.Sync([.. SelectedTracks], [.. Outputs], _playback.Profiles);
        var single = SelectedTracks.Count == 1 ? SelectedTracks[0].Id : (TrackId?)null;
        DeviceStrip.Sync(Project, single, Devices);
        Connections.Sync(Project, single, DeviceStrip.Rack);
        Racks.Sync(Project, DeviceStrip.Rack);
        Mixer.Sync(Project.Mixer);
    }

    /// <summary>Refreshes the device strip, routing inspector, racks, and mixer from the project.</summary>
    internal void SyncDeviceViews() => SyncSelection();

    private static string Describe(EndpointTransport transport) => transport switch
    {
        EndpointTransport.Physical => "Hardware",
        EndpointTransport.Virtual => "Virtual port",
        EndpointTransport.Network => "Network",
        EndpointTransport.Software => "Software instrument",
        EndpointTransport.Test => "Built-in",
        _ => "MIDI output",
    };

    private void SyncTracks()
    {
        var tracks = Project.Sequence.Tracks;
        var selectedIds = SelectedTracks.Select(t => t.Id).ToHashSet();
        for (var i = Tracks.Count - 1; i >= 0; i--)
        {
            if (!tracks.Any(t => t.Id == Tracks[i].Id))
            {
                Tracks.RemoveAt(i);
            }
        }

        var outputs = Outputs.ToList();
        for (var i = 0; i < tracks.Length; i++)
        {
            var existing = Tracks.FirstOrDefault(t => t.Id == tracks[i].Id);
            if (existing is null)
            {
                existing = new TrackViewModel(this, tracks[i].Id);
                Tracks.Insert(Math.Min(i, Tracks.Count), existing);
            }
            else if (Tracks.IndexOf(existing) != i)
            {
                Tracks.Move(Tracks.IndexOf(existing), i);
            }

            existing.Sync(i + 1, tracks[i], _playback.Tracks.FirstOrDefault(r => r.Track == tracks[i].Id), outputs);
        }

        Groups.Sync();

        if (ArmedTrack is { } armed && !Tracks.Contains(armed))
        {
            ArmedTrack = null;
        }

        for (var i = SelectedTracks.Count - 1; i >= 0; i--)
        {
            if (!Tracks.Contains(SelectedTracks[i]))
            {
                SelectedTracks.RemoveAt(i);
            }
        }

        if (SelectedTracks.Count == 0 && Tracks.Count > 0)
        {
            SelectedTracks.Add(Tracks[0]);
        }
        else if (selectedIds.Count > 0)
        {
            // Rows were updated in place; refresh the inspector from their new values.
            SyncSelection();
        }
    }

    private async Task<bool> ResolveUnsavedChangesAsync()
    {
        if (!_session.IsDirty || _session.Project.Sequence.Tracks.IsEmpty && _session.FilePath is null)
        {
            return true;
        }

        switch (await _ui.AskToSaveChangesAsync(ProjectName))
        {
            case UnsavedChangesChoice.Discard:
                return true;
            case UnsavedChangesChoice.Save:
                await SaveAsync();
                return !_session.IsDirty;
            default:
                return false;
        }
    }

    private async Task RunAsync(string source, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectFormatException or SmfFormatException or EndpointUnavailableException or InvalidOperationException)
        {
            AddMessage(MessageSeverity.Error, source, ex.Message);
        }
    }

    private void ReportMidi(string source, IEnumerable<SmfDiagnostic> diagnostics)
    {
        foreach (var d in diagnostics)
        {
            AddMessage(d.Severity == SmfDiagnosticSeverity.Warning ? MessageSeverity.Warning : MessageSeverity.Info, source, d.ToString());
        }
    }

    internal void AddMessage(MessageSeverity severity, string source, string text)
    {
        Messages.Add(new MessageItem(severity, source, text));
        while (Messages.Count > MaxMessages)
        {
            Messages.RemoveAt(0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Keyboard?.ReleaseAll();
        _monitor?.Dispose();
        if (_plugins is not null)
        {
            await _plugins.DisposeAsync();
        }

        await _playback.DisposeAsync();
    }
}
