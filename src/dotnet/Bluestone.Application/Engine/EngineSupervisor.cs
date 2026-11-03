using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Threading.Channels;
using Bluestone.Application.Status;
using Bluestone.Engine.Protocol;

namespace Bluestone.Application.Engine;

/// <summary>Where the engine process is in its life.</summary>
public enum EngineState
{
    /// <summary>Not started, or shut down.</summary>
    Stopped,

    /// <summary>Launched; waiting for its handshake and Ready.</summary>
    Starting,

    /// <summary>Running and answering; the transport can be used.</summary>
    Ready,

    /// <summary>It stopped unexpectedly and a new one is starting.</summary>
    Restarting,

    /// <summary>It stopped too often to restart by itself; <see cref="EngineSupervisor.RestartAsync"/> tries again.</summary>
    Failed,
}

/// <summary>Why an engine stopped (ADR 0044: what counts as a crash).</summary>
public enum EngineStopReason
{
    /// <summary>The process ended without being asked to.</summary>
    Exited,

    /// <summary>Nothing arrived for <see cref="EngineSupervisorOptions.HeartbeatTimeout"/>.</summary>
    MissedHeartbeats,

    /// <summary>The control channel closed or failed.</summary>
    ChannelBroken,

    /// <summary>The engine sent something the protocol does not allow.</summary>
    ProtocolError,

    /// <summary>The engine reported a fault on a real-time thread.</summary>
    Fault,

    /// <summary>It could not be launched, or did not finish its handshake in time.</summary>
    FailedToStart,
}

/// <summary>One unexpected stop of an engine process.</summary>
public sealed record EngineStop(EngineStopReason Reason, string Detail, int? ProcessId);

public sealed record EngineSupervisorOptions
{
    /// <summary>The engine executable or <c>.dll</c>; null finds it beside this assembly.</summary>
    public string? EnginePath { get; init; }

    /// <summary>How long a launch may take to connect, introduce itself, and report Ready.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>An engine that sends nothing for this long has stopped.</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = EngineLimits.HeartbeatTimeout;

    /// <summary>After this many unexpected stops within <see cref="RestartWindow"/>, the engine is not restarted by itself.</summary>
    public int RestartLimit { get; init; } = 3;

    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Starts engines that obey <see cref="TestCommand"/>s. Tests only.</summary>
    public bool AllowTestCommands { get; init; }

    /// <summary>
    /// With <see cref="AllowTestCommands"/>: the engine's audio device is a stand-in that renders only on a
    /// <see cref="TestRender"/>, so tests need no audio hardware. Tests only.
    /// </summary>
    public bool TestAudio { get; init; }
}

/// <summary>
/// Starts and watches the engine process (ADR 0044). The engine is started alongside the main window; editing works
/// while it starts, and <see cref="State"/> becomes <see cref="EngineState.Ready"/> when the engine has opened its
/// devices. An engine that stops unexpectedly (<see cref="EngineStopReason"/>) is killed if still running and replaced
/// by a new one, which sends All Notes Off before anything else; the transport stays stopped. After
/// <see cref="EngineSupervisorOptions.RestartLimit"/> stops within <see cref="EngineSupervisorOptions.RestartWindow"/>
/// it is left <see cref="EngineState.Failed"/> until <see cref="RestartAsync"/>. Every start and restart is an activity
/// on the status line.
/// </summary>
public sealed class EngineSupervisor : IAsyncDisposable
{
    private readonly EngineSupervisorOptions _options;
    private readonly ActivityLog _activities;
    private readonly Lock _gate = new();
    private readonly Queue<long> _recentStops = new();
    private readonly List<EngineStop> _stops = [];
    private readonly CancellationTokenSource _lifetime = new();
    private Session? _session;
    private EngineState _state;
    private Task _recovery = Task.CompletedTask;
    private long _nextPing;
    private ImmutableArray<IEngineClient> _clients = [];

    public EngineSupervisor(EngineSupervisorOptions options, ActivityLog activities)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _activities = activities ?? throw new ArgumentNullException(nameof(activities));
    }

    /// <summary>Raised on any thread when <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    public EngineState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>The running engine's process id, if one is running.</summary>
    public int? ProcessId => Volatile.Read(ref _session)?.ProcessId;

    /// <summary>The figures in the running engine's last heartbeat.</summary>
    public EngineTelemetry? Telemetry => Volatile.Read(ref _session)?.Telemetry;

    /// <summary>Every unexpected stop so far, oldest first.</summary>
    public ImmutableArray<EngineStop> Stops
    {
        get
        {
            lock (_gate)
            {
                return [.. _stops];
            }
        }
    }

    /// <summary>
    /// Connects a client that uses the engine (MIDI playback, audio). Call before <see cref="StartAsync"/>: each client is
    /// told about every engine that starts, in the order they were attached, before that engine is reported Ready, so it
    /// can send what the engine needs. Every client receives every message from the engine and keeps those it handles.
    /// </summary>
    public void Attach(IEngineClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            if (_clients.Contains(client))
            {
                throw new InvalidOperationException("The client is attached already.");
            }

            _clients = _clients.Add(client);
        }
    }

    private ImmutableArray<IEngineClient> Clients
    {
        get
        {
            lock (_gate)
            {
                return _clients;
            }
        }
    }

    /// <summary>Starts the engine. Completes when it is Ready, or when starting has failed (see <see cref="State"/>).</summary>
    public Task StartAsync() => LaunchAsync(restarted: false, "Starting the engine…");

    /// <summary>Starts the engine again after it was left <see cref="EngineState.Failed"/>, forgetting earlier stops.</summary>
    public Task RestartAsync()
    {
        lock (_gate)
        {
            _recentStops.Clear();
        }

        return LaunchAsync(restarted: true, "Restarting the engine…");
    }

    /// <summary>The time a Ping takes to come back from the running engine.</summary>
    /// <exception cref="InvalidOperationException">No engine is ready.</exception>
    /// <exception cref="TimeoutException">It did not answer within <paramref name="timeout"/>.</exception>
    public async Task<TimeSpan> PingAsync(TimeSpan timeout)
    {
        var session = Volatile.Read(ref _session) is { } s && State == EngineState.Ready ? s : throw new InvalidOperationException("The engine is not ready.");
        var id = Interlocked.Increment(ref _nextPing);
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Pongs[id] = answered;
        var started = Stopwatch.GetTimestamp();
        try
        {
            await session.Channel.WriteAsync(new Ping(id), _lifetime.Token).ConfigureAwait(false);
            await answered.Task.WaitAsync(timeout, _lifetime.Token).ConfigureAwait(false);
            return Stopwatch.GetElapsedTime(started);
        }
        finally
        {
            session.Pongs.TryRemove(id, out _);
        }
    }

    /// <summary>Tests only: asks the running engine to <paramref name="action"/>.</summary>
    internal Task SendTestAsync(TestAction action) =>
        Volatile.Read(ref _session) is { } session
            ? session.Channel.WriteAsync(new TestCommand(action), _lifetime.Token)
            : throw new InvalidOperationException("No engine is running.");

    /// <summary>Asks the engine to stop and waits briefly for it; one that does not stop is killed. Not a crash.</summary>
    public async ValueTask DisposeAsync()
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
            _state = EngineState.Stopped;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _recovery.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (session is not null)
        {
            foreach (var client in Clients)
            {
                client.Disconnected(session);
            }

            await session.ShutdownAsync().ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }

    // The engine's audio device problems go to the status line and Messages, as the device problems of Bluestone's own
    // audio do (MIDI problems reach Messages with the playback controller's next refresh).
    private void ReportAudioProblem(string problem)
    {
        using var activity = _activities.Begin("Audio", "The engine's audio device");
        activity.CompleteWithWarning(problem);
    }

    private async Task LaunchAsync(bool restarted, string what)
    {
        using var activity = _activities.Begin("Engine", what);
        SetState(restarted ? EngineState.Restarting : EngineState.Starting);
        try
        {
            var session = await Session.StartAsync(this, restarted).ConfigureAwait(false);
            if (!_lifetime.IsCancellationRequested)
            {
                foreach (var client in Clients)
                {
                    client.Connected(session, restarted);
                }
            }

            lock (_gate)
            {
                if (_lifetime.IsCancellationRequested)
                {
                    _ = session.ShutdownAsync();
                    return;
                }

                _session = session;
                _state = EngineState.Ready;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            session.Watch();
            if (restarted)
            {
                var stop = Stops is [.., var last] ? last : null;
                activity.CompleteWithWarning(stop is null
                    ? "The engine was restarted; playback is stopped."
                    : $"The engine stopped ({Describe(stop)}) and was restarted; playback is stopped.");
            }
        }
        catch (EngineStartException ex) when (!_lifetime.IsCancellationRequested)
        {
            activity.Fail(ex.Message);
            Stopped(null, EngineStopReason.FailedToStart, ex.Message, ex.ProcessId);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            activity.Fail("Bluestone is closing.");
        }
    }

    // A session ended unexpectedly: record it, then restart, or give up after too many stops in a short time.
    private void Stopped(Session? session, EngineStopReason reason, string detail, int? processId)
    {
        bool restart;
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested || (session is not null && !ReferenceEquals(session, _session)))
            {
                return;
            }

            _session = null;
            _stops.Add(new EngineStop(reason, detail, processId));
            if (session is not null)
            {
                foreach (var client in _clients)
                {
                    client.Disconnected(session);
                }
            }

            var now = Stopwatch.GetTimestamp();
            _recentStops.Enqueue(now);
            while (_recentStops.Count > 0 && Stopwatch.GetElapsedTime(_recentStops.Peek(), now) > _options.RestartWindow)
            {
                _recentStops.Dequeue();
            }

            restart = _recentStops.Count < _options.RestartLimit;
            _state = restart ? EngineState.Restarting : EngineState.Failed;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        if (restart)
        {
            _recovery = Task.Run(() => LaunchAsync(restarted: true, "Restarting the engine…"));
        }
        else
        {
            using var activity = _activities.Begin("Engine", "Restarting the engine…");
            activity.Fail($"the engine stopped {_options.RestartLimit} times within {_options.RestartWindow.TotalSeconds:0} seconds ({Describe(Stops[^1])}), so it was not restarted. Restart it to try again; editing still works.");
        }
    }

    private void SetState(EngineState state)
    {
        lock (_gate)
        {
            _state = state;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Describe(EngineStop stop) => stop.Reason switch
    {
        EngineStopReason.Exited => $"its process ended: {stop.Detail}",
        EngineStopReason.MissedHeartbeats => "it stopped answering",
        EngineStopReason.ChannelBroken => "its connection broke",
        EngineStopReason.ProtocolError => $"it sent something invalid: {stop.Detail}",
        EngineStopReason.Fault => $"it reported a fault: {stop.Detail}",
        _ => stop.Detail,
    };

    /// <summary>A launch could not finish.</summary>
    private sealed class EngineStartException(string message, int? processId, Exception? inner = null) : Exception(message, inner)
    {
        public int? ProcessId { get; } = processId;
    }

    /// <summary>One engine process, its control channel, and the loops that watch it.</summary>
#pragma warning disable CA1001 // _stop is cancelled, never disposed: the watch loops may still read its token, and it holds no timer or handle.
    private sealed class Session : IEngineLink
#pragma warning restore CA1001
    {
        private readonly EngineSupervisor _owner;
        private readonly Process _process;
        private readonly NamedPipeServerStream _pipe;
        private readonly CancellationTokenSource _stop = new();
        private readonly Channel<EngineMessage> _outbox = System.Threading.Channels.Channel.CreateUnbounded<EngineMessage>(new UnboundedChannelOptions { SingleReader = true });
        private long _lastHeard = Stopwatch.GetTimestamp();
        private int _ended;
        private EngineTelemetry? _telemetry;

        private Session(EngineSupervisor owner, Process process, NamedPipeServerStream pipe)
        {
            _owner = owner;
            _process = process;
            _pipe = pipe;
            Channel = new EngineChannel(pipe);
            ProcessId = process.Id;
        }

        public EngineChannel Channel { get; }

        public int ProcessId { get; }

        public ConcurrentDictionary<long, TaskCompletionSource> Pongs { get; } = new();

        public EngineTelemetry? Telemetry
        {
            get
            {
                lock (Pongs)
                {
                    return _telemetry;
                }
            }
        }

        public static async Task<Session> StartAsync(EngineSupervisor owner, bool restarted)
        {
            var options = owner._options;
            string path;
            try
            {
                path = options.EnginePath ?? EngineLauncher.ResolveDefault();
            }
            catch (FileNotFoundException ex)
            {
                throw new EngineStartException(ex.Message, null, ex);
            }

            var pipeName = "bluestone-engine-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            var token = EngineLimits.CreateToken();
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var process = new Process { StartInfo = EngineLauncher.CreateStartInfo(path, pipeName, token, restarted, options.AllowTestCommands, options.TestAudio), EnableRaisingEvents = true };
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
                throw new EngineStartException($"Could not start the engine '{path}': {ex.Message}", null, ex);
            }

            // The engine's output is read and dropped so a chatty engine can never block on a full pipe.
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { } line)
                {
                    Trace.TraceWarning($"Engine {process.Id}: {line}");
                }
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var session = new Session(owner, process, pipe);
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(owner._lifetime.Token);
                limit.CancelAfter(options.StartTimeout);
                await session.HandshakeAsync(token, limit.Token).ConfigureAwait(false);
                return session;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or EngineProtocolException or ObjectDisposedException)
            {
                var processId = session.ProcessId;
                await session.KillAsync().ConfigureAwait(false);
                if (owner._lifetime.IsCancellationRequested)
                {
                    throw new OperationCanceledException(owner._lifetime.Token);
                }

                throw new EngineStartException(ex switch
                {
                    OperationCanceledException => $"The engine did not start within {options.StartTimeout.TotalSeconds:0.#} seconds.",
                    EngineProtocolException => $"The engine did not introduce itself correctly: {ex.Message}",
                    _ => $"The engine's connection failed while it started: {ex.Message}",
                }, processId, ex);
            }
        }

        public bool Send(EngineMessage message) => Volatile.Read(ref _ended) == 0 && _outbox.Writer.TryWrite(message);

        /// <summary>Starts watching for the ways a running engine can stop, and sending what clients queue.</summary>
        public void Watch()
        {
            _process.Exited += (_, _) => End(EngineStopReason.Exited, ExitDescription());
            if (_process.HasExited)
            {
                End(EngineStopReason.Exited, ExitDescription());
                return;
            }

            _ = Task.Run(ReadLoopAsync);
            _ = Task.Run(WatchdogAsync);
            _ = Task.Run(WriteLoopAsync);
        }

        public async Task ShutdownAsync()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0)
            {
                return;
            }

            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await Channel.WriteAsync(new Shutdown(), limit.Token).ConfigureAwait(false);
                await _process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                // It did not stop by itself; it is killed below.
            }

            await KillAsync().ConfigureAwait(false);
        }

        private async Task HandshakeAsync(string token, CancellationToken cancellationToken)
        {
            var exited = _process.WaitForExitAsync(cancellationToken);
            var connected = _pipe.WaitForConnectionAsync(cancellationToken);
            if (await Task.WhenAny(connected, exited).ConfigureAwait(false) != connected)
            {
                throw new IOException($"The engine ended before it connected ({ExitDescription()}).");
            }

            await connected.ConfigureAwait(false);
            if (await Channel.ReadAsync(cancellationToken).ConfigureAwait(false) is not Hello hello)
            {
                throw new EngineProtocolException("The engine did not begin with Hello.");
            }

            if (hello.Version != EngineLimits.Version)
            {
                throw new EngineProtocolException($"The engine speaks protocol version {hello.Version}; Bluestone speaks {EngineLimits.Version}.");
            }

            if (!EngineLimits.TokensMatch(hello.Token, token) || hello.ProcessId != ProcessId)
            {
                throw new EngineProtocolException("The engine presented the wrong launch token.");
            }

            await Channel.WriteAsync(new Welcome(EngineLimits.Version), cancellationToken).ConfigureAwait(false);
            while (await Channel.ReadAsync(cancellationToken).ConfigureAwait(false) is { } message)
            {
                switch (message)
                {
                    case Ready:
                        Heard();
                        return;
                    case Heartbeat heartbeat:
                        Record(heartbeat);
                        break;
                    case Fault fault:
                        throw new EngineProtocolException($"The engine reported a fault while starting: {fault.Message}");
                    default:
                        throw new EngineProtocolException($"The engine sent {message.Type} before Ready.");
                }
            }

            throw new IOException("The engine closed its connection before it was ready.");
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (await Channel.ReadAsync(_stop.Token).ConfigureAwait(false) is { } message)
                {
                    Heard();
                    switch (message)
                    {
                        case Heartbeat heartbeat:
                            Record(heartbeat);
                            break;
                        case Pong pong when Pongs.TryGetValue(pong.Id, out var waiting):
                            waiting.TrySetResult();
                            break;
                        case Pong:
                            break;
                        case Fault fault:
                            End(EngineStopReason.Fault, fault.Message);
                            return;
                        case EngineProblem { Area: EngineProblemArea.Audio } problem:
                            _owner.ReportAudioProblem(problem.Message);
                            Deliver(message);
                            break;
                        case TransportStatus or TimingStatus or MidiBatch or PlanLoaded or EngineProblem or AudioDeviceStatus or AudioGraphLoaded or AudioStatus or TestRendered:
                            Deliver(message);
                            break;
                        default:
                            End(EngineStopReason.ProtocolError, $"{message.Type} is not expected from a running engine.");
                            return;
                    }
                }

                End(EngineStopReason.ChannelBroken, "the engine closed its connection.");
            }
            catch (EngineProtocolException ex)
            {
                End(EngineStopReason.ProtocolError, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                End(EngineStopReason.ChannelBroken, ex.Message);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void Deliver(EngineMessage message)
        {
            foreach (var client in _owner.Clients)
            {
                try
                {
                    client.Received(this, message);
                }
#pragma warning disable CA1031 // A client's failure must not look like the engine's; it is reported and the channel goes on.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    Trace.TraceError($"The engine client failed on {message.Type}: {ex}");
                }
            }
        }

        // Sends what clients queue, in order. A failed write is a broken channel.
        private async Task WriteLoopAsync()
        {
            try
            {
                await foreach (var message in _outbox.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                {
                    await Channel.WriteAsync(message, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                End(EngineStopReason.ChannelBroken, ex.Message);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task WatchdogAsync()
        {
            var timeout = _owner._options.HeartbeatTimeout;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(10, timeout.TotalMilliseconds / 10)));
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                {
                    if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastHeard)) > timeout)
                    {
                        End(EngineStopReason.MissedHeartbeats, $"nothing arrived for {timeout.TotalSeconds:0.##} s.");
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void Heard() => Volatile.Write(ref _lastHeard, Stopwatch.GetTimestamp());

        private void Record(Heartbeat heartbeat)
        {
            lock (Pongs)
            {
                _telemetry = heartbeat.Telemetry;
            }
        }

        // Called once, by whichever watcher notices first; the engine is killed if still running, then replaced.
        private void End(EngineStopReason reason, string detail)
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0)
            {
                return;
            }

            _ = KillAsync();
            _owner.Stopped(this, reason, detail, ProcessId);
        }

        private async Task KillAsync()
        {
            _outbox.Writer.TryComplete();
            await _stop.CancelAsync().ConfigureAwait(false);
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

            Channel.Dispose();
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _process.Dispose();
        }

        private string ExitDescription()
        {
            try
            {
                return _process.HasExited ? $"exit code {_process.ExitCode}" : "still running";
            }
            catch (InvalidOperationException)
            {
                return "unknown";
            }
        }
    }
}
