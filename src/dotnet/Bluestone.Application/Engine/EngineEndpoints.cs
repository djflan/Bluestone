using Bluestone.Midi.Endpoints;

namespace Bluestone.Application.Engine;

/// <summary>An output or input known to the engine by a handle.</summary>
internal interface IEngineEndpoint
{
    int Handle { get; }
}

/// <summary>
/// An output the engine opens itself (a platform endpoint). Sending here from Bluestone (initialization SysEx, thru
/// from routes Bluestone runs) goes to the engine, which sends it at once.
/// </summary>
internal sealed class EngineOutput(EnginePlayback owner, int handle, EndpointDescriptor endpoint) : IMidiOutput, IEngineEndpoint
{
    private int _state = (int)EndpointState.Open;

    public int Handle { get; } = handle;

    public EndpointDescriptor Endpoint { get; } = endpoint;

    public EndpointState State => (EndpointState)Volatile.Read(ref _state);

    public event EventHandler<EndpointStateChangedEventArgs>? StateChanged;

    public SendResult Send(ReadOnlySpan<byte> message, MidiTimestamp timestamp) =>
        State != EndpointState.Open ? SendResult.Closed : owner.SendToOutput(Handle, message);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _state, (int)EndpointState.Closed) != (int)EndpointState.Closed)
        {
            owner.Close(this);
        }
    }

    /// <summary>The engine could not open it: it is faulted, so the next refresh opens it again.</summary>
    internal void Fail(string reason)
    {
        if (Interlocked.CompareExchange(ref _state, (int)EndpointState.Faulted, (int)EndpointState.Open) == (int)EndpointState.Open)
        {
            StateChanged?.Invoke(this, new EndpointStateChangedEventArgs(EndpointState.Faulted, reason));
        }
    }
}

/// <summary>
/// An output only Bluestone can reach (its monitor and test buses, its own virtual ports), opened here: the engine
/// plays to it through <see cref="EnginePlayback"/>, which sends what arrives on to <see cref="Local"/>.
/// </summary>
internal sealed class ForwardedOutput : IMidiOutput, IEngineEndpoint
{
    private readonly EnginePlayback _owner;
    private int _disposed;

    public ForwardedOutput(EnginePlayback owner, int handle, IMidiOutput local)
    {
        _owner = owner;
        Handle = handle;
        Local = local;
        local.StateChanged += OnLocalStateChanged;
    }

    public int Handle { get; }

    public IMidiOutput Local { get; }

    public EndpointDescriptor Endpoint => Local.Endpoint;

    public EndpointState State => Local.State;

    public event EventHandler<EndpointStateChangedEventArgs>? StateChanged;

    public SendResult Send(ReadOnlySpan<byte> message, MidiTimestamp timestamp) => Local.Send(message, timestamp);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Local.StateChanged -= OnLocalStateChanged;
            _owner.Close(this);
            Local.Dispose();
        }
    }

    private void OnLocalStateChanged(object? sender, EndpointStateChangedEventArgs e) => StateChanged?.Invoke(this, e);
}

/// <summary>An input the engine opens itself: what it receives arrives here through <see cref="EnginePlayback"/>, after the engine has played it on the thru route.</summary>
internal sealed class EngineInput(EnginePlayback owner, int handle, EndpointDescriptor endpoint) : IMidiInput, IEngineEndpoint
{
    private MidiReceiveHandler? _receiver;
    private int _state = (int)EndpointState.Open;

    public int Handle { get; } = handle;

    public EndpointDescriptor Endpoint { get; } = endpoint;

    public EndpointState State => (EndpointState)Volatile.Read(ref _state);

    public event EventHandler<EndpointStateChangedEventArgs>? StateChanged;

    public void SetReceiver(MidiReceiveHandler? handler) => Volatile.Write(ref _receiver, handler);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _state, (int)EndpointState.Closed) != (int)EndpointState.Closed)
        {
            Volatile.Write(ref _receiver, null);
            owner.Close(this);
        }
    }

    internal void Deliver(ReadOnlySpan<byte> message, TimeSpan timestamp) => Volatile.Read(ref _receiver)?.Invoke(message, timestamp);

    internal void Fail(string reason)
    {
        if (Interlocked.CompareExchange(ref _state, (int)EndpointState.Faulted, (int)EndpointState.Open) == (int)EndpointState.Open)
        {
            StateChanged?.Invoke(this, new EndpointStateChangedEventArgs(EndpointState.Faulted, reason));
        }
    }
}
