using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Wire;

namespace Bluestone.Platform.CoreMidi;

/// <summary>Shared one-way state handling for CoreMIDI handles.</summary>
[SupportedOSPlatform("macos")]
internal abstract class CoreMidiHandle(EndpointDescriptor endpoint)
{
    private int _state = (int)EndpointState.Open;

    public EndpointDescriptor Endpoint { get; } = endpoint;

    public EndpointState State => (EndpointState)Volatile.Read(ref _state);

    public event EventHandler<EndpointStateChangedEventArgs>? StateChanged;

    public void MarkDisconnected() => Transition(EndpointState.Disconnected, "The CoreMIDI endpoint went away.");

    protected bool Transition(EndpointState state, string? reason)
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
}

/// <summary>
/// Sends to a CoreMIDI destination with <c>MIDISend</c> (timestamped), or out of a Bluestone virtual
/// source with <c>MIDIReceived</c>. Small messages are built on the stack; nothing is allocated per send.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class CoreMidiOutput(CoreMidiProvider provider, EndpointDescriptor endpoint, uint target, bool isVirtualSource)
    : CoreMidiHandle(endpoint), IMidiOutput
{
    private const int MaxMessage = ushort.MaxValue;
    private const int StackLimit = 128;

    public SendResult Send(ReadOnlySpan<byte> message, MidiTimestamp timestamp)
    {
        switch (State)
        {
            case EndpointState.Disconnected:
                return SendResult.Disconnected;
            case EndpointState.Closed or EndpointState.Faulted:
                return SendResult.Closed;
        }

        var kind = MidiWire.Classify(message);
        if (kind == MidiMessageClass.Invalid || message.Length > MaxMessage)
        {
            return SendResult.Rejected;
        }

        var size = Native.PacketListHeader + Native.PacketHeader + message.Length;
        byte[]? rented = null;
        var buffer = size <= StackLimit ? stackalloc byte[StackLimit] : (rented = ArrayPool<byte>.Shared.Rent(size));
        try
        {
            // MIDIPacketList { UInt32 numPackets; MIDIPacket { UInt64 timeStamp; UInt16 length; Byte data[] } }, 4-byte packed.
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, 1);
            var host = timestamp.IsImmediate || isVirtualSource ? 0UL : provider.HostTime.ToHost(timestamp.Time);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer[4..], host);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer[12..], (ushort)message.Length);
            message.CopyTo(buffer[14..]);

            int status;
            fixed (byte* packetList = buffer)
            {
                status = isVirtualSource ? Native.MIDIReceived(target, packetList) : Native.MIDISend(provider.OutputPort, target, packetList);
            }

            return status == Native.NoError ? SendResult.Sent : SendResult.Disconnected;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public void Dispose()
    {
        if (Transition(EndpointState.Closed, null))
        {
            provider.Forget(this);
        }
    }
}

/// <summary>
/// Receives from a CoreMIDI source on CoreMIDI's high-priority thread, splitting packets into complete
/// messages and converting host timestamps to Bluestone clock time.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class CoreMidiInput : CoreMidiHandle, IMidiInput
{
    private readonly CoreMidiProvider _provider;
    private readonly MidiStreamParser _parser = new();
    private readonly MidiMessageHandler _onMessage;
    private GCHandle _self;
    private uint _port;
    private MidiReceiveHandler? _receiver;
    private TimeSpan _currentTimestamp;

    public CoreMidiInput(CoreMidiProvider provider, EndpointDescriptor endpoint, uint source)
        : base(endpoint)
    {
        _provider = provider;
        _onMessage = Deliver;
        _self = GCHandle.Alloc(this);
        var name = Native.CreateString("Bluestone In");
        try
        {
            var status = Native.MIDIInputPortCreate(provider.Client, name, &OnRead, GCHandle.ToIntPtr(_self), out _port);
            if (status == Native.NoError)
            {
                status = Native.MIDIPortConnectSource(_port, source, IntPtr.Zero);
            }

            if (status != Native.NoError)
            {
                Release();
                throw new EndpointUnavailableException(endpoint.Id, string.Create(CultureInfo.InvariantCulture, $"CoreMIDI could not connect (OSStatus {status})"));
            }
        }
        finally
        {
            Native.CFRelease(name);
        }
    }

    public void SetReceiver(MidiReceiveHandler? handler) => Volatile.Write(ref _receiver, handler);

    public void Dispose()
    {
        if (Transition(EndpointState.Closed, null))
        {
            Release();
            _provider.Forget(this);
        }
    }

    private void Release()
    {
        if (_port != 0)
        {
            _ = Native.MIDIPortDispose(_port);
            _port = 0;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private void Deliver(ReadOnlySpan<byte> message) => Volatile.Read(ref _receiver)?.Invoke(message, _currentTimestamp);

    [UnmanagedCallersOnly]
    private static void OnRead(IntPtr packetList, IntPtr readRefCon, IntPtr sourceRefCon)
    {
        if (GCHandle.FromIntPtr(readRefCon).Target is not CoreMidiInput input || input.State != EndpointState.Open)
        {
            return;
        }

        var count = *(uint*)packetList;
        var packet = (byte*)packetList + Native.PacketListHeader;
        for (var i = 0; i < count; i++)
        {
            var timestamp = *(ulong*)packet;
            var length = *(ushort*)(packet + 8);
            var data = packet + Native.PacketHeader;
            input._currentTimestamp = input._provider.HostTime.FromHost(timestamp);
            input._parser.Feed(new ReadOnlySpan<byte>(data, length), input._onMessage);

            // MIDIPacketNext: packets are 4-byte aligned on ARM.
            var next = (nuint)(data + length);
            if (RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm)
            {
                next = (next + 3) & ~(nuint)3;
            }

            packet = (byte*)next;
        }
    }
}
