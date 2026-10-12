using System.Collections.Concurrent;
using System.Collections.Immutable;
using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using Bluestone.Signal;
using DeviceParameter = Bluestone.Domain.Devices.ParameterDescriptor;
using WorkerParameter = Bluestone.Plugins.Protocol.ParameterDescriptor;
using WorkerParameterValue = Bluestone.Plugins.Protocol.ParameterValue;

namespace Bluestone.Application.Plugins;

/// <summary>A parameter a plugin changed itself, for the device that hosts it.</summary>
public sealed record PluginParameterEdit(DeviceId Device, uint ParameterId, double Value);

/// <summary>A plugin device's run-time status, in words for the device strip.</summary>
/// <param name="Text">"Running", "Crashed", "Not installed", …</param>
/// <param name="NeedsAttention">The musician should know: the device is not doing its job.</param>
/// <param name="CanRestart">A restart (or reload) can be asked for now.</param>
/// <param name="CanOpenEditor">The plugin is a running VST3 or CLAP plugin, whose editor can be asked for.</param>
/// <param name="Hosting">Which worker process it is in: "Hosted by plugin (Surge XT) · shares its process with Pad".</param>
/// <param name="Detail">More about a failure, for example which other devices went down with it.</param>
public sealed record DeviceRuntimeStatus(string Text, bool NeedsAttention, bool CanRestart, bool CanOpenEditor = false, string? Hosting = null, string? Detail = null);

/// <summary>How often a plugin's worker has failed under it this session.</summary>
/// <param name="Crashes">Worker processes that crashed, hung, or misbehaved while running this plugin.</param>
/// <param name="Shared">Of those, how many also ran other plugins, any of which may have caused the crash.</param>
public sealed record PluginCrashCount(int Crashes, int Shared);

/// <summary>Plugin devices whose worker process ended under them, together.</summary>
public sealed record PluginCrash(ImmutableArray<DeviceId> Devices, UnavailableReason Reason);

/// <summary>
/// Connects the project's plugin devices to plugin instances in worker processes (ADR 0025). The project
/// stays the authority: a device's identity, parameters, and saved state live in the project; the worker
/// only runs it. This bridge creates an instance for each plugin device whose plugin is available, restores
/// it from the device's saved state, captures state back into the project on request, reports status per
/// device, and restarts crashed devices. A device whose plugin is missing keeps everything and is reported.
/// </summary>
/// <remarks>Call from one thread (the UI's). Status events may arrive on any thread.</remarks>
public sealed class PluginDeviceHost : IAsyncDisposable
{
    public const string DefinitionPrefix = "plugin:";

    // Values closer than this are the same: the project stores 32-bit fractions, plugins often 32-bit floats.
    private const double ParameterTolerance = 1e-6;

    private readonly PluginHostManager _manager;
    private readonly ProjectSession _session;
    private volatile ImmutableDictionary<DeviceDefinitionId, PluginIdentity> _plugins;
    private volatile ImmutableDictionary<PluginIdentity, string> _modulePaths = ImmutableDictionary<PluginIdentity, string>.Empty;
    private readonly ConcurrentDictionary<DeviceId, EventHandler<PluginParameterEditedEventArgs>> _editHandlers = new();
    private readonly ConcurrentDictionary<DeviceId, EventHandler<NotificationKind>> _changeHandlers = new();
    private readonly ConcurrentDictionary<DeviceId, PluginInstance> _instances = new();
    private readonly ConcurrentDictionary<DeviceId, EventHandler<PluginStatusChangedEventArgs>> _handlers = new();
    private readonly ConcurrentDictionary<DeviceDefinitionId, ImmutableArray<WorkerParameter>> _parameters = new();
    // The value each running plugin parameter was last given by the host or recorded from the plugin. The project is
    // compared with this, not with the plugin's latest value, which runs ahead of the project while the plugin's own
    // edits are on their way to RecordEdit.
    private readonly ConcurrentDictionary<(DeviceId Device, uint Parameter), double> _hostValues = new();
    private readonly SemaphoreSlim _syncing = new(1, 1);

    // Devices started ahead of the edit that adds them (StageAsync): a sync must not destroy them before that edit lands.
    private readonly ConcurrentDictionary<DeviceId, byte> _staged = new();
    private double _sampleRate;
    private readonly int _maxBlockFrames;
    private volatile PluginHostingSettings _hosting;
    private volatile ImmutableDictionary<DeviceDefinitionId, PluginCrashCount> _crashes = ImmutableDictionary<DeviceDefinitionId, PluginCrashCount>.Empty;

    /// <summary>A bridge for <paramref name="available"/> plugins. <paramref name="hosting"/> says how instances share worker processes; null gives the defaults (by plugin, no automatic restart).</summary>
    public PluginDeviceHost(PluginHostManager manager, ProjectSession session, IEnumerable<PluginIdentity> available, double sampleRate = 48_000, int maxBlockFrames = 256, PluginHostingSettings? hosting = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(available);
        _plugins = available.ToImmutableDictionary(DefinitionIdOf, p => p);
        _sampleRate = sampleRate;
        _maxBlockFrames = maxBlockFrames;
        _hosting = (hosting ?? new PluginHostingSettings()).Sanitized();
        _manager.WorkerLost += OnWorkerLost;
    }

    /// <summary>A bridge whose available plugins follow <paramref name="library"/>: after every scan, rescan, or retry.</summary>
    public PluginDeviceHost(PluginHostManager manager, ProjectSession session, PluginLibrary library, double sampleRate = 48_000, int maxBlockFrames = 256, PluginHostingSettings? hosting = null)
        : this(manager, session, (library ?? throw new ArgumentNullException(nameof(library))).Available, sampleRate, maxBlockFrames, hosting)
    {
        _modulePaths = library.ModulePaths;
        library.Changed += (_, _) => SetAvailable(library.Available, library.ModulePaths);
    }

    /// <summary>
    /// The plugin changed a parameter itself (in its editor, or by loading one of its presets). Raised on any thread;
    /// pass it to <see cref="RecordEdit"/> on the UI thread so the project, the authority, holds the value.
    /// </summary>
    public event EventHandler<PluginParameterEdit>? ParameterEdited;

    /// <summary>Raised when a device's plugin status changes; the argument is the device.</summary>
    public event EventHandler<DeviceId>? StatusChanged;

    /// <summary>
    /// Raised when the available plugins change. <see cref="Extend"/> then gives a different catalog, and
    /// <see cref="SyncAsync"/> starts devices whose plugin was found and stops those whose plugin is gone.
    /// May be raised on any thread.
    /// </summary>
    public event EventHandler? AvailableChanged;

    /// <summary>
    /// A worker process ended under one or more of this bridge's devices: they are all Unavailable now, and
    /// <see cref="CrashCounts"/> is updated. Raised on any thread.
    /// </summary>
    public event EventHandler<PluginCrash>? Crashed;

    /// <summary>How instances share worker processes now. Change it with <see cref="ApplyHostingAsync"/>.</summary>
    public PluginHostingSettings Hosting => _hosting;

    /// <summary>Worker failures per plugin this session, by device definition ID. Plugins that never failed are absent.</summary>
    public ImmutableDictionary<DeviceDefinitionId, PluginCrashCount> CrashCounts => _crashes;

    /// <summary>
    /// Applies new hosting settings to every instance. An instance whose mode changes so that it belongs in another worker
    /// is moved there: a running one has its state captured first, a crashed one is restarted from its last state
    /// (<see cref="PluginHostManager.ChangeHostingAsync"/>). The restart policy applies from the next failure. Returns
    /// the devices that were moved or restarted.
    /// </summary>
    public async Task<ImmutableArray<DeviceId>> ApplyHostingAsync(PluginHostingSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _hosting = settings = settings.Sanitized();
        var moved = ImmutableArray.CreateBuilder<DeviceId>();
        foreach (var (device, instance) in _instances.ToList())
        {
            if (instance.RestartPolicy != settings.RestartPolicy)
            {
                instance.RestartPolicy = settings.RestartPolicy;
            }

            var mode = settings.ModeFor(instance.Plugin);
            if (instance.Hosting == mode)
            {
                continue;
            }

            // Every start in a worker takes a new generation, so a changed one means the instance was moved or restarted.
            var generation = instance.Generation;
            await _manager.ChangeHostingAsync(instance, mode, cancellationToken).ConfigureAwait(false);
            if (instance.Generation != generation)
            {
                moved.Add(device);
            }

            StatusChanged?.Invoke(this, device);
        }

        return moved.ToImmutable();
    }

    /// <summary>
    /// Replaces the available plugins, for example after a rescan. <paramref name="modulePaths"/> gives the module each
    /// third-party plugin is loaded from; the built-in reference plugins need none.
    /// </summary>
    public void SetAvailable(IEnumerable<PluginIdentity> available, ImmutableDictionary<PluginIdentity, string>? modulePaths = null)
    {
        ArgumentNullException.ThrowIfNull(available);
        _modulePaths = modulePaths ?? ImmutableDictionary<PluginIdentity, string>.Empty;
        _plugins = available.DistinctBy(DefinitionIdOf).ToImmutableDictionary(DefinitionIdOf, p => p);
        AvailableChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stores a parameter the plugin changed itself in the project (one undo step per parameter while it keeps moving).</summary>
    public void RecordEdit(PluginParameterEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (_session.Project.FindDevice(edit.Device) is not null && _instances.TryGetValue(edit.Device, out var instance)
            && instance.Parameters.Any(p => p.Id == edit.ParameterId && p.IsUserFacing))
        {
            var value = ControlValue.FromFraction(edit.Value);
            _hostValues[(edit.Device, edit.ParameterId)] = value.ToFraction();
            _session.Execute(DeviceCommands.SetParameter(edit.Device, new ParameterId(edit.ParameterId), value));
        }
    }

    /// <summary>Whether devices of <paramref name="definition"/> are plugins this host runs.</summary>
    public bool Hosts(DeviceDefinitionId definition) => _plugins.ContainsKey(definition);

    /// <summary>Whether the device's plugin is running with its editor window open.</summary>
    public bool IsEditorOpen(DeviceId device) => _instances.TryGetValue(device, out var instance) && instance.IsEditorOpen;

    /// <summary>
    /// Opens the device's plugin editor in a window of its worker. Returns null on success, else why it could not
    /// (not running, or the plugin has no editor).
    /// </summary>
    public async Task<string?> OpenEditorAsync(DeviceId device, CancellationToken cancellationToken = default)
    {
        if (!_instances.TryGetValue(device, out var instance) || instance.Status.State != PluginInstanceState.Running)
        {
            return "The plugin is not running.";
        }

        var title = instance.Plugin.DisplayName;
        try
        {
            await instance.OpenEditorAsync(title, cancellationToken).ConfigureAwait(false);
            StatusChanged?.Invoke(this, device);
            return null;
        }
        catch (Exception ex) when (ex is PluginHostException or PluginUnavailableException or TimeoutException)
        {
            return ex.Message;
        }
    }

    public async Task CloseEditorAsync(DeviceId device, CancellationToken cancellationToken = default)
    {
        if (_instances.TryGetValue(device, out var instance))
        {
            try
            {
                await instance.CloseEditorAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PluginHostException or PluginUnavailableException or TimeoutException)
            {
                // The worker is gone; its window went with it.
            }

            StatusChanged?.Invoke(this, device);
        }
    }

    public static DeviceDefinitionId DefinitionIdOf(PluginIdentity plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        return new DeviceDefinitionId($"{DefinitionPrefix}{plugin.Format}:{plugin.ModuleId}/{plugin.PluginId}");
    }

    /// <summary>
    /// A device definition for a plugin: data only, never runnable in Bluestone's process. What it handles follows
    /// its kind: an instrument takes notes, controllers, and programs and makes audio; a MIDI effect takes notes
    /// (everything else passes around it, as the host promises); an audio effect takes and makes audio.
    /// </summary>
    public static DeviceDefinition DefinitionOf(PluginIdentity plugin, IEnumerable<WorkerParameter>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var (consumes, produces, handles) = plugin.Kind switch
        {
            PluginKind.Instrument => (SignalKinds.Events, SignalKinds.Audio, EventClass.Notes | EventClass.Controllers | EventClass.Programs),
            PluginKind.MidiEffect => (SignalKinds.Events, SignalKinds.Events, EventClass.Notes),
            _ => (SignalKinds.Audio, SignalKinds.Audio, EventClass.None),
        };
        return new DeviceDefinition
        {
            Id = DefinitionIdOf(plugin),
            Name = plugin.DisplayName,
            Origin = DeviceOrigin.Plugin,
            Vendor = plugin.Vendor,
            Version = plugin.Version,
            Consumes = consumes,
            Produces = produces,
            Handles = handles,
            Parameters = [.. (parameters ?? []).Where(p => p.IsUserFacing).Select(p => new DeviceParameter(new ParameterId(p.Id), p.Name, 0, 1, p.DefaultValue, p.StepCount, p.Units))],
        };
    }

    /// <summary>
    /// <paramref name="catalog"/> with every available plugin added as a definition (data only), and a renderer that
    /// runs plugin MIDI effects in their workers when the plan is compiled, so their output is routed downstream. A
    /// plugin's parameters are listed once a worker has reported them, so automation of them can be checked.
    /// </summary>
    public DeviceCatalog Extend(DeviceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return _plugins
            .Aggregate(catalog, (c, p) => c.With(DefinitionOf(p.Value, _parameters.GetValueOrDefault(p.Key, []))))
            .WithRenderer(new PluginEventRenderer(this));
    }

    public PluginInstance? InstanceOf(DeviceId device) => _instances.GetValueOrDefault(device);

    /// <summary>The rate plugin instances are created at (the audio device's, once audio runs).</summary>
    public double SampleRate => _sampleRate;

    /// <summary>
    /// Runs every plugin instance at <paramref name="sampleRate"/> from now on: running instances are captured (their
    /// state and parameters, not stored in the project), stopped, and created again at the new rate from what was
    /// captured; others start again from their last snapshot or the project's stored state. Nothing happens when the
    /// rate is unchanged. Plugins cannot change rate while running, so this is a restart; their status shows it.
    /// </summary>
    public async Task SetSampleRateAsync(double sampleRate, CancellationToken cancellationToken = default)
    {
        await _syncing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sampleRate != _sampleRate)
            {
                _sampleRate = sampleRate;
                await RecreateAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _syncing.Release();
        }
    }

    private async Task RecreateAsync(CancellationToken cancellationToken)
    {
        var restore = new Dictionary<DeviceId, PluginStateSnapshot?>();
        foreach (var (id, instance) in _instances.ToList())
        {
            var snapshot = instance.LastSnapshot;
            if (instance.Status.State == PluginInstanceState.Running)
            {
                try
                {
                    snapshot = await instance.CaptureStateAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (PluginHostException)
                {
                    // A plugin that cannot report its state now starts again from the last state it did report.
                }
            }

            restore[id] = snapshot;
            if (_instances.TryRemove(id, out _))
            {
                instance.StatusChanged -= Handler(id);
                instance.ParameterEdited -= EditHandler(id);
                instance.Changed -= ChangeHandler(id);
                await _manager.DestroyInstanceAsync(instance, cancellationToken).ConfigureAwait(false);
            }
        }

        await SyncCoreAsync(_session.Project, restore, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether <paramref name="device"/> is a plugin MIDI effect, whose output is worked out in its worker when the plan
    /// is compiled: when its status changes, the plan should be compiled again.
    /// </summary>
    public bool ShapesThePlan(DeviceId device) =>
        _session.Project.FindDevice(device) is { } found && PluginOf(found.Device.Definition.Id) is { Kind: PluginKind.MidiEffect };

    /// <summary>The available plugin a definition stands for, or null.</summary>
    internal PluginIdentity? PluginOf(DeviceDefinitionId definition) => _plugins.GetValueOrDefault(definition);

    /// <summary>The status of a plugin device, or null for a device that is not a plugin.</summary>
    public DeviceRuntimeStatus? StatusOf(DeviceInstance device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!device.Definition.Id.Value.StartsWith(DefinitionPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        if (!_plugins.ContainsKey(device.Definition.Id))
        {
            return new DeviceRuntimeStatus("Not installed", NeedsAttention: true, CanRestart: false);
        }

        if (!_instances.TryGetValue(device.Id, out var instance))
        {
            return new DeviceRuntimeStatus("Not loaded", NeedsAttention: false, CanRestart: false);
        }

        var status = instance.Status;
        var hosting = HostingOf(device.Id, instance);
        return status.State switch
        {
            PluginInstanceState.Running => new DeviceRuntimeStatus(instance.IsEditorOpen ? "Running, editor open" : "Running", false, false, CanOpenEditor: instance.Plugin.Format is "vst3" or "clap", hosting),
            PluginInstanceState.Starting or PluginInstanceState.Restarting => new DeviceRuntimeStatus("Starting", false, false, Hosting: hosting),
            PluginInstanceState.Quarantined => new DeviceRuntimeStatus("Stopped after repeated crashes", true, true, Hosting: hosting, Detail: WentDownWith(device.Id, instance)),
            PluginInstanceState.Unavailable => new DeviceRuntimeStatus(status.Reason switch
            {
                UnavailableReason.Crashed => "Crashed",
                UnavailableReason.Hung => "Not responding",
                UnavailableReason.FailedToStart => "Failed to start",
                UnavailableReason.ProtocolError => "Misbehaved",
                _ => "Unavailable",
            }, true, true, Hosting: hosting, Detail: WentDownWith(device.Id, instance)),
            _ => new DeviceRuntimeStatus("Unloaded", false, false),
        };
    }

    /// <summary>
    /// The devices that were in the same worker process as <paramref name="device"/> when it last ended, this one first;
    /// just this one when it ran alone, and empty when its worker has not failed since it last ran.
    /// </summary>
    public ImmutableArray<DeviceId> WentDownTogether(DeviceId device) =>
        _instances.TryGetValue(device, out var instance) && instance.LastWorkerLoss is { } loss
            ? [device, .. DevicesOf(loss.Instances).Where(d => d != device)]
            : [];

    /// <summary>
    /// Brings the running instances in line with <paramref name="project"/>: every plugin device whose plugin is
    /// available gets an instance, created from its saved state and parameters; instances of devices that are gone
    /// are destroyed. Failures to start are statuses, not exceptions, and never fall back to Bluestone's process.
    /// </summary>
    public async Task SyncAsync(Project project, CancellationToken cancellationToken = default)
    {
        await _syncing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SyncCoreAsync(project, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _syncing.Release();
        }
    }

    private async Task SyncCoreAsync(Project project, Dictionary<DeviceId, PluginStateSnapshot?>? restore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var available = _plugins;
        var wanted = project.Chains.SelectMany(c => c.Devices).Where(d => available.ContainsKey(d.Definition.Id)).ToDictionary(d => d.Id);
        foreach (var id in wanted.Keys)
        {
            _staged.TryRemove(id, out _);
        }

        foreach (var (id, instance) in _instances.ToList())
        {
            if (!wanted.ContainsKey(id) && !_staged.ContainsKey(id) && _instances.TryRemove(id, out _))
            {
                ForgetHostValues(id);
                instance.StatusChanged -= Handler(id);
                instance.ParameterEdited -= EditHandler(id);
                instance.Changed -= ChangeHandler(id);
                await _manager.DestroyInstanceAsync(instance, cancellationToken).ConfigureAwait(false);
                StatusChanged?.Invoke(this, id);
            }
        }

        // Values changed in the project (in the device strip, or by undo) go to the running plugins. Values the plugin
        // set itself and that were recorded are not sent back: by then the plugin may have moved on.
        foreach (var device in wanted.Values)
        {
            if (_instances.TryGetValue(device.Id, out var running) && running.Status.State == PluginInstanceState.Running)
            {
                var known = running.GetCachedParameterValues().ToDictionary(v => v.Id, v => v.Value);
                foreach (var parameter in device.Parameters)
                {
                    var value = parameter.Value.ToFraction();
                    if (!known.TryGetValue(parameter.Id.Value, out var current))
                    {
                        continue;
                    }

                    var key = (device.Id, parameter.Id.Value);
                    var given = _hostValues.TryGetValue(key, out var last) ? last : current;
                    if (Math.Abs(given - value) > ParameterTolerance)
                    {
                        await running.SetParameterAsync(parameter.Id.Value, value, cancellationToken).ConfigureAwait(false);
                    }

                    _hostValues[key] = value;
                }
            }
        }

        foreach (var device in wanted.Values.Where(d => !_instances.ContainsKey(d.Id)))
        {
            await StartAsync(device, available[device.Definition.Id], restore?.GetValueOrDefault(device.Id), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts <paramref name="device"/>'s plugin in a worker before the device is in the project, so a replacement can
    /// be checked before the project changes. Returns null when it runs (the instance is then kept for the device and
    /// adopted when the project has it), or why it could not start (nothing is kept). Never loads a plugin in
    /// Bluestone's process. <see cref="UnstageAsync"/> drops an instance whose edit did not happen.
    /// </summary>
    public async Task<string?> StageAsync(DeviceInstance device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!_plugins.TryGetValue(device.Definition.Id, out var plugin))
        {
            return $"{device.Definition.DisplayName ?? "The plugin"} is not installed here.";
        }

        await _syncing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _staged[device.Id] = 0;
            var instance = await StartAsync(device, plugin, null, cancellationToken).ConfigureAwait(false);
            if (instance.Status.State == PluginInstanceState.Running)
            {
                return null;
            }

            var reason = instance.Status.Message ?? $"{plugin.DisplayName} did not start.";
            await DropAsync(device.Id, cancellationToken).ConfigureAwait(false);
            return reason;
        }
        catch
        {
            await DropAsync(device.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _syncing.Release();
        }
    }

    /// <summary>Destroys an instance started by <see cref="StageAsync"/> whose device never reached the project.</summary>
    public async Task UnstageAsync(DeviceId device, CancellationToken cancellationToken = default)
    {
        await _syncing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_staged.ContainsKey(device))
            {
                await DropAsync(device, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _syncing.Release();
        }
    }

    private async Task DropAsync(DeviceId id, CancellationToken cancellationToken)
    {
        _staged.TryRemove(id, out _);
        if (_instances.TryRemove(id, out var instance))
        {
            ForgetHostValues(id);
            instance.StatusChanged -= Handler(id);
            instance.ParameterEdited -= EditHandler(id);
            instance.Changed -= ChangeHandler(id);
            await _manager.DestroyInstanceAsync(instance, cancellationToken).ConfigureAwait(false);
            StatusChanged?.Invoke(this, id);
        }
    }

    // Starts the device's plugin from its saved state (or `captured`) and parameters, and keeps the instance for it.
    private async Task<PluginInstance> StartAsync(DeviceInstance device, PluginIdentity plugin, PluginStateSnapshot? captured, CancellationToken cancellationToken)
    {
        var parameters = device.Parameters.Select(p => new WorkerParameterValue(p.Id.Value, p.Value.ToFraction())).ToImmutableArray();
        var hosting = _hosting;
        var request = new PluginInstanceRequest(plugin, _sampleRate, _maxBlockFrames, plugin.Kind == PluginKind.AudioEffect ? 2 : 0, plugin.Kind == PluginKind.MidiEffect ? 0 : 2)
        {
            Hosting = hosting.ModeFor(plugin),
            RestartPolicy = hosting.RestartPolicy,
            ModulePath = _modulePaths.GetValueOrDefault(plugin),
            RestoreFrom = captured ?? (device.State is { } saved
                ? new PluginStateSnapshot(plugin, new PluginStateData(saved.Format, [.. saved.Data.Span]), parameters, DateTimeOffset.UnixEpoch)
                : null),
        };
        var instance = await _manager.CreateInstanceAsync(request, cancellationToken).ConfigureAwait(false);
        _instances[device.Id] = instance;
        if (!instance.Parameters.IsEmpty)
        {
            _parameters[device.Definition.Id] = instance.Parameters;
        }

        instance.StatusChanged += Handler(device.Id);
        instance.ParameterEdited += EditHandler(device.Id);
        instance.Changed += ChangeHandler(device.Id);
        if (device.State is null && captured is null && instance.Status.State == PluginInstanceState.Running)
        {
            // No saved state: the stored parameter values are the configuration to restore. Only values that differ
            // from the plugin's own are sent (a plugin may have thousands of parameters).
            var known = instance.GetCachedParameterValues().ToDictionary(v => v.Id, v => v.Value);
            foreach (var parameter in parameters)
            {
                if (known.TryGetValue(parameter.Id, out var current) && Math.Abs(current - parameter.Value) > ParameterTolerance)
                {
                    await instance.SetParameterAsync(parameter.Id, parameter.Value, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // The instance starts from the project's values (its stored state is restored with them).
        ForgetHostValues(device.Id);
        foreach (var parameter in parameters)
        {
            _hostValues[(device.Id, parameter.Id)] = parameter.Value;
        }

        StatusChanged?.Invoke(this, device.Id);
        return instance;
    }

    /// <summary>
    /// Asks the device's worker for its current state and parameter values and stores them in the project as one
    /// undoable step, so the next save, reopen, or restart restores them. Returns false when the plugin is not running
    /// (a crashed plugin cannot be asked for newer state; the last stored state stays).
    /// </summary>
    public async Task<bool> CaptureStateAsync(DeviceId device, CancellationToken cancellationToken = default)
    {
        if (!_instances.TryGetValue(device, out var instance) || instance.Status.State != PluginInstanceState.Running)
        {
            return false;
        }

        var snapshot = await instance.CaptureStateAsync(cancellationToken).ConfigureAwait(false);
        var state = new PluginState(ByteBlock.Copy(snapshot.State.Data.AsSpan()), snapshot.State.Format);
        var commands = new List<IProjectCommand> { DeviceCommands.SetPluginState(device, state) };
        var shown = instance.Parameters.Where(p => p.IsUserFacing).Select(p => p.Id).ToHashSet();
        foreach (var parameter in snapshot.ParameterValues.Where(p => shown.Contains(p.Id)))
        {
            var value = ControlValue.FromFraction(parameter.Value);
            _hostValues[(device, parameter.Id)] = value.ToFraction();
            commands.Add(DeviceCommands.SetParameter(device, new ParameterId(parameter.Id), value));
        }

        _session.Execute(ProjectCommands.Batch("Store Plugin State", commands));
        return true;
    }

    /// <summary>Captures every running plugin's state into the project, before a save.</summary>
    public async Task CaptureAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var device in _instances.Keys.ToList())
        {
            await CaptureStateAsync(device, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Restarts a crashed, hung, or quarantined device's worker and restores its last captured state. Devices that went
    /// down with it (they shared its worker) and are still down are restarted with it, so they share a worker again.
    /// Returns whether <paramref name="device"/> runs again.
    /// </summary>
    public async Task<bool> RestartAsync(DeviceId device, CancellationToken cancellationToken = default)
    {
        if (!_instances.TryGetValue(device, out var instance))
        {
            return false;
        }

        var together = WentDownTogether(device).Where(d => d != device).Select(d => (Device: d, Instance: _instances.GetValueOrDefault(d)))
            .Where(t => t.Instance?.Status.State is PluginInstanceState.Unavailable or PluginInstanceState.Quarantined)
            .ToList();
        var restarts = together.Select(t => t.Instance!.RestartAsync(cancellationToken)).Prepend(instance.RestartAsync(cancellationToken)).ToList();
        var statuses = await Task.WhenAll(restarts).ConfigureAwait(false);
        StatusChanged?.Invoke(this, device);
        foreach (var (other, _) in together)
        {
            StatusChanged?.Invoke(this, other);
        }

        return statuses[0].State == PluginInstanceState.Running;
    }

    public async ValueTask DisposeAsync()
    {
        _manager.WorkerLost -= OnWorkerLost;
        foreach (var (id, instance) in _instances.ToList())
        {
            instance.StatusChanged -= Handler(id);
            await _manager.DestroyInstanceAsync(instance).ConfigureAwait(false);
        }

        _instances.Clear();
        _syncing.Dispose();
    }

    // Runs on a thread-pool thread after every instance on the worker is Unavailable.
    private void OnWorkerLost(object? sender, WorkerLoss loss)
    {
        var devices = DevicesOf(loss.Instances).ToImmutableArray();
        if (devices.IsEmpty)
        {
            return;
        }

        var plugins = loss.Instances.Where(i => _instances.Values.Contains(i)).Select(i => DefinitionIdOf(i.Plugin)).Distinct().ToList();
        var shared = plugins.Count > 1;
        ImmutableDictionary<DeviceDefinitionId, PluginCrashCount> before, after;
        do
        {
            before = _crashes;
            after = before;
            foreach (var plugin in plugins)
            {
                var count = after.GetValueOrDefault(plugin) ?? new PluginCrashCount(0, 0);
                after = after.SetItem(plugin, new PluginCrashCount(count.Crashes + 1, count.Shared + (shared ? 1 : 0)));
            }
        }
        while (Interlocked.CompareExchange(ref _crashes, after, before) != before);

        Crashed?.Invoke(this, new PluginCrash(devices, loss.Reason));
    }

    private IEnumerable<DeviceId> DevicesOf(IEnumerable<PluginInstance> instances)
    {
        var wanted = instances.ToHashSet();
        return _instances.Where(p => wanted.Contains(p.Value)).Select(p => p.Key);
    }

    private string NameOf(DeviceId device) => _session.Project.FindDevice(device) is { } found ? found.Device.DisplayName : "a removed device";

    private static string List(List<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        <= 3 => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        _ => string.Join(", ", names.Take(2)) + $" and {names.Count - 2} more",
    };

    // "Hosted by plugin (Surge XT) · shares its process with Pad and Bass", from the instance's mode and current worker.
    private string HostingOf(DeviceId device, PluginInstance instance)
    {
        var plugin = instance.Plugin;
        var text = instance.Hosting switch
        {
            HostingMode.Individually => "Hosted individually",
            HostingMode.ByManufacturer when plugin.Vendor.Trim().Length > 0 => $"Hosted by manufacturer ({plugin.Vendor.Trim()})",
            HostingMode.ByManufacturer => $"Hosted by plugin ({plugin.ModuleId}; no manufacturer given)",
            HostingMode.ByPlugin => $"Hosted by plugin ({plugin.ModuleId})",
            _ => "Hosted together",
        };
        if (_hosting.Overrides.ContainsKey(DefinitionIdOf(plugin).Value))
        {
            text += ", set for this plugin";
        }

        if (instance.WorkerKey is { } key && instance.Status.State == PluginInstanceState.Running)
        {
            var others = _instances.Where(p => p.Key != device && p.Value.WorkerKey == key && p.Value.Status.State == PluginInstanceState.Running)
                .Select(p => NameOf(p.Key)).Order(StringComparer.CurrentCulture).ToList();
            if (others.Count > 0)
            {
                text += $" · shares its process with {List(others)}";
            }
        }

        return text;
    }

    private string? WentDownWith(DeviceId device, PluginInstance instance)
    {
        var others = WentDownTogether(device).Where(d => d != device).Select(NameOf).Order(StringComparer.CurrentCulture).ToList();
        return others.Count == 0 || instance.LastWorkerLoss is null
            ? null
            : $"{List(others)} went down with it: they shared its worker process. Restart starts them all again.";
    }

    private void ForgetHostValues(DeviceId device)
    {
        foreach (var key in _hostValues.Keys.Where(k => k.Device == device).ToList())
        {
            _hostValues.TryRemove(key, out _);
        }
    }

    // One handler per device, so the same delegate can be removed again.
    private EventHandler<PluginStatusChangedEventArgs> Handler(DeviceId device) =>
        _handlers.GetOrAdd(device, id => (_, _) => StatusChanged?.Invoke(this, id));

    private EventHandler<PluginParameterEditedEventArgs> EditHandler(DeviceId device) =>
        _editHandlers.GetOrAdd(device, id => (_, e) => ParameterEdited?.Invoke(this, new PluginParameterEdit(id, e.ParameterId, e.Value)));

    private EventHandler<NotificationKind> ChangeHandler(DeviceId device) =>
        _changeHandlers.GetOrAdd(device, id => (_, kind) =>
        {
            if (kind is NotificationKind.EditorClosed or NotificationKind.LatencyChanged)
            {
                StatusChanged?.Invoke(this, id);
            }
        });
}
