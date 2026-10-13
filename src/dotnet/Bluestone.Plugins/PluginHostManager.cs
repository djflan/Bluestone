using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;
using Bluestone.Plugins.Workers;

namespace Bluestone.Plugins;

/// <summary>
/// Creates, supervises, recovers, and destroys plugin instances, each hosted in a worker process chosen by its
/// <see cref="HostingMode"/> (instances with the same <see cref="WorkerKeys">worker key</see> share one). Plugins never run in this process: when a worker cannot be launched or fails its
/// handshake, the instance becomes Unavailable(FailedToStart) and nothing falls back to in-process hosting.
/// </summary>
public sealed class PluginHostManager : IAsyncDisposable
{
    private readonly PluginHostOptions _options;
    private readonly bool _ownsDataDirectory;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, WorkerSlot> _workers = [];
    private readonly List<PluginInstance> _instances = [];
    private readonly ConcurrentBag<WorkerProcess> _launched = [];
    private readonly CancellationTokenSource _shutdown = new();
    private int _workerLaunches;
    private int _disposed;

    public PluginHostManager(PluginHostOptions? options = null)
    {
        _options = options ?? new PluginHostOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.SlotCount, ProtocolLimits.MinSlotCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.SlotCount, ProtocolLimits.MaxSlotCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.PipelineDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.PipelineDepth, _options.SlotCount);
        _ownsDataDirectory = _options.DataDirectory is null;
        DataDirectory = Path.GetFullPath(_options.DataDirectory
            ?? Path.Combine(Path.GetTempPath(), "bluestone-plugins", $"{Environment.ProcessId}-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(DataDirectory);
    }

    /// <summary>Where data-plane files live.</summary>
    public string DataDirectory { get; }

    /// <summary>Worker processes launched so far, including ones that failed to start.</summary>
    public int WorkerLaunchCount => Volatile.Read(ref _workerLaunches);

    public IReadOnlyList<PluginInstance> Instances
    {
        get
        {
            lock (_gate)
            {
                return [.. _instances];
            }
        }
    }

    internal TimeProvider TimeProvider => _options.TimeProvider;

    /// <summary>
    /// A worker process ended under running instances (crashed, hung, or misbehaved), after every one of them has been
    /// marked Unavailable. Raised once per worker, on a thread-pool thread.
    /// </summary>
    public event EventHandler<WorkerLoss>? WorkerLost;

    /// <summary>
    /// Creates an instance and starts it in a worker. Always returns the instance: if it could not start, its status
    /// says why (Unavailable with FailedToStart, Missing, ProtocolError, ...), and the restart policy applies.
    /// <see cref="HostingMode.WithinBluestone"/> throws: <see cref="InvalidOperationException"/> unless explicitly
    /// allowed, and <see cref="NotSupportedException"/> even then, because in-process hosting is deferred.
    /// </summary>
    public async Task<PluginInstance> CreateInstanceAsync(PluginInstanceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        RefuseWithinBluestone(request.Hosting);

        if (request.FailurePolicy == FailurePolicy.BypassInput
            && (request.Plugin.Kind != PluginKind.AudioEffect || request.InputChannels != request.OutputChannels || request.InputChannels == 0))
        {
            throw new ArgumentException("BypassInput applies only to audio effects whose input and output channel counts match.", nameof(request));
        }

        if (request.RestoreFrom is { } snapshot
            && (snapshot.Plugin.Format != request.Plugin.Format || snapshot.Plugin.PluginId != request.Plugin.PluginId))
        {
            throw new ArgumentException("The snapshot belongs to a different plugin.", nameof(request));
        }

        var instance = new PluginInstance(this, request, _options.PipelineDepth, _options.CollectWait);
        lock (_gate)
        {
            _instances.Add(instance);
        }

        await instance.Lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartAsync(instance, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            instance.Lifecycle.Release();
        }

        return instance;
    }

    /// <summary>Destroys an instance: Unloaded, data plane closed, worker told (and released when no longer used).</summary>
    public async Task DestroyInstanceAsync(PluginInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        await instance.Lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (instance.Unload() is { } link)
            {
                link.Exchange.MarkClosed();
                link.Exchange.Dispose();
                try
                {
                    await RequestAsync(link.Worker, new DestroyInstance(instance.Id), cancellationToken).ConfigureAwait(false);
                }
                catch (PluginHostException)
                {
                    // The worker is gone or refused; either way the instance no longer exists for us.
                }

                TryDelete(link.Exchange.Path);
            }

            await ReleaseWorkerAsync(instance).ConfigureAwait(false);
            lock (_gate)
            {
                _instances.Remove(instance);
            }
        }
        finally
        {
            instance.Lifecycle.Release();
        }
    }

    /// <summary>
    /// Kills every worker process at once, without unloading anything. For quitting when <see cref="DisposeAsync"/> has
    /// taken too long: a worker stuck in plugin code would not notice its pipe closing and would outlive Bluestone.
    /// </summary>
    public void KillWorkers()
    {
        foreach (var worker in _launched)
        {
            worker.Kill(WorkerExitReason.ShutDown);
        }
    }

    /// <summary>Unloads every instance and stops every worker; no worker process outlives this call.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var instance in Instances)
        {
            var locked = await instance.Lifecycle.WaitAsync(_options.HandshakeTimeout + _options.RequestTimeout).ConfigureAwait(false);
            try
            {
                if (instance.Unload() is { } link)
                {
                    link.Exchange.MarkClosed();
                    link.Exchange.Dispose();
                    TryDelete(link.Exchange.Path);
                }
            }
            finally
            {
                if (locked)
                {
                    instance.Lifecycle.Release();
                }
            }
        }

        List<WorkerSlot> slots;
        lock (_gate)
        {
            slots = [.. _workers.Values];
            _workers.Clear();
            _instances.Clear();
        }

        foreach (var slot in slots)
        {
            try
            {
                await slot.Launch.WaitAsync(_options.HandshakeTimeout + TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WorkerStartException or OperationCanceledException or TimeoutException)
            {
                // Never started; StartAsync already cleaned up.
            }
        }

        await Task.WhenAll(_launched.Select(w => w.DisposeAsync().AsTask())).ConfigureAwait(false);
        if (_ownsDataDirectory)
        {
            try
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        // _shutdown stays undisposed on purpose: late supervision callbacks may still read its (cancelled) token.
    }

    /// <summary>
    /// Hosts <paramref name="instance"/> as <paramref name="mode"/> says from now on. When that means another worker, a
    /// running instance has its state captured first, is moved (Running, Restarting, Running), and keeps its identity,
    /// parameters, and state; a crashed, hung, or quarantined one is restarted there from its last snapshot, as a manual
    /// restart would. When the worker key stays the same nothing is restarted. Returns the resulting status; a move that
    /// fails leaves the instance Unavailable with the reason, like any start.
    /// </summary>
    public async Task<PluginInstanceStatus> ChangeHostingAsync(PluginInstance instance, HostingMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        RefuseWithinBluestone(mode);
        await instance.Lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = instance.Status.State;
            var moves = KeyFor(instance, mode) != (instance.WorkerKey ?? KeyFor(instance, instance.Hosting));
            instance.Hosting = mode;
            if (!moves || state == PluginInstanceState.Unloaded)
            {
                return instance.Status;
            }

            if (state == PluginInstanceState.Running)
            {
                try
                {
                    await instance.CaptureStateAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is PluginHostException or PluginUnavailableException)
                {
                    // The plugin would not give its state (or its worker just died): the last snapshot is restored instead.
                }

                if (instance.Suspend() is { } link)
                {
                    link.Exchange.MarkClosed();
                    link.Exchange.Dispose();
                    try
                    {
                        await RequestAsync(link.Worker, new DestroyInstance(instance.Id), cancellationToken).ConfigureAwait(false);
                    }
                    catch (PluginHostException)
                    {
                        // The old worker is gone or refused; the instance no longer runs there either way.
                    }

                    TryDelete(link.Exchange.Path);
                    await ReleaseWorkerAsync(instance).ConfigureAwait(false);
                    await StartAsync(instance, cancellationToken).ConfigureAwait(false);
                    return instance.Status;
                }
            }

            // Not running (or its worker died while it was being moved): restart it in its new worker.
            instance.CancelPendingRestart();
            lock (instance.GuardGate)
            {
                instance.Guard.Reset();
            }

            if (instance.TryTransition(PluginInstanceStatus.Restarting, PluginInstanceState.Unavailable, PluginInstanceState.Quarantined))
            {
                await StartAsync(instance, cancellationToken).ConfigureAwait(false);
            }

            return instance.Status;
        }
        finally
        {
            instance.Lifecycle.Release();
        }
    }

    internal async Task<PluginInstanceStatus> RestartAsync(PluginInstance instance, bool manual, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await instance.Lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (manual)
            {
                if (instance.Status.State is not (PluginInstanceState.Unavailable or PluginInstanceState.Quarantined))
                {
                    throw new InvalidOperationException($"Only an unavailable or quarantined instance can be restarted; it is {instance.Status}.");
                }

                instance.CancelPendingRestart();
                lock (instance.GuardGate)
                {
                    instance.Guard.Reset();
                }
            }

            var from = manual
                ? new[] { PluginInstanceState.Unavailable, PluginInstanceState.Quarantined }
                : [PluginInstanceState.Unavailable];
            if (instance.TryTransition(PluginInstanceStatus.Restarting, from))
            {
                await StartAsync(instance, cancellationToken).ConfigureAwait(false);
            }

            return instance.Status;
        }
        finally
        {
            instance.Lifecycle.Release();
        }
    }

    /// <summary>A control request with the per-request timeout. A timeout means the worker is hung: it is killed.</summary>
    internal async Task<ProtocolMessage> RequestAsync(WorkerProcess worker, ProtocolMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await worker.RequestAsync(request, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            worker.Kill(WorkerExitReason.Hung);
            throw new PluginUnavailableException("The plugin worker did not answer in time and was stopped.", ex);
        }
    }

    private static string KeyFor(PluginInstance instance, HostingMode mode) =>
        WorkerKeys.For(mode, instance.Plugin, instance.Id, RuntimeInformation.ProcessArchitecture);

    private void RefuseWithinBluestone(HostingMode mode)
    {
        if (mode == HostingMode.WithinBluestone)
        {
            if (!_options.AllowWithinBluestone)
            {
                throw new InvalidOperationException("In-process plugin hosting is not allowed. Plugins run in worker processes.");
            }

            throw new NotSupportedException("Hosting plugins within Bluestone is deferred and not implemented; plugins run only in worker processes.");
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown hosting mode.");
        }
    }

    private static UnavailableReason ReasonFor(WorkerExitReason reason) => reason switch
    {
        WorkerExitReason.Hung => UnavailableReason.Hung,
        WorkerExitReason.ProtocolError => UnavailableReason.ProtocolError,
        _ => UnavailableReason.Crashed,
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still mapped on Windows, or already gone; the data directory is removed on dispose.
        }
    }

    // Caller holds instance.Lifecycle and the instance is Starting or Restarting.
    private async Task StartAsync(PluginInstance instance, CancellationToken cancellationToken)
    {
        WorkerProcess? worker = null;
        HostBlockExchange? exchange = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            var token = linked.Token;
            var generation = instance.NextGeneration();
            worker = await AcquireWorkerAsync(instance, token).ConfigureAwait(false);
            var request = instance.Request;
            var options = new ExchangeOptions(request.MaxBlockFrames, request.InputChannels, request.OutputChannels, _options.SlotCount, request.EventCapacity, request.ParameterChangeCapacity);
            var reply = await RequestAsync(
                worker,
                new CreateInstance(instance.Id, request.Plugin, request.SampleRate, options, generation, DataDirectory, instance.LastSnapshot?.State, request.ModulePath),
                token).ConfigureAwait(false);
            var created = reply switch
            {
                InstanceCreated c => c,
                ErrorReply { Code: ErrorCode.PluginNotFound } e => throw new StartFailure(UnavailableReason.Missing, e.Message),
                ErrorReply e => throw new StartFailure(UnavailableReason.FailedToStart, e.Message),
                _ => throw new StartFailure(UnavailableReason.ProtocolError, $"Unexpected reply {reply.Type} to CreateInstance."),
            };
            if (created.InstanceId != instance.Id || created.Generation != generation || !IsInDataDirectory(created.DataPlanePath))
            {
                worker.Kill(WorkerExitReason.ProtocolError);
                throw new StartFailure(UnavailableReason.ProtocolError, "The worker reported an instance that does not match the request.");
            }

            try
            {
                exchange = HostBlockExchange.Open(created.DataPlanePath, generation, options);
            }
            catch (Exception ex) when (ex is ProtocolException or IOException or UnauthorizedAccessException)
            {
                worker.Kill(WorkerExitReason.ProtocolError);
                throw new StartFailure(UnavailableReason.ProtocolError, $"The worker's data-plane file is unusable: {ex.Message}");
            }

            var hadValues = instance.ParameterTable is not null;
            var table = instance.EnsureParameterTable(created.Parameters);
            if (hadValues)
            {
                // Recovery: the snapshot's state went in with CreateInstance; the remembered values are newer. Only the
                // values that differ from what the restored plugin holds are sent (a plugin may have thousands).
                var live = await RequestAsync(worker, new GetParameters(instance.Id), token).ConfigureAwait(false) is ParameterValues current
                    ? current.Values.ToDictionary(v => v.Id, v => v.Value)
                    : [];
                foreach (var value in table.Snapshot())
                {
                    if (!live.TryGetValue(value.Id, out var now) || Math.Abs(now - value.Value) > 1e-9)
                    {
                        await RequestAsync(worker, new SetParameter(instance.Id, value.Id, value.Value), token).ConfigureAwait(false);
                    }
                }
            }
            else if (await RequestAsync(worker, new GetParameters(instance.Id), token).ConfigureAwait(false) is ParameterValues values)
            {
                table.SetAll(values.Values);
            }

            if (!instance.Attach(new ActiveLink(worker, exchange), created.LatencyFrames, created.StateRestoreError))
            {
                throw new OperationCanceledException("The instance was unloaded while starting.");
            }

            lock (_gate)
            {
                instance.Slot?.Attached.Add(instance);
            }

            if (!worker.IsAlive)
            {
                // It died between the reply and the attach, so supervision may have missed this instance.
                var reason = await worker.Exited.ConfigureAwait(false);
                var loss = new WorkerLoss(worker.ProcessId, ReasonFor(reason), [instance]);
                if (await HandleWorkerLostAsync(instance, worker, reason, loss).ConfigureAwait(false))
                {
                    RaiseWorkerLost(loss);
                }
            }
            else if (_options.PeriodicSnapshotInterval is { } interval)
            {
                instance.StartSnapshotLoop(interval, _shutdown.Token);
            }
        }
        catch (Exception ex) when (ex is StartFailure or WorkerStartException or PluginUnavailableException or OperationCanceledException)
        {
            exchange?.Dispose();
            var status = ex switch
            {
                StartFailure f => PluginInstanceStatus.Unavailable(f.Reason, f.Message),
                WorkerStartException w => PluginInstanceStatus.Unavailable(w.Reason, w.Message),
                PluginUnavailableException when worker is not null =>
                    PluginInstanceStatus.Unavailable(ReasonFor(await ExitReasonAsync(worker).ConfigureAwait(false)), ex.Message),
                PluginUnavailableException => PluginInstanceStatus.Unavailable(UnavailableReason.Crashed, ex.Message),
                _ => PluginInstanceStatus.Unavailable(UnavailableReason.FailedToStart, "Start was cancelled."),
            };
            await ReleaseWorkerAsync(instance).ConfigureAwait(false);
            if (instance.TryTransition(status, PluginInstanceState.Starting, PluginInstanceState.Restarting))
            {
                ApplyRestartPolicy(instance);
            }

            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }

    // A worker that failed a request is dead or being killed; its exit reason says which status applies.
    private static async Task<WorkerExitReason> ExitReasonAsync(WorkerProcess worker)
    {
        try
        {
            return await worker.Exited.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            worker.Kill(WorkerExitReason.Crashed);
            return WorkerExitReason.Crashed;
        }
    }

    private async Task<WorkerProcess> AcquireWorkerAsync(PluginInstance instance, CancellationToken cancellationToken)
    {
        var key = KeyFor(instance, instance.Hosting);
        WorkerSlot slot;
        lock (_gate)
        {
            if (!_workers.TryGetValue(key, out slot!) || slot.IsDead)
            {
                slot = new WorkerSlot(key, LaunchAsync());
                _workers[key] = slot;
            }

            slot.Users++;
            instance.Slot = slot;
        }

        try
        {
            var worker = await slot.Launch.WaitAsync(cancellationToken).ConfigureAwait(false);
            return worker.IsAlive ? worker : throw new PluginUnavailableException("The shared worker has already ended.");
        }
        catch (WorkerStartException)
        {
            lock (_gate)
            {
                if (_workers.TryGetValue(key, out var current) && current == slot)
                {
                    _workers.Remove(key);
                }
            }

            throw;
        }
    }

    private async Task<WorkerProcess> LaunchAsync()
    {
        await Task.Yield();
        Interlocked.Increment(ref _workerLaunches);
        var worker = await WorkerProcess.StartAsync(
            WorkerMode.InstanceHost,
            new WorkerProcessOptions
            {
                WorkerPath = _options.WorkerPath,
                Environment = _options.WorkerEnvironment,
                HandshakeTimeout = _options.HandshakeTimeout,
                HeartbeatInterval = _options.HeartbeatInterval,
                HeartbeatTimeout = _options.HeartbeatTimeout,
                ShutdownTimeout = _options.ShutdownTimeout,
                OnNotification = OnNotification,
            },
            OnWorkerTerminated,
            _shutdown.Token).ConfigureAwait(false);
        _launched.Add(worker);
        if (Volatile.Read(ref _disposed) != 0)
        {
            await worker.DisposeAsync().ConfigureAwait(false);
        }

        return worker;
    }

    // With lost set, releases the instance's slot only if it is still that worker's: once the loss is reported, a
    // restart may already have given the instance a new slot, which must not be released here.
    private async Task ReleaseWorkerAsync(PluginInstance instance, WorkerProcess? lost = null)
    {
        WorkerProcess? toDispose = null;
        lock (_gate)
        {
            if (instance.Slot is not { } slot)
            {
                return;
            }

            if (lost is not null && !(slot.Launch.IsCompletedSuccessfully && slot.Launch.Result == lost))
            {
                return;
            }

            instance.Slot = null;
            slot.Attached.Remove(instance);
            slot.Users--;
            if (slot.Users <= 0)
            {
                slot.IsDead = true;
                if (_workers.TryGetValue(slot.Key, out var current) && current == slot)
                {
                    _workers.Remove(slot.Key);
                }

                if (slot.Launch.IsCompletedSuccessfully)
                {
                    toDispose = slot.Launch.Result;
                }
            }
        }

        if (toDispose is not null)
        {
            await toDispose.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Runs on a worker's read loop: something a plugin did on its own.
    private void OnNotification(WorkerProcess worker, PluginNotification notification)
    {
        PluginInstance? target;
        lock (_gate)
        {
            target = _instances.FirstOrDefault(i => i.Id == notification.InstanceId);
        }

        if (target is not null && target.Link?.Worker == worker)
        {
            target.OnNotification(notification);
        }
    }

    // Runs on a thread-pool thread, raised once per worker by WorkerProcess.
    private void OnWorkerTerminated(WorkerProcess worker, WorkerExitReason reason)
    {
        List<PluginInstance> affected;
        lock (_gate)
        {
            affected = [];
            foreach (var slot in _workers.Values.Where(s => s.Launch.IsCompletedSuccessfully && s.Launch.Result == worker).ToList())
            {
                slot.IsDead = true;
                _workers.Remove(slot.Key);
                affected.AddRange(slot.Attached);
            }
        }

        var loss = new WorkerLoss(worker.ProcessId, ReasonFor(reason), [.. affected.Where(i => i.Link?.Worker == worker)]);
        _ = Task.Run(async () =>
        {
            var lost = false;
            foreach (var instance in affected)
            {
                lost |= await HandleWorkerLostAsync(instance, worker, reason, loss).ConfigureAwait(false);
            }

            await worker.DisposeAsync().ConfigureAwait(false);
            if (lost)
            {
                RaiseWorkerLost(loss);
            }
        });
    }

    // Returns whether the instance was still on this worker, so the loss was its.
    private async Task<bool> HandleWorkerLostAsync(PluginInstance instance, WorkerProcess worker, WorkerExitReason reason, WorkerLoss loss)
    {
        var message = $"The plugin worker (process {worker.ProcessId}) ended: {reason}.";
        if (instance.Detach(worker, PluginInstanceStatus.Unavailable(ReasonFor(reason), message), loss) is not { } link)
        {
            return false;
        }

        link.Exchange.Dispose();
        TryDelete(link.Exchange.Path);
        await ReleaseWorkerAsync(instance, worker).ConfigureAwait(false);
        ApplyRestartPolicy(instance);
        return true;
    }

    private void RaiseWorkerLost(WorkerLoss loss)
    {
        try
        {
            WorkerLost?.Invoke(this, loss);
        }
#pragma warning disable CA1031 // A failing subscriber must not break supervision.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void ApplyRestartPolicy(PluginInstance instance)
    {
        var policy = instance.RestartPolicy;
        if (!policy.AutoRestart || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        RestartDecision decision;
        lock (instance.GuardGate)
        {
            decision = instance.Guard.RecordFailure(_options.TimeProvider.GetUtcNow());
        }

        if (decision.Quarantine)
        {
            instance.TryTransition(
                PluginInstanceStatus.Quarantined($"Failed again after {policy.MaxRestarts} automatic restarts; restart it manually."),
                PluginInstanceState.Unavailable);
            return;
        }

        var restart = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        instance.SetPendingRestart(restart);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(decision.Delay, _options.TimeProvider, restart.Token).ConfigureAwait(false);
                await RestartAsync(instance, manual: false, restart.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
        });
    }

    private bool IsInDataDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = DataDirectory.EndsWith(Path.DirectorySeparatorChar) ? DataDirectory : DataDirectory + Path.DirectorySeparatorChar;
        return full.StartsWith(directory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && Path.GetDirectoryName(full) == Path.TrimEndingDirectorySeparator(DataDirectory);
    }

    /// <summary>A worker process shared by the instances with the same worker key.</summary>
    internal sealed class WorkerSlot(string key, Task<WorkerProcess> launch)
    {
        public string Key { get; } = key;

        public Task<WorkerProcess> Launch { get; } = launch;

        /// <summary>Instances that reserved this worker (starting or running).</summary>
        public int Users { get; set; }

        /// <summary>Instances running on this worker.</summary>
        public HashSet<PluginInstance> Attached { get; } = [];

        public bool IsDead { get; set; }
    }

    private sealed class StartFailure(UnavailableReason reason, string message) : Exception(message)
    {
        public UnavailableReason Reason { get; } = reason;
    }
}
