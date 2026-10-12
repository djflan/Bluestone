using System.Threading.Channels;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;
using Bluestone.PluginWorker.Native;

namespace Bluestone.PluginWorker;

/// <summary>
/// Instance-host mode: serves control requests one at a time until the host sends Shutdown or the pipe closes.
/// Every request gets exactly one reply with the request's id; failures become <see cref="ErrorReply"/>. Requests are
/// carried out on the worker's main thread. What native plugins do on their own (edits from their editors, latency
/// changes, an editor closing) is sent as <see cref="PluginNotification"/> frames with request id 0.
/// </summary>
internal sealed class InstanceHostSession(Stream pipe, WorkerMainThread mainThread) : IDisposable
{
    private readonly Dictionary<PluginInstanceId, HostedInstance> _instances = [];
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Channel<PluginNotification> _notifications = Channel.CreateUnbounded<PluginNotification>(new UnboundedChannelOptions { SingleReader = true });

    public void Dispose() => _write.Dispose();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var notifier = Task.Run(() => SendNotificationsAsync(cancellationToken), cancellationToken);
        try
        {
            while (true)
            {
                var frame = await FrameCodec.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (frame is not { } request || request.Message is Shutdown)
                {
                    return;
                }

                if (request.Message is InduceTestFault fault)
                {
                    await WriteLockedAsync(() => WorkerFaults.InduceAsync(fault.Fault, pipe, cancellationToken), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var reply = mainThread.Invoke(() => Handle(request.Message));
                await WriteLockedAsync(() => FrameCodec.WriteAsync(pipe, request.RequestId, reply, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _notifications.Writer.TryComplete();
            mainThread.Invoke(() =>
            {
                foreach (var instance in _instances.Values)
                {
                    instance.Dispose();
                }

                _instances.Clear();
            });
            await Task.WhenAny(notifier, Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None)).ConfigureAwait(false);
        }
    }

    private async Task WriteLockedAsync(Func<Task> write, CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await write().ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task SendNotificationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var notification in _notifications.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await WriteLockedAsync(() => FrameCodec.WriteAsync(pipe, 0, notification, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The host went away.
        }
    }

    private ProtocolMessage Handle(ProtocolMessage message) => message switch
    {
        Ping ping => new Pong(ping.Sequence),
        CreateInstance create => Create(create),
        DestroyInstance destroy => Destroy(destroy.InstanceId),
        CaptureState capture => WithInstance(capture.InstanceId, i => i.TrySaveState(out var state, out var error)
            ? new StateResult(i.Id, state!)
            : new ErrorReply(ErrorCode.PluginError, error ?? "The plugin did not save its state.")),
        RestoreState restore => WithInstance(restore.InstanceId, i => ReportingLatency(i, () =>
        {
            var error = i.TryLoadState(restore.State);
            return new RestoreStateResult(i.Id, error is null, error);
        })),
        SetParameter set => WithInstance(set.InstanceId, i => ReportingLatency(i, () => i.TrySetParameter(set.ParameterId, set.Value)
            ? new Ack()
            : new ErrorReply(ErrorCode.InvalidRequest, $"Unknown parameter {set.ParameterId}."))),
        GetParameters get => WithInstance(get.InstanceId, i => new ParameterValues(i.Id, [.. i.GetParameters()])),
        RenderEvents render => OfflineRender.Run(render),
        OpenEditor open => WithInstance(open.InstanceId, i => i.OpenEditor(open.Title) is { } error
            ? new ErrorReply(ErrorCode.NotSupported, error)
            : new Ack()),
        CloseEditor close => WithInstance(close.InstanceId, i =>
        {
            i.CloseEditor();
            return new Ack();
        }),
        SetEditorVisible show => WithInstance(show.InstanceId, i => i.SetEditorVisible(show.Visible) is { } error
            ? new ErrorReply(ErrorCode.NotSupported, error)
            : new Ack()),
        SetEditorKeyboard keys => WithInstance(keys.InstanceId, i => i.RouteEditorKeys(keys.ToHost) is { } error
            ? new ErrorReply(ErrorCode.NotSupported, error)
            : new Ack()),
        _ => new ErrorReply(ErrorCode.InvalidRequest, $"{message.Type} is not a request this worker serves."),
    };

    // A parameter or state that changes a plugin's latency is announced, as native plugins announce theirs. The reply
    // is written first, so the host sees the notification after the request completes.
    private ProtocolMessage ReportingLatency(HostedInstance instance, Func<ProtocolMessage> action)
    {
        var before = instance.LatencyFrames;
        var reply = action();
        var after = instance.LatencyFrames;
        if (after != before)
        {
            _notifications.Writer.TryWrite(new PluginNotification(instance.Id, NotificationKind.LatencyChanged, 0, after));
        }

        return reply;
    }

    private ProtocolMessage Create(CreateInstance request)
    {
        if (_instances.ContainsKey(request.InstanceId))
        {
            return new ErrorReply(ErrorCode.InstanceExists, $"Instance {request.InstanceId} already exists.");
        }

        var plugin = HostedPlugins.Create(request.Plugin, request.ModulePath, out var createError);
        if (plugin is null)
        {
            return new ErrorReply(ErrorCode.PluginNotFound, createError ?? $"Plugin {request.Plugin} is not available in this worker.");
        }

        var options = request.Exchange;
        try
        {
            plugin.Prepare(request.SampleRate, options.MaxFrames, options.InputChannels, options.OutputChannels);
        }
        catch (NotSupportedException ex)
        {
            (plugin as IDisposable)?.Dispose();
            return new ErrorReply(ErrorCode.NotSupported, ex.Message);
        }

        string? stateError = null;
        if (request.State is { } state)
        {
            try
            {
                plugin.LoadState(state);
            }
            catch (InvalidDataException ex)
            {
                stateError = ex.Message;
            }
        }

        WorkerBlockExchange exchange;
        try
        {
            var path = Path.Combine(request.DataPlaneDirectory, $"{request.InstanceId}-g{request.Generation}.bluestone-exchange");
            exchange = WorkerBlockExchange.Create(path, options, request.Generation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            (plugin as IDisposable)?.Dispose();
            return new ErrorReply(ErrorCode.ExchangeFailed, $"Could not create the data-plane file: {ex.Message}");
        }

        var instance = new HostedInstance(request.InstanceId, plugin, exchange, request.SampleRate);
        _instances.Add(request.InstanceId, instance);
        instance.Start();
        if (plugin is NativePlugin native)
        {
            var id = request.InstanceId;
            mainThread.AddIdleHandler(() =>
            {
                if (!_instances.TryGetValue(id, out var live) || live.IsDisposed)
                {
                    return false;
                }

                native.Idle((kind, parameter, value) => _notifications.Writer.TryWrite(new PluginNotification(id, kind, parameter, value)));
                return true;
            });
        }

        return new InstanceCreated(request.InstanceId, plugin.LatencyFrames, plugin.Parameters, exchange.Path, request.Generation, stateError);
    }

    private ProtocolMessage Destroy(PluginInstanceId id)
    {
        if (!_instances.Remove(id, out var instance))
        {
            return new ErrorReply(ErrorCode.InstanceNotFound, $"Instance {id} does not exist.");
        }

        instance.Dispose();
        return new Ack();
    }

    private ProtocolMessage WithInstance(PluginInstanceId id, Func<HostedInstance, ProtocolMessage> action) =>
        _instances.TryGetValue(id, out var instance)
            ? action(instance)
            : new ErrorReply(ErrorCode.InstanceNotFound, $"Instance {id} does not exist.");
}
