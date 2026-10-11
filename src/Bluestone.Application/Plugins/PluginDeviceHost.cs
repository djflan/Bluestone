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
public sealed record DeviceRuntimeStatus(string Text, bool NeedsAttention, bool CanRestart);

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

    private readonly PluginHostManager _manager;
    private readonly ProjectSession _session;
    private volatile ImmutableDictionary<DeviceDefinitionId, PluginIdentity> _plugins;
    private volatile ImmutableDictionary<PluginIdentity, string> _modulePaths = ImmutableDictionary<PluginIdentity, string>.Empty;
    private readonly ConcurrentDictionary<DeviceId, EventHandler<PluginParameterEditedEventArgs>> _editHandlers = new();
    private readonly ConcurrentDictionary<DeviceId, EventHandler<NotificationKind>> _changeHandlers = new();
    private readonly ConcurrentDictionary<DeviceId, PluginInstance> _instances = new();
    private readonly ConcurrentDictionary<DeviceId, EventHandler<PluginStatusChangedEventArgs>> _handlers = new();
    private readonly ConcurrentDictionary<DeviceDefinitionId, ImmutableArray<WorkerParameter>> _parameters = new();
    private readonly double _sampleRate;
    private readonly int _maxBlockFrames;

    public PluginDeviceHost(PluginHostManager manager, ProjectSession session, IEnumerable<PluginIdentity> available, double sampleRate = 48_000, int maxBlockFrames = 256)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(available);
        _plugins = available.ToImmutableDictionary(DefinitionIdOf, p => p);
        _sampleRate = sampleRate;
        _maxBlockFrames = maxBlockFrames;
    }

    /// <summary>A bridge whose available plugins follow <paramref name="library"/>: after every scan, rescan, or retry.</summary>
    public PluginDeviceHost(PluginHostManager manager, ProjectSession session, PluginLibrary library, double sampleRate = 48_000, int maxBlockFrames = 256)
        : this(manager, session, (library ?? throw new ArgumentNullException(nameof(library))).Available, sampleRate, maxBlockFrames)
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
            _session.Execute(DeviceCommands.SetParameter(edit.Device, new ParameterId(edit.ParameterId), ControlValue.FromFraction(edit.Value)));
        }
    }

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
        return status.State switch
        {
            PluginInstanceState.Running => new DeviceRuntimeStatus("Running", false, false),
            PluginInstanceState.Starting or PluginInstanceState.Restarting => new DeviceRuntimeStatus("Starting", false, false),
            PluginInstanceState.Quarantined => new DeviceRuntimeStatus("Stopped after repeated crashes", true, true),
            PluginInstanceState.Unavailable => new DeviceRuntimeStatus(status.Reason switch
            {
                UnavailableReason.Crashed => "Crashed",
                UnavailableReason.Hung => "Not responding",
                UnavailableReason.FailedToStart => "Failed to start",
                UnavailableReason.ProtocolError => "Misbehaved",
                _ => "Unavailable",
            }, true, true),
            _ => new DeviceRuntimeStatus("Unloaded", false, false),
        };
    }

    /// <summary>
    /// Brings the running instances in line with <paramref name="project"/>: every plugin device whose plugin is
    /// available gets an instance, created from its saved state and parameters; instances of devices that are gone
    /// are destroyed. Failures to start are statuses, not exceptions, and never fall back to Bluestone's process.
    /// </summary>
    public async Task SyncAsync(Project project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var available = _plugins;
        var wanted = project.Chains.SelectMany(c => c.Devices).Where(d => available.ContainsKey(d.Definition.Id)).ToDictionary(d => d.Id);
        foreach (var (id, instance) in _instances.ToList())
        {
            if (!wanted.ContainsKey(id) && _instances.TryRemove(id, out _))
            {
                instance.StatusChanged -= Handler(id);
                instance.ParameterEdited -= EditHandler(id);
                instance.Changed -= ChangeHandler(id);
                await _manager.DestroyInstanceAsync(instance, cancellationToken).ConfigureAwait(false);
                StatusChanged?.Invoke(this, id);
            }
        }

        foreach (var device in wanted.Values.Where(d => !_instances.ContainsKey(d.Id)))
        {
            var plugin = available[device.Definition.Id];
            var parameters = device.Parameters.Select(p => new WorkerParameterValue(p.Id.Value, p.Value.ToFraction())).ToImmutableArray();
            var request = new PluginInstanceRequest(plugin, _sampleRate, _maxBlockFrames, plugin.Kind == PluginKind.AudioEffect ? 2 : 0, plugin.Kind == PluginKind.MidiEffect ? 0 : 2)
            {
                ModulePath = _modulePaths.GetValueOrDefault(plugin),
                RestoreFrom = device.State is { } saved
                    ? new PluginStateSnapshot(plugin, new PluginStateData(saved.Format, [.. saved.Data.Span]), parameters, DateTimeOffset.UnixEpoch)
                    : null,
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
            if (device.State is null && instance.Status.State == PluginInstanceState.Running)
            {
                // No saved state: the stored parameter values are the configuration to restore. Only values that differ
                // from the plugin's own are sent (a plugin may have thousands of parameters).
                var known = instance.GetCachedParameterValues().ToDictionary(v => v.Id, v => v.Value);
                foreach (var parameter in parameters)
                {
                    if (known.TryGetValue(parameter.Id, out var current) && Math.Abs(current - parameter.Value) > 1e-9)
                    {
                        await instance.SetParameterAsync(parameter.Id, parameter.Value, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            StatusChanged?.Invoke(this, device.Id);
        }
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
        commands.AddRange(snapshot.ParameterValues.Where(p => shown.Contains(p.Id)).Select(p => DeviceCommands.SetParameter(device, new ParameterId(p.Id), ControlValue.FromFraction(p.Value))));
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

    /// <summary>Restarts a crashed, hung, or quarantined device's worker and restores its last captured state.</summary>
    public async Task<bool> RestartAsync(DeviceId device, CancellationToken cancellationToken = default)
    {
        if (!_instances.TryGetValue(device, out var instance))
        {
            return false;
        }

        var status = await instance.RestartAsync(cancellationToken).ConfigureAwait(false);
        StatusChanged?.Invoke(this, device);
        return status.State == PluginInstanceState.Running;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (id, instance) in _instances.ToList())
        {
            instance.StatusChanged -= Handler(id);
            await _manager.DestroyInstanceAsync(instance).ConfigureAwait(false);
        }

        _instances.Clear();
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
