using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using Bluestone.Plugins.Protocol;

namespace Bluestone.Plugins.Workers;

/// <summary>Why a worker process ended.</summary>
internal enum WorkerExitReason
{
    /// <summary>The host asked it to shut down.</summary>
    ShutDown,

    /// <summary>It died or its pipe broke without being asked to.</summary>
    Crashed,

    /// <summary>It stopped answering heartbeats or a request timed out, so the host killed it.</summary>
    Hung,

    /// <summary>It sent something that is not a valid frame, so the host killed it.</summary>
    ProtocolError,
}

internal sealed record WorkerProcessOptions
{
    public string? WorkerPath { get; init; }

    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Ping period; null disables the heartbeat (scanner processes rely on a hard timeout instead).</summary>
    public TimeSpan? HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Silence after which the worker is considered hung and killed.</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Called on the read loop for each <see cref="PluginNotification"/> the worker sends; must return quickly.</summary>
    public Action<WorkerProcess, PluginNotification>? OnNotification { get; init; }
}

/// <summary>A worker could not be brought up. <see cref="Reason"/> is the status the affected instances get.</summary>
internal sealed class WorkerStartException : Exception
{
    public WorkerStartException(UnavailableReason reason, string message, Exception? innerException = null)
        : base(message, innerException) => Reason = reason;

    public UnavailableReason Reason { get; }
}

/// <summary>
/// One worker process and its control pipe. The host creates the pipe server, launches the worker (never through a
/// shell), hands it a random token on standard input, and accepts the connection only after a valid Hello. Requests
/// are correlated by id and each has its own timeout. A heartbeat kills a worker that stops answering. The end of
/// the process is detected both from the process and from the pipe; whichever comes first reports it exactly once,
/// on a thread-pool thread, never on a real-time path.
/// </summary>
internal sealed class WorkerProcess : IAsyncDisposable
{
    private const int MaxOutputLines = 64;

    private readonly Process _process;
    private readonly NamedPipeServerStream _pipe;
    private readonly WorkerProcessOptions _options;
    private readonly Action<WorkerProcess, WorkerExitReason>? _onTerminated;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<ProtocolMessage>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentQueue<string> _output = new();
    private readonly TaskCompletionSource<WorkerExitReason> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _readLoop = Task.CompletedTask;
    private Task _heartbeatLoop = Task.CompletedTask;
    private int _nextRequestId;
    private int _terminated;
    private int _killReason;
    private int _disposed;
    private long _lastPong;
    private volatile bool _running;
    private volatile bool _shutdownRequested;

    private WorkerProcess(Process process, NamedPipeServerStream pipe, WorkerMode mode, WorkerProcessOptions options, Action<WorkerProcess, WorkerExitReason>? onTerminated)
    {
        _process = process;
        _pipe = pipe;
        Mode = mode;
        _options = options;
        _onTerminated = onTerminated;
        ProcessId = process.Id;
        _process.OutputDataReceived += (_, e) => Note(e.Data);
        _process.ErrorDataReceived += (_, e) => Note(e.Data);
        _process.Exited += (_, _) => OnProcessExited();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public int ProcessId { get; }

    public WorkerMode Mode { get; }

    /// <summary>Completes once the worker has ended, with the reason.</summary>
    public Task<WorkerExitReason> Exited => _exit.Task;

    public bool IsAlive => Volatile.Read(ref _terminated) == 0;

    /// <summary>The last lines the worker wrote to standard output or error, for diagnostics.</summary>
    public IReadOnlyList<string> RecentOutput => [.. _output];

    /// <summary>Launches a worker and completes the handshake, or throws <see cref="WorkerStartException"/>. Nothing is left running on failure.</summary>
    public static async Task<WorkerProcess> StartAsync(
        WorkerMode mode,
        WorkerProcessOptions options,
        Action<WorkerProcess, WorkerExitReason>? onTerminated,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var pipeName = "bluestone-plugin-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var token = Handshake.CreateToken();
        ProcessStartInfo info;
        try
        {
            info = WorkerLauncher.CreateStartInfo(options.WorkerPath, mode, pipeName, options.Environment);
        }
        catch (FileNotFoundException ex)
        {
            throw new WorkerStartException(UnavailableReason.FailedToStart, ex.Message, ex);
        }

        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Process.Start returned false.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException or IOException)
        {
            process.Dispose();
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new WorkerStartException(UnavailableReason.FailedToStart, $"Could not start the plugin worker '{info.FileName}': {ex.Message}", ex);
        }

        var worker = new WorkerProcess(process, pipe, mode, options, onTerminated);
        try
        {
            await worker.HandshakeAsync(token, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await worker.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        worker.StartLoops();
        return worker;
    }

    /// <summary>
    /// Sends a request and waits for its reply (which may be an <see cref="ErrorReply"/>). Throws
    /// <see cref="TimeoutException"/> after <paramref name="timeout"/> and <see cref="PluginUnavailableException"/> if
    /// the worker is gone or goes away meanwhile.
    /// </summary>
    public async Task<ProtocolMessage> RequestAsync(ProtocolMessage request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ThrowIfGone();
        var id = NextRequestId();
        var reply = new TaskCompletionSource<ProtocolMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            ThrowIfGone();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            limit.CancelAfter(timeout);
            try
            {
                await WriteAsync(id, request, limit.Token).ConfigureAwait(false);
                return await reply.Task.WaitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                ThrowIfGone();
                throw new TimeoutException($"The plugin worker did not answer {request.Type} within {timeout.TotalSeconds:0.###} s.");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                throw new PluginUnavailableException("The plugin worker's pipe is closed.", ex);
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a message that has no reply (test faults). Failures are reported, not thrown, as the worker may already be gone.</summary>
    public async Task<bool> TrySendAsync(ProtocolMessage message, TimeSpan timeout)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            limit.CancelAfter(timeout);
            await WriteAsync(0, message, limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException)
        {
            return false;
        }
    }

    /// <summary>The exit code once the process has exited.</summary>
    internal int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    /// <summary>Test hook: drops the pipe as if the host had vanished, without asking the worker to stop or killing it.</summary>
    internal void ClosePipeWithoutShutdown()
    {
        _shutdownRequested = true;
        _pipe.Dispose();
    }

    /// <summary>Kills the process tree. The first recorded reason wins; the end is then reported as usual.</summary>
    public void Kill(WorkerExitReason reason)
    {
        Interlocked.CompareExchange(ref _killReason, (int)reason + 1, 0);
        KillProcess();
    }

    /// <summary>Asks the worker to exit, waits a bounded time, then kills it. Idempotent; leaves no process behind.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdownRequested = true;
        if (_running && IsAlive)
        {
            await TrySendAsync(new Shutdown(), TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        await WaitForExitAsync(_options.ShutdownTimeout).ConfigureAwait(false);
        KillProcess();
        await WaitForExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAny(Task.WhenAll(_readLoop, _heartbeatLoop), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        OnTerminated();
        _process.Dispose();
    }

    private async Task HandshakeAsync(string token, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_options.HandshakeTimeout);
        try
        {
            try
            {
                await _process.StandardInput.WriteLineAsync(token.AsMemory(), limit.Token).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(limit.Token).ConfigureAwait(false);
                _process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The worker is already gone; the exit is reported below.
            }

            var connect = _pipe.WaitForConnectionAsync(limit.Token);
            var exited = _process.WaitForExitAsync(limit.Token);
            if (await Task.WhenAny(connect, exited).ConfigureAwait(false) == exited && !connect.IsCompletedSuccessfully)
            {
                var code = _process.HasExited ? _process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
                throw new WorkerStartException(UnavailableReason.FailedToStart, $"The plugin worker exited (code {code}) before connecting.{Diagnostics()}");
            }

            await connect.ConfigureAwait(false);
            var hello = await FrameCodec.ReadAsync(_pipe, limit.Token).ConfigureAwait(false)
                ?? throw new WorkerStartException(UnavailableReason.FailedToStart, $"The plugin worker closed the pipe during the handshake.{Diagnostics()}");
            try
            {
                Handshake.ValidateHello(hello.Message, token, Mode);
                if (((Hello)hello.Message).ProcessId != _process.Id)
                {
                    throw new ProtocolException("The Hello came from a different process than the one launched.");
                }
            }
            catch (ProtocolException ex)
            {
                await FrameCodec.WriteAsync(_pipe, hello.RequestId, new ErrorReply(ErrorCode.InvalidRequest, ex.Message), limit.Token).ConfigureAwait(false);
                throw;
            }

            await FrameCodec.WriteAsync(_pipe, hello.RequestId, new HelloAck(ProtocolLimits.ProtocolVersion), limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WorkerStartException(UnavailableReason.FailedToStart, $"The plugin worker did not complete the handshake within {_options.HandshakeTimeout.TotalSeconds:0.###} s.{Diagnostics()}", ex);
        }
        catch (ProtocolException ex)
        {
            throw new WorkerStartException(UnavailableReason.ProtocolError, $"The plugin worker failed the handshake: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new WorkerStartException(UnavailableReason.FailedToStart, $"The plugin worker's pipe failed during the handshake: {ex.Message}{Diagnostics()}", ex);
        }
    }

    private void StartLoops()
    {
        Volatile.Write(ref _lastPong, Stopwatch.GetTimestamp());
        _running = true;
        _readLoop = Task.Run(ReadLoopAsync);
        if (_options.HeartbeatInterval is { } interval)
        {
            _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(interval));
        }

        if (_process.HasExited)
        {
            OnProcessExited();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await FrameCodec.ReadAsync(_pipe, _lifetime.Token).ConfigureAwait(false) is { } frame)
            {
                if (frame.Message is Pong)
                {
                    Volatile.Write(ref _lastPong, Stopwatch.GetTimestamp());
                }
                else if (frame.Message is PluginNotification notification && frame.RequestId == 0)
                {
                    _options.OnNotification?.Invoke(this, notification);
                }
                else if (_pending.TryRemove(frame.RequestId, out var reply))
                {
                    reply.TrySetResult(frame.Message);
                }
            }
        }
        catch (ProtocolException ex)
        {
            Note($"[host] protocol error: {ex.Message}");
            Kill(WorkerExitReason.ProtocolError);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The pipe broke or closed: the process is dead or going.
        }

        OnTerminated();
    }

    private async Task HeartbeatLoopAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        long sequence = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false) && IsAlive)
            {
                if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastPong)) > _options.HeartbeatTimeout)
                {
                    Note("[host] heartbeat missed; killing the worker as hung");
                    Kill(WorkerExitReason.Hung);
                    return;
                }

                _ = TrySendAsync(new Ping(++sequence), interval);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task WriteAsync(uint requestId, ProtocolMessage message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(_pipe, requestId, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void OnProcessExited()
    {
        if (_running)
        {
            // The pipe normally reports this first; closing it makes sure the read loop ends even if a descendant kept it open.
            OnTerminated();
        }
    }

    private void OnTerminated()
    {
        if (Interlocked.Exchange(ref _terminated, 1) != 0)
        {
            return;
        }

        var killReason = Volatile.Read(ref _killReason);
        var reason = killReason != 0 ? (WorkerExitReason)(killReason - 1) : _shutdownRequested ? WorkerExitReason.ShutDown : WorkerExitReason.Crashed;
        if (reason != WorkerExitReason.ShutDown)
        {
            // A worker whose pipe broke is not trusted to keep running.
            KillProcess();
        }

        // The reason is published before pending requests fail, so their callers can read it.
        _exit.TrySetResult(reason);
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new PluginUnavailableException($"The plugin worker ended ({reason})."));
        }

        if (_running && _onTerminated is { } callback)
        {
            _ = Task.Run(() => callback(this, reason));
        }
    }

    private void KillProcess()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    private async Task WaitForExitAsync(TimeSpan timeout)
    {
        try
        {
            using var limit = new CancellationTokenSource(timeout);
            await _process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
        }
    }

    private void ThrowIfGone()
    {
        if (!IsAlive || Volatile.Read(ref _disposed) != 0)
        {
            throw new PluginUnavailableException("The plugin worker is not running.");
        }
    }

    private uint NextRequestId()
    {
        while (true)
        {
            var id = (uint)Interlocked.Increment(ref _nextRequestId);
            if (id != 0)
            {
                return id;
            }
        }
    }

    private void Note(string? line)
    {
        if (line is null)
        {
            return;
        }

        _output.Enqueue(line);
        while (_output.Count > MaxOutputLines && _output.TryDequeue(out _))
        {
        }
    }

    private string Diagnostics()
    {
        var lines = RecentOutput;
        return lines.Count == 0 ? string.Empty : " Worker output: " + string.Join(" | ", lines.TakeLast(5));
    }
}
