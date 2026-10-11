using Bluestone.Midi.Timing;
using Bluestone.Midi.Wire;

namespace Bluestone.Midi.Endpoints;

/// <summary>A message captured by a <see cref="LoopbackPort"/>.</summary>
public sealed record RecordedMessage(byte[] Bytes, MidiTimestamp Requested, TimeSpan SentAt);

/// <summary>
/// A deterministic, in-memory provider. Each port has an output and an input endpoint; anything sent
/// to the output is recorded and delivered to the input, like a virtual MIDI bus. Ports can be removed
/// and renamed to simulate hot-plugging, and sends can be made to fail to simulate backpressure.
/// </summary>
/// <remarks>This is a test double: it records every message and therefore allocates on send.</remarks>
public sealed class LoopbackMidiProvider : IMidiEndpointProvider
{
    public const string ProviderId = "loopback";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, LoopbackPort> _ports = new(StringComparer.Ordinal);
    private bool _disposed;

    public LoopbackMidiProvider(IMonotonicClock clock, EndpointCapabilities capabilities = EndpointCapabilities.ScheduledDelivery | EndpointCapabilities.SystemExclusive)
    {
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Capabilities = capabilities;
    }

    public string Id => ProviderId;

    public string DisplayName => "Loopback";

    public string TimingDescription => "In-memory loopback; timestamps are recorded, not enforced.";

    public IMonotonicClock Clock { get; }

    public EndpointCapabilities Capabilities { get; }

    public event EventHandler? EndpointsChanged;

    /// <summary>Adds a port. <paramref name="key"/> is its stable identity; reusing a removed key simulates replugging.</summary>
    public LoopbackPort CreatePort(string name, string? key = null)
    {
        LoopbackPort port;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            key ??= Guid.NewGuid().ToString("N");
            if (_ports.ContainsKey(key))
            {
                throw new ArgumentException($"A port with key '{key}' already exists.", nameof(key));
            }

            port = new LoopbackPort(this, key, name);
            _ports.Add(key, port);
        }

        EndpointsChanged?.Invoke(this, EventArgs.Empty);
        return port;
    }

    /// <summary>Removes a port, disconnecting every handle opened on it.</summary>
    public void RemovePort(string key)
    {
        LoopbackPort? port;
        lock (_gate)
        {
            if (!_ports.Remove(key, out port))
            {
                return;
            }
        }

        port.Disconnect();
        EndpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RenamePort(string key, string name)
    {
        lock (_gate)
        {
            if (!_ports.TryGetValue(key, out var port))
            {
                throw new KeyNotFoundException($"No port with key '{key}'.");
            }

            port.Name = name;
        }

        EndpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<EndpointDescriptor> GetEndpoints()
    {
        lock (_gate)
        {
            return [.. _ports.Values.OrderBy(p => p.Key, StringComparer.Ordinal).SelectMany(p => new[] { p.OutputDescriptor, p.InputDescriptor })];
        }
    }

    public ValueTask<IMidiOutput> OpenOutputAsync(EndpointId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var port = Find(id, EndpointDirection.Output);
        return ValueTask.FromResult<IMidiOutput>(port.OpenOutput());
    }

    public ValueTask<IMidiInput> OpenInputAsync(EndpointId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var port = Find(id, EndpointDirection.Input);
        return ValueTask.FromResult<IMidiInput>(port.OpenInput());
    }

    public void Dispose()
    {
        List<LoopbackPort> ports;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ports = [.. _ports.Values];
            _ports.Clear();
        }

        foreach (var port in ports)
        {
            port.Disconnect();
        }
    }

    private LoopbackPort Find(EndpointId id, EndpointDirection direction)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (id.Provider == ProviderId)
            {
                foreach (var port in _ports.Values)
                {
                    var descriptor = direction == EndpointDirection.Output ? port.OutputDescriptor : port.InputDescriptor;
                    if (descriptor.Id == id)
                    {
                        return port;
                    }
                }
            }
        }

        throw new EndpointUnavailableException(id, direction == EndpointDirection.Output ? "no such output endpoint" : "no such input endpoint");
    }
}

/// <summary>One loopback bus: an output endpoint whose messages arrive at the paired input endpoint.</summary>
public sealed class LoopbackPort
{
    private readonly LoopbackMidiProvider _provider;
    private readonly Lock _gate = new();
    private readonly List<RecordedMessage> _sent = [];
    private readonly List<Handle> _handles = [];
    private string _name;
    private int _failuresRemaining;
    private SendResult _failureResult;
    private bool _disconnected;

    internal LoopbackPort(LoopbackMidiProvider provider, string key, string name)
    {
        _provider = provider;
        Key = key;
        _name = name;
        OutputId = new EndpointId(LoopbackMidiProvider.ProviderId, key + "/out");
        InputId = new EndpointId(LoopbackMidiProvider.ProviderId, key + "/in");
    }

    public string Key { get; }

    public string Name
    {
        get
        {
            lock (_gate)
            {
                return _name;
            }
        }

        internal set
        {
            lock (_gate)
            {
                _name = value;
            }
        }
    }

    public EndpointId OutputId { get; }

    public EndpointId InputId { get; }

    internal EndpointDescriptor OutputDescriptor => new(OutputId, Name, EndpointDirection.Output, EndpointTransport.Test, _provider.Capabilities);

    internal EndpointDescriptor InputDescriptor => new(InputId, Name, EndpointDirection.Input, EndpointTransport.Test, _provider.Capabilities);

    /// <summary>A snapshot of every message accepted by the port's output, in send order.</summary>
    public IReadOnlyList<RecordedMessage> Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    public void ClearSent()
    {
        lock (_gate)
        {
            _sent.Clear();
        }
    }

    /// <summary>Makes the next <paramref name="count"/> sends fail with <paramref name="result"/> without being recorded.</summary>
    public void FailNextSends(int count, SendResult result = SendResult.QueueFull)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (_gate)
        {
            _failuresRemaining = count;
            _failureResult = result;
        }
    }

    /// <summary>Simulates the device transmitting a message to Bluestone through the port's input.</summary>
    public void Inject(ReadOnlySpan<byte> message) => Deliver(message, _provider.Clock.Now);

    internal IMidiOutput OpenOutput() => Register(new OutputHandle(this, OutputDescriptor));

    internal IMidiInput OpenInput() => Register(new InputHandle(this, InputDescriptor));

    internal void Disconnect()
    {
        List<Handle> handles;
        lock (_gate)
        {
            _disconnected = true;
            handles = [.. _handles];
        }

        foreach (var handle in handles)
        {
            handle.Transition(EndpointState.Disconnected, "The loopback port was removed.");
        }
    }

    private T Register<T>(T handle)
        where T : Handle
    {
        lock (_gate)
        {
            if (_disconnected)
            {
                throw new EndpointUnavailableException(handle.Endpoint.Id, "the port was removed");
            }

            _handles.Add(handle);
        }

        return handle;
    }

    private void Unregister(Handle handle)
    {
        lock (_gate)
        {
            _handles.Remove(handle);
        }
    }

    private SendResult Send(ReadOnlySpan<byte> message, MidiTimestamp timestamp)
    {
        var kind = MidiWire.Classify(message);
        if (kind == MidiMessageClass.Invalid
            || (kind == MidiMessageClass.SystemExclusive && !_provider.Capabilities.HasFlag(EndpointCapabilities.SystemExclusive)))
        {
            return SendResult.Rejected;
        }

        var now = _provider.Clock.Now;
        lock (_gate)
        {
            if (_disconnected)
            {
                return SendResult.Disconnected;
            }

            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                return _failureResult;
            }

            _sent.Add(new RecordedMessage(message.ToArray(), timestamp, now));
        }

        var deliverAt = timestamp.IsImmediate || timestamp.Time < now ? now : timestamp.Time;
        Deliver(message, deliverAt);
        return SendResult.Sent;
    }

    private void Deliver(ReadOnlySpan<byte> message, TimeSpan timestamp)
    {
        InputHandle[] inputs;
        lock (_gate)
        {
            inputs = [.. _handles.OfType<InputHandle>()];
        }

        foreach (var input in inputs)
        {
            input.Receive(message, timestamp);
        }
    }

    private abstract class Handle(LoopbackPort port, EndpointDescriptor endpoint)
    {
        private int _state = (int)EndpointState.Open;

        protected LoopbackPort Port { get; } = port;

        public EndpointDescriptor Endpoint { get; } = endpoint;

        public EndpointState State => (EndpointState)Volatile.Read(ref _state);

        public event EventHandler<EndpointStateChangedEventArgs>? StateChanged;

        /// <summary>Moves to <paramref name="state"/> unless already there or closed; returns whether it moved.</summary>
        public bool Transition(EndpointState state, string? reason)
        {
            while (true)
            {
                var current = Volatile.Read(ref _state);
                if (current == (int)EndpointState.Closed || current == (int)state)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _state, (int)state, current) == current)
                {
                    break;
                }
            }

            StateChanged?.Invoke(this, new EndpointStateChangedEventArgs(state, reason));
            return true;
        }

        public void Dispose()
        {
            if (Transition(EndpointState.Closed, null))
            {
                Port.Unregister(this);
            }
        }
    }

    private sealed class OutputHandle(LoopbackPort port, EndpointDescriptor endpoint) : Handle(port, endpoint), IMidiOutput
    {
        public SendResult Send(ReadOnlySpan<byte> message, MidiTimestamp timestamp) => State switch
        {
            EndpointState.Open => Port.Send(message, timestamp),
            EndpointState.Disconnected => SendResult.Disconnected,
            _ => SendResult.Closed,
        };
    }

    private sealed class InputHandle(LoopbackPort port, EndpointDescriptor endpoint) : Handle(port, endpoint), IMidiInput
    {
        private MidiReceiveHandler? _receiver;

        public void SetReceiver(MidiReceiveHandler? handler) => Volatile.Write(ref _receiver, handler);

        public void Receive(ReadOnlySpan<byte> message, TimeSpan timestamp)
        {
            if (State == EndpointState.Open)
            {
                Volatile.Read(ref _receiver)?.Invoke(message, timestamp);
            }
        }
    }
}
