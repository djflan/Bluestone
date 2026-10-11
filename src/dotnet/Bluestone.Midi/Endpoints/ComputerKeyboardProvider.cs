using Bluestone.Midi.Timing;

namespace Bluestone.Midi.Endpoints;

/// <summary>
/// A single software input endpoint fed by the computer keyboard. The UI turns key presses into MIDI
/// messages and calls <see cref="Send"/>; every open input receives them, stamped with the clock, so
/// recording, thru, and the input monitor treat the keyboard like any other controller.
/// </summary>
public sealed class ComputerKeyboardProvider : IMidiEndpointProvider
{
    public const string ProviderId = "keyboard";

    public static readonly EndpointId InputId = new(ProviderId, "computer-keyboard");

    private readonly IMonotonicClock _clock;
    private readonly Lock _gate = new();
    private readonly List<InputHandle> _inputs = [];
    private bool _disposed;

    public ComputerKeyboardProvider(IMonotonicClock clock) =>
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public string Id => ProviderId;

    public string DisplayName => "Computer Keyboard";

    public string TimingDescription => "Computer keyboard: notes are stamped when the key event reaches Bluestone.";

    public EndpointDescriptor Descriptor { get; } =
        new(InputId, "Computer Keyboard", EndpointDirection.Input, EndpointTransport.Software, EndpointCapabilities.None);

    /// <summary>The endpoint never changes, so this event is never raised.</summary>
    public event EventHandler? EndpointsChanged
    {
        add { }
        remove { }
    }

    public IReadOnlyList<EndpointDescriptor> GetEndpoints() => [Descriptor];

    public ValueTask<IMidiOutput> OpenOutputAsync(EndpointId id, CancellationToken cancellationToken = default) =>
        throw new EndpointUnavailableException(id, "the computer keyboard is an input only");

    public ValueTask<IMidiInput> OpenInputAsync(EndpointId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id != InputId)
        {
            throw new EndpointUnavailableException(id, "no such input endpoint");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var handle = new InputHandle(this, Descriptor);
            _inputs.Add(handle);
            return ValueTask.FromResult<IMidiInput>(handle);
        }
    }

    /// <summary>Delivers one complete MIDI message to every open input.</summary>
    public void Send(ReadOnlySpan<byte> message)
    {
        InputHandle[] inputs;
        lock (_gate)
        {
            if (_disposed || _inputs.Count == 0)
            {
                return;
            }

            inputs = [.. _inputs];
        }

        var now = _clock.Now;
        foreach (var input in inputs)
        {
            input.Receive(message, now);
        }
    }

    public void Dispose()
    {
        InputHandle[] inputs;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            inputs = [.. _inputs];
            _inputs.Clear();
        }

        foreach (var input in inputs)
        {
            input.Transition(EndpointState.Disconnected, "The computer keyboard provider was closed.");
        }
    }

    private void Unregister(InputHandle handle)
    {
        lock (_gate)
        {
            _inputs.Remove(handle);
        }
    }

    private sealed class InputHandle(ComputerKeyboardProvider provider, EndpointDescriptor endpoint) : IMidiInput
    {
        private int _state = (int)EndpointState.Open;
        private MidiReceiveHandler? _receiver;

        public EndpointDescriptor Endpoint { get; } = endpoint;

        public EndpointState State => (EndpointState)Volatile.Read(ref _state);

        public event EventHandler<EndpointStateChangedEventArgs>? StateChanged;

        public void SetReceiver(MidiReceiveHandler? handler) => Volatile.Write(ref _receiver, handler);

        public void Receive(ReadOnlySpan<byte> message, TimeSpan timestamp)
        {
            if (State == EndpointState.Open)
            {
                Volatile.Read(ref _receiver)?.Invoke(message, timestamp);
            }
        }

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
                provider.Unregister(this);
            }
        }
    }
}
