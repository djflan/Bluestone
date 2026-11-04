using System.Collections.Concurrent;
using System.Collections.Immutable;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;
using Bluestone.Plugins.Workers;

namespace Bluestone.Plugins;

/// <summary>What one <see cref="PluginInstance.ProcessBlock"/> call delivered.</summary>
public enum ProcessOutcome
{
    /// <summary>The plugin's output for the block collected this call.</summary>
    Processed,

    /// <summary>The pipeline is still filling (the first depth - 1 blocks): exact silence. This is latency, not failure.</summary>
    Priming,

    /// <summary>The block was not Done in time, was never submitted, or did not fit; the failure policy decided the output.</summary>
    Late,

    /// <summary>The plugin reported an error for the block; the failure policy decided the output.</summary>
    PluginError,

    /// <summary>The instance is not running (worker dead, starting, quarantined, unloaded); the failure policy decided the output.</summary>
    Unavailable,
}

/// <summary>Result of <see cref="PluginInstance.ProcessBlock"/>. <see cref="Bypassed"/> is set when the failure policy passed the dry input through.</summary>
public readonly record struct BlockOutcome(ProcessOutcome Outcome, int OutputEventCount, bool Bypassed);

/// <summary>
/// A plugin instance hosted in a worker process. One audio thread calls <see cref="ProcessBlock"/> and
/// <see cref="QueueParameterChange"/>; those never lock, allocate, do I/O, log, or throw for a plugin failure. Control
/// threads use the async methods. Status changes are raised in order, never on the audio thread.
/// </summary>
public sealed class PluginInstance : IPluginProcessor
{
    private readonly PluginHostManager _manager;
    private readonly Lock _statusGate = new();
    private readonly ConcurrentQueue<PluginStatusChangedEventArgs> _statusEvents = new();
    private readonly PluginBlockPipeline _pipeline;
    private PluginInstanceStatus _status = PluginInstanceStatus.Starting;
    private volatile ActiveLink? _link;
    private volatile ParameterTable? _parameters;
    private volatile PluginStateSnapshot? _lastSnapshot;
    private int _dispatching;
    private uint _generation;
    private CancellationTokenSource? _pendingRestart;
    private CancellationTokenSource? _snapshotLoop;
    private RestartPolicy _restartPolicy;
    private volatile WorkerLoss? _lastWorkerLoss;

    internal PluginInstance(PluginHostManager manager, PluginInstanceRequest request, int pipelineDepth, TimeSpan collectWait, int slotCount)
    {
        _manager = manager;
        Request = request;
        Id = PluginInstanceId.New();
        PipelineDepth = pipelineDepth;
        CollectWait = collectWait;
        ExchangeShape = new ExchangeOptions(request.MaxBlockFrames, request.InputChannels, request.OutputChannels, slotCount, request.EventCapacity, request.ParameterChangeCapacity);
        _lastSnapshot = request.RestoreFrom;
        _pipeline = new PluginBlockPipeline(request.MaxBlockFrames, request.InputChannels, request.ParameterChangeCapacity, pipelineDepth, collectWait, request.FailurePolicy == FailurePolicy.BypassInput);
        _restartPolicy = request.RestartPolicy;
        Guard = new RestartGuard(request.RestartPolicy);
        Hosting = request.Hosting;
    }

    public event EventHandler<PluginStatusChangedEventArgs>? StatusChanged;

    /// <summary>
    /// The plugin changed one of its parameters itself (usually in its editor). The remembered value is already updated.
    /// Raised on a worker's read loop, in the order the plugin made the changes.
    /// </summary>
    public event EventHandler<PluginParameterEditedEventArgs>? ParameterEdited;

    /// <summary>
    /// Something else the plugin did on its own: its latency changed (<see cref="PluginLatencyFrames"/> is updated), its
    /// state changed and should be captured before the next save, its editor was closed, or its parameter list changed.
    /// Raised on a worker's read loop.
    /// </summary>
    public event EventHandler<NotificationKind>? Changed;

    /// <summary>
    /// A key typed in the editor window while its keys go to Bluestone (<see cref="SetEditorKeyboardAsync"/>). Raised on
    /// a worker's read loop, in order.
    /// </summary>
    public event EventHandler<EditorKey>? EditorKeyPressed;

    /// <summary>
    /// A button of the header Bluestone draws on the editor window was clicked (<see cref="SetEditorHeaderAsync"/>).
    /// Raised on a worker's read loop, in order.
    /// </summary>
    public event EventHandler<EditorButtonClick>? EditorButtonClicked;

    private volatile bool _editorOpen;

    /// <summary>Whether the plugin's editor window is open in its worker, as far as the host knows.</summary>
    public bool IsEditorOpen => _editorOpen && _link is not null;

    public PluginInstanceId Id { get; }

    public PluginIdentity Plugin => Request.Plugin;

    public PluginInstanceRequest Request { get; }

    int IPluginProcessor.MaxBlockFrames => Request.MaxBlockFrames;

    int IPluginProcessor.InputChannels => Request.InputChannels;

    int IPluginProcessor.OutputChannels => Request.OutputChannels;

    int IPluginProcessor.EventCapacity => Request.EventCapacity;

    public PluginInstanceStatus Status
    {
        get
        {
            lock (_statusGate)
            {
                return _status;
            }
        }
    }

    /// <summary>Parameters as the plugin described them; empty until the instance first ran.</summary>
    public ImmutableArray<ParameterDescriptor> Parameters => _parameters?.Descriptors ?? [];

    /// <summary>Latency the plugin itself reports, in frames.</summary>
    public int PluginLatencyFrames { get; private set; }

    public int PipelineDepth { get; }

    /// <summary>Total latency to compensate: plugin latency plus (depth - 1) blocks of <see cref="PluginInstanceRequest.MaxBlockFrames"/>.</summary>
    public int ReportedLatencyFrames => PipelineLatency.ReportedLatencyFrames(PluginLatencyFrames, PipelineDepth, Request.MaxBlockFrames);

    /// <summary>The last state captured from the live plugin (or the snapshot it was created from). Recovery restores this.</summary>
    public PluginStateSnapshot? LastSnapshot => _lastSnapshot;

    /// <summary>Why the last state restore was rejected by the plugin, if it was. The instance then ran with defaults.</summary>
    public string? LastStateRestoreError { get; private set; }

    /// <summary>The current worker's process id, or null when there is none.</summary>
    public int? WorkerProcessId => _link?.Worker.ProcessId;

    /// <summary>How this instance shares a worker now: the request's mode until <see cref="PluginHostManager.ChangeHostingAsync"/> moves it.</summary>
    public HostingMode Hosting { get; internal set; }

    /// <summary>The <see cref="WorkerKeys">key</see> of the worker this instance runs on or is starting on; null when it has none.</summary>
    public string? WorkerKey => Slot?.Key;

    /// <summary>
    /// When to restart after a failure; the request's policy until changed. Changing it forgets the restart history, and
    /// turning automatic restart off cancels a restart that is waiting for its back-off.
    /// </summary>
    public RestartPolicy RestartPolicy
    {
        get
        {
            lock (GuardGate)
            {
                return _restartPolicy;
            }
        }

        set
        {
            var guard = new RestartGuard(value);
            lock (GuardGate)
            {
                _restartPolicy = value;
                Guard = guard;
            }

            if (!value.AutoRestart)
            {
                CancelPendingRestart();
            }
        }
    }

    /// <summary>
    /// The last time this instance's worker ended under it: which process, why, and every instance that was running in
    /// that worker (this one included), so the user can be told what went down together. Cleared when it runs again.
    /// </summary>
    public WorkerLoss? LastWorkerLoss => _lastWorkerLoss;

    /// <summary>The data-plane generation; it changes on every (re)creation and on every worker loss.</summary>
    public uint Generation => Volatile.Read(ref _generation);

    /// <summary>Parameter changes dropped because the per-block queue was full.</summary>
    public long ParameterChangesDropped => _pipeline.ParameterChangesDropped;

    /// <summary>The shape of every block exchange this instance's workers create.</summary>
    public ExchangeOptions ExchangeShape { get; }

    /// <summary>How long <see cref="ProcessBlock"/> may spin for a block that is not Done yet.</summary>
    public TimeSpan CollectWait { get; }

    /// <summary>
    /// The running worker's block exchange, for a process that drives the data plane itself (the engine, ADR 0044); null
    /// while the instance is not running. It changes whenever the worker does (a new generation and file).
    /// </summary>
    public PluginDataPlane? DataPlane => _link is { } link ? new PluginDataPlane(link.Exchange.Path, link.Exchange.Generation, link.Exchange.Options) : null;

    /// <summary>The restart history. Read and use it only while holding <see cref="GuardGate"/>.</summary>
    internal RestartGuard Guard { get; private set; }

    internal Lock GuardGate { get; } = new();

    internal SemaphoreSlim Lifecycle { get; } = new(1, 1);

    internal PluginHostManager.WorkerSlot? Slot { get; set; }

    internal ActiveLink? Link => _link;

    internal ParameterTable? ParameterTable => _parameters;

    /// <summary>
    /// Processes one block. Audio is planar: channel c of <paramref name="input"/> is <c>[c * frames, (c + 1) * frames)</c>,
    /// and likewise for <paramref name="output"/>. Block k is submitted and block k - (depth - 1) is collected into the
    /// output, so the output lags by <see cref="ReportedLatencyFrames"/>; use one block size throughout. On any failure
    /// the output is exact silence (or the delayed dry input under <see cref="FailurePolicy.BypassInput"/>), never stale
    /// plugin audio. Argument errors throw; plugin failures never do.
    /// </summary>
    public BlockOutcome ProcessBlock(
        int frames,
        ReadOnlySpan<float> input,
        Span<float> output,
        ReadOnlySpan<PluginEvent> inputEvents,
        Span<PluginEvent> outputEvents,
        in TransportState transport)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frames, Request.MaxBlockFrames);
        if (input.Length != Request.InputChannels * frames)
        {
            throw new ArgumentException("The input must hold InputChannels * frames samples.", nameof(input));
        }

        if (output.Length < Request.OutputChannels * frames)
        {
            throw new ArgumentException("The output must hold OutputChannels * frames samples.", nameof(output));
        }

        return _pipeline.Process(_link?.Exchange, frames, input, output[..(Request.OutputChannels * frames)], inputEvents, outputEvents, transport);
    }

    /// <summary>
    /// Queues a normalized parameter change for the next <see cref="ProcessBlock"/>, at <paramref name="sampleOffset"/>
    /// frames into that block. Audio thread only. The value is also remembered so recovery restores it. False when the
    /// id is unknown, the value or offset is out of range, or the queue is full (counted).
    /// </summary>
    public bool QueueParameterChange(uint parameterId, double normalizedValue, int sampleOffset)
    {
        var table = _parameters;
        if (table is null || !NormalizedValue.IsValid(normalizedValue) || sampleOffset < 0 || sampleOffset >= Request.MaxBlockFrames
            || !table.TryGetIndex(parameterId, out var index))
        {
            return false;
        }

        table.Set(index, normalizedValue);
        return _pipeline.Queue(parameterId, normalizedValue, sampleOffset);
    }

    /// <summary>The last known value of every parameter, as the host remembers it for recovery.</summary>
    public ImmutableArray<ParameterValue> GetCachedParameterValues() => _parameters?.Snapshot() ?? [];

    /// <summary>Sets a parameter through the control plane (not sample-accurate) and remembers it for recovery.</summary>
    public async Task SetParameterAsync(uint parameterId, double normalizedValue, CancellationToken cancellationToken = default)
    {
        if (!NormalizedValue.IsValid(normalizedValue))
        {
            throw new ArgumentOutOfRangeException(nameof(normalizedValue), normalizedValue, "Parameter values are normalized to [0, 1].");
        }

        var table = _parameters ?? throw new PluginUnavailableException("The instance has not started yet.");
        if (!table.TryGetIndex(parameterId, out var index))
        {
            throw new ArgumentOutOfRangeException(nameof(parameterId), parameterId, "Unknown parameter.");
        }

        table.Set(index, normalizedValue);
        if (_link is { } link)
        {
            Expect<Ack>(await _manager.RequestAsync(link.Worker, new SetParameter(Id, parameterId, normalizedValue), cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>Reads the live parameter values from the worker and refreshes the remembered values.</summary>
    public async Task<ImmutableArray<ParameterValue>> GetParametersAsync(CancellationToken cancellationToken = default)
    {
        var link = RequireLink();
        var values = Expect<ParameterValues>(await _manager.RequestAsync(link.Worker, new GetParameters(Id), cancellationToken).ConfigureAwait(false));
        _parameters?.SetAll(values.Values);
        return values.Values;
    }

    /// <summary>
    /// Captures state from the live plugin and keeps it as the recovery snapshot. Only a running instance can be
    /// asked: a crashed plugin cannot return state it never saved.
    /// </summary>
    public async Task<PluginStateSnapshot> CaptureStateAsync(CancellationToken cancellationToken = default)
    {
        var link = RequireLink();
        var result = Expect<StateResult>(await _manager.RequestAsync(link.Worker, new CaptureState(Id), cancellationToken).ConfigureAwait(false));
        var snapshot = new PluginStateSnapshot(Plugin, result.State, GetCachedParameterValues(), _manager.TimeProvider.GetUtcNow());
        _lastSnapshot = snapshot;
        return snapshot;
    }

    /// <summary>
    /// Runs this instance's plugin, a MIDI effect, offline over a timeline in its worker: a fresh copy given
    /// <paramref name="state"/> and <paramref name="parameters"/>, not the live instance, which is left as it is. The
    /// request must fit the protocol's limits (<see cref="ProtocolLimits.MaxRenderEvents"/> and the others). A worker
    /// that dies or does not answer in time throws <see cref="PluginUnavailableException"/> (and is recovered like any
    /// crash); a plugin that fails throws <see cref="PluginHostException"/>.
    /// </summary>
    public async Task<RenderedEvents> RenderEventsAsync(
        PluginStateData? state,
        ImmutableArray<ParameterValue> parameters,
        long endFrame,
        ImmutableArray<TimelineTempo> tempo,
        ImmutableArray<TimelineEvent> events,
        ImmutableArray<TimelineParameterChange> changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(endFrame);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(endFrame, ProtocolLimits.MaxRenderFrames);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(events.Length, ProtocolLimits.MaxRenderEvents, nameof(events));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(changes.Length, ProtocolLimits.MaxRenderParameterChanges, nameof(changes));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tempo.Length, ProtocolLimits.MaxRenderTempoChanges, nameof(tempo));
        var link = RequireLink();
        var request = new RenderEvents(Plugin, state, parameters, Request.SampleRate, Request.MaxBlockFrames, endFrame, tempo, events, changes, Request.ModulePath);
        return Expect<RenderedEvents>(await _manager.RequestAsync(link.Worker, request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Opens the plugin's editor in a window owned by its worker. Throws <see cref="PluginHostException"/> when the
    /// plugin has no editor that can be shown, <see cref="PluginUnavailableException"/> when it is not running.
    /// </summary>
    public async Task OpenEditorAsync(string title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(title);
        var link = RequireLink();
        Expect<Ack>(await _manager.RequestAsync(link.Worker, new OpenEditor(Id, title), cancellationToken).ConfigureAwait(false));
        _editorOpen = true;
    }

    /// <summary>
    /// Hides the editor's window, keeping the editor, or shows it again without taking focus. False when that could not
    /// be done (not running, no editor open, or a plugin-owned window that refused).
    /// </summary>
    public async Task<bool> SetEditorVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        if (_link is not { } link || !_editorOpen)
        {
            return false;
        }

        return await _manager.RequestAsync(link.Worker, new SetEditorVisible(Id, visible), cancellationToken).ConfigureAwait(false) switch
        {
            Ack => true,
            ErrorReply { Code: ErrorCode.NotSupported } => false,
            var other => Expect<Ack>(other) is not null,
        };
    }

    /// <summary>
    /// Sends the editor window's keys to Bluestone (<paramref name="toHost"/>) or to the plugin. Returns false when the
    /// platform or editor cannot (the keys then stay with the plugin), or the instance is not running.
    /// </summary>
    public async Task<bool> SetEditorKeyboardAsync(bool toHost, CancellationToken cancellationToken = default)
    {
        if (_link is not { } link || !_editorOpen)
        {
            return false;
        }

        return await _manager.RequestAsync(link.Worker, new SetEditorKeyboard(Id, toHost), cancellationToken).ConfigureAwait(false) switch
        {
            Ack => true,
            ErrorReply { Code: ErrorCode.NotSupported } => false,
            var other => Expect<Ack>(other) is not null,
        };
    }

    /// <summary>
    /// Shows what the header Bluestone draws on the editor window offers: each button hidden, off, on, or unavailable.
    /// Returns false when that could not be done (not running, no editor open, or a window the plugin owns).
    /// </summary>
    public async Task<bool> SetEditorHeaderAsync(EditorButtonState bypass, EditorButtonState pin, EditorButtonState keys, CancellationToken cancellationToken = default)
    {
        if (_link is not { } link || !_editorOpen)
        {
            return false;
        }

        return await _manager.RequestAsync(link.Worker, new SetEditorHeader(Id, bypass, pin, keys), cancellationToken).ConfigureAwait(false) switch
        {
            Ack => true,
            ErrorReply { Code: ErrorCode.NotSupported } => false,
            var other => Expect<Ack>(other) is not null,
        };
    }

    /// <summary>Closes the plugin's editor if it is open. Does nothing when the instance is not running.</summary>
    public async Task CloseEditorAsync(CancellationToken cancellationToken = default)
    {
        _editorOpen = false;
        if (_link is { } link)
        {
            Expect<Ack>(await _manager.RequestAsync(link.Worker, new CloseEditor(Id), cancellationToken).ConfigureAwait(false));
        }
    }

    internal void OnNotification(PluginNotification notification)
    {
        switch (notification.Kind)
        {
            case NotificationKind.ParameterEdited:
                if (_parameters is { } table && table.TryGetIndex(notification.ParameterId, out var index))
                {
                    table.Set(index, notification.Value);
                    ParameterEdited?.Invoke(this, new PluginParameterEditedEventArgs(notification.ParameterId, notification.Value));
                }

                return;
            case NotificationKind.LatencyChanged:
                PluginLatencyFrames = (int)notification.Value;
                break;
            case NotificationKind.EditorClosed:
                _editorOpen = false;
                break;
            case NotificationKind.EditorKey:
                EditorKeyPressed?.Invoke(this, EditorKey.From(notification));
                return;
            case NotificationKind.EditorButton:
                EditorButtonClicked?.Invoke(this, EditorButtonClick.From(notification));
                return;
        }

        Changed?.Invoke(this, notification.Kind);
    }

    /// <summary>
    /// Restarts an instance that is Unavailable or Quarantined: a new worker (as the isolation policy says), the
    /// instance recreated, the last snapshot restored, then the remembered parameter values applied. Resets the
    /// restart counters. Returns the resulting status; failure leaves the instance Unavailable with the reason.
    /// </summary>
    public Task<PluginInstanceStatus> RestartAsync(CancellationToken cancellationToken = default) =>
        _manager.RestartAsync(this, manual: true, cancellationToken);

    /// <summary>Test-only: tells the worker to misbehave.</summary>
    internal Task<bool> InduceTestFaultAsync(TestFault fault) =>
        _link is { } link ? link.Worker.TrySendAsync(new InduceTestFault(fault), TimeSpan.FromSeconds(5)) : Task.FromResult(false);

    internal uint NextGeneration() => Interlocked.Increment(ref _generation);

    internal ParameterTable EnsureParameterTable(ImmutableArray<ParameterDescriptor> descriptors)
    {
        var existing = _parameters;
        if (existing is not null && existing.Descriptors.SequenceEqual(descriptors))
        {
            return existing;
        }

        var table = new ParameterTable(descriptors, existing);
        _parameters = table;
        return table;
    }

    /// <summary>Connects the running worker; Starting or Restarting to Running.</summary>
    internal bool Attach(ActiveLink link, int pluginLatency, string? stateRestoreError)
    {
        lock (_statusGate)
        {
            if (_status.State is not (PluginInstanceState.Starting or PluginInstanceState.Restarting))
            {
                return false;
            }

            PluginLatencyFrames = pluginLatency;
            LastStateRestoreError = stateRestoreError;
            _editorOpen = false;
            _lastWorkerLoss = null;
            _link = link;
            SetStatusLocked(PluginInstanceStatus.Running);
        }

        DispatchStatusEvents();
        return true;
    }

    /// <summary>
    /// The worker behind this instance ended. Faults the exchange first (so the audio thread stops touching it), drops
    /// the link, bumps the generation, and reports Unavailable once. Returns the old link for cleanup, or null if this
    /// worker was not (or no longer) the instance's.
    /// </summary>
    internal ActiveLink? Detach(WorkerProcess worker, PluginInstanceStatus unavailable, WorkerLoss loss)
    {
        ActiveLink? link;
        lock (_statusGate)
        {
            link = _link;
            if (link is null || link.Worker != worker)
            {
                return null;
            }

            link.Exchange.MarkFaulted();
            _link = null;
            _lastWorkerLoss = loss;
            NextGeneration();
            SetStatusLocked(unavailable);
        }

        DispatchStatusEvents();
        return link;
    }

    /// <summary>
    /// A planned move to another worker: Running to Restarting. Faults the exchange first (the audio thread then outputs
    /// the failure policy), drops the link, and bumps the generation. Returns the old link for cleanup, or null if the
    /// instance was not running (its worker may have just died).
    /// </summary>
    internal ActiveLink? Suspend()
    {
        ActiveLink? link;
        lock (_statusGate)
        {
            link = _link;
            if (link is null || _status.State != PluginInstanceState.Running)
            {
                return null;
            }

            link.Exchange.MarkFaulted();
            _link = null;
            _editorOpen = false;
            NextGeneration();
            SetStatusLocked(PluginInstanceStatus.Restarting);
        }

        DispatchStatusEvents();
        return link;
    }

    /// <summary>Moves to <paramref name="next"/> if the current state is one of <paramref name="from"/>.</summary>
    internal bool TryTransition(PluginInstanceStatus next, params ReadOnlySpan<PluginInstanceState> from)
    {
        lock (_statusGate)
        {
            if (!from.Contains(_status.State))
            {
                return false;
            }

            SetStatusLocked(next);
        }

        DispatchStatusEvents();
        return true;
    }

    /// <summary>Final: drops the link and moves to Unloaded from any state. Returns the old link for cleanup.</summary>
    internal ActiveLink? Unload()
    {
        ActiveLink? link;
        lock (_statusGate)
        {
            if (_status.State == PluginInstanceState.Unloaded)
            {
                return null;
            }

            link = _link;
            link?.Exchange.MarkFaulted();
            _link = null;
            SetStatusLocked(PluginInstanceStatus.Unloaded);
        }

        CancelPendingRestart();
        Interlocked.Exchange(ref _snapshotLoop, null)?.Cancel();
        DispatchStatusEvents();
        return link;
    }

    internal void SetPendingRestart(CancellationTokenSource restart) => Interlocked.Exchange(ref _pendingRestart, restart)?.Cancel();

    internal void CancelPendingRestart() => Interlocked.Exchange(ref _pendingRestart, null)?.Cancel();

    internal void StartSnapshotLoop(TimeSpan interval, CancellationToken shutdown)
    {
        var loop = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        if (Interlocked.CompareExchange(ref _snapshotLoop, loop, null) is not null)
        {
            loop.Dispose();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(interval, _manager.TimeProvider, loop.Token).ConfigureAwait(false);
                    if (_link is not null)
                    {
                        try
                        {
                            await CaptureStateAsync(loop.Token).ConfigureAwait(false);
                        }
                        catch (PluginHostException)
                        {
                            // The worker went away between the check and the request; supervision reports that.
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private static T Expect<T>(ProtocolMessage reply)
        where T : ProtocolMessage => reply switch
        {
            T expected => expected,
            ErrorReply error => throw new PluginHostException(error.Code, error.Message),
            _ => throw new PluginHostException($"Unexpected reply {reply.Type}."),
        };

    private ActiveLink RequireLink() =>
        _link ?? throw new PluginUnavailableException($"The plugin instance is {Status}; only a running instance can be asked.");

    private void SetStatusLocked(PluginInstanceStatus next)
    {
        if (!PluginInstanceStatus.IsAllowed(_status.State, next.State))
        {
            throw new InvalidOperationException($"Invalid plugin status transition {_status} -> {next}.");
        }

        _statusEvents.Enqueue(new PluginStatusChangedEventArgs(_status, next));
        _status = next;
    }

    // Raises queued events in order, one dispatcher at a time, outside the status lock.
    private void DispatchStatusEvents()
    {
        while (!_statusEvents.IsEmpty)
        {
            if (Interlocked.CompareExchange(ref _dispatching, 1, 0) != 0)
            {
                return;
            }

            try
            {
                while (_statusEvents.TryDequeue(out var args))
                {
                    try
                    {
                        StatusChanged?.Invoke(this, args);
                    }
#pragma warning disable CA1031 // A failing subscriber must not break supervision.
                    catch (Exception)
#pragma warning restore CA1031
                    {
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _dispatching, 0);
            }
        }
    }
}

/// <summary>
/// A worker process that ended under running instances: its process id, why (Crashed, Hung, or ProtocolError), and every
/// instance that was running in it. With a shared worker that is more than one: they went down together.
/// </summary>
public sealed record WorkerLoss(int ProcessId, UnavailableReason Reason, ImmutableArray<PluginInstance> Instances);

/// <summary>The live connection of a running instance: its worker and its mapped exchange.</summary>
internal sealed record ActiveLink(WorkerProcess Worker, HostBlockExchange Exchange);

/// <summary>Latency arithmetic of the block pipeline.</summary>
public static class PipelineLatency
{
    /// <summary>Plugin latency plus (depth - 1) blocks. Scheduling jitter comes on top and is not included.</summary>
    public static int ReportedLatencyFrames(int pluginLatencyFrames, int pipelineDepth, int blockFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pluginLatencyFrames);
        ArgumentOutOfRangeException.ThrowIfLessThan(pipelineDepth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockFrames, 1);
        return pluginLatencyFrames + ((pipelineDepth - 1) * blockFrames);
    }

    /// <summary>The block whose result is collected while block <paramref name="submittedBlock"/> is submitted; negative while priming.</summary>
    public static long CollectedBlock(long submittedBlock, int pipelineDepth) => submittedBlock - (pipelineDepth - 1);
}

/// <summary>Remembered parameter values, readable from any thread, written by the audio thread or control threads.</summary>
internal sealed class ParameterTable
{
    private readonly Dictionary<uint, int> _index = [];
    private readonly double[] _values;

    public ParameterTable(ImmutableArray<ParameterDescriptor> descriptors, ParameterTable? previous)
    {
        Descriptors = descriptors;
        _values = new double[descriptors.Length];
        for (var i = 0; i < descriptors.Length; i++)
        {
            _index[descriptors[i].Id] = i;
            _values[i] = previous is not null && previous.TryGetIndex(descriptors[i].Id, out var old)
                ? previous.Get(old)
                : descriptors[i].DefaultValue;
        }
    }

    public ImmutableArray<ParameterDescriptor> Descriptors { get; }

    public bool TryGetIndex(uint id, out int index) => _index.TryGetValue(id, out index);

    public double Get(int index) => Volatile.Read(ref _values[index]);

    public void Set(int index, double value) => Volatile.Write(ref _values[index], value);

    public void SetAll(ImmutableArray<ParameterValue> values)
    {
        foreach (var value in values)
        {
            if (TryGetIndex(value.Id, out var index))
            {
                Set(index, value.Value);
            }
        }
    }

    public ImmutableArray<ParameterValue> Snapshot()
    {
        var builder = ImmutableArray.CreateBuilder<ParameterValue>(Descriptors.Length);
        for (var i = 0; i < Descriptors.Length; i++)
        {
            builder.Add(new ParameterValue(Descriptors[i].Id, Get(i)));
        }

        return builder.MoveToImmutable();
    }
}
