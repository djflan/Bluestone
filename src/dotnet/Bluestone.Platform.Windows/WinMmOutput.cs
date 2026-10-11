using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Wire;

namespace Bluestone.Platform.Windows;

/// <summary>
/// Sends short messages with <c>midiOutShortMsg</c> and system exclusive with <c>midiOutLongMsg</c>,
/// both of which deliver immediately. Long messages use a fixed pool of buffers in native memory: a
/// buffer is reused once the driver marks it done, and a send that finds no free buffer reports
/// <see cref="SendResult.QueueFull"/> rather than waiting. A buffer only grows (reallocates) when a
/// larger message than it has held before arrives.
/// </summary>
/// <remarks>Like every <see cref="IMidiOutput"/>, sends come from one thread at a time.</remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WinMmOutput : IMidiOutput
{
    /// <summary>The longest system exclusive message accepted, matching the CoreMIDI adapter.</summary>
    public const int MaxMessage = ushort.MaxValue;

    /// <summary>How many long messages may be in the driver at once.</summary>
    public const int LongMessageSlots = 8;

    private const int InitialCapacity = 1024;
    private static readonly uint HeaderSize = (uint)sizeof(WinMm.MidiHeader);

    private readonly WinMmProvider _provider;
    private readonly IntPtr _handle;
    private readonly WinMm.MidiHeader* _headers;
    private readonly int[] _capacities = new int[LongMessageSlots];
    private readonly bool[] _queued = new bool[LongMessageSlots];
    private int _state = (int)EndpointState.Open;
    private int _sending;

    public WinMmOutput(WinMmProvider provider, EndpointDescriptor endpoint, IntPtr handle)
    {
        _provider = provider;
        Endpoint = endpoint;
        _handle = handle;
        _headers = (WinMm.MidiHeader*)NativeMemory.AllocZeroed(LongMessageSlots, HeaderSize);
        for (var i = 0; i < LongMessageSlots; i++)
        {
            _headers[i].Data = (byte*)NativeMemory.Alloc(InitialCapacity);
            _capacities[i] = InitialCapacity;
        }
    }

    public EndpointDescriptor Endpoint { get; }

    public EndpointState State => (EndpointState)Volatile.Read(ref _state);

    public event EventHandler<EndpointStateChangedEventArgs>? StateChanged;

    public SendResult Send(ReadOnlySpan<byte> message, MidiTimestamp timestamp)
    {
        // Dispose marks the handle closed, then waits for in-flight sends before closing the device.
        Interlocked.Increment(ref _sending);
        try
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

            if (kind == MidiMessageClass.SystemExclusive)
            {
                return SendLong(message);
            }

            var packed = (uint)message[0];
            if (message.Length > 1)
            {
                packed |= (uint)message[1] << 8;
            }

            if (message.Length > 2)
            {
                packed |= (uint)message[2] << 16;
            }

            return Map(WinMm.midiOutShortMsg(_handle, packed));
        }
        finally
        {
            Interlocked.Decrement(ref _sending);
        }
    }

    public void Dispose()
    {
        if (!Transition(EndpointState.Closed, null))
        {
            return;
        }

        var spinner = default(SpinWait);
        while (Volatile.Read(ref _sending) != 0)
        {
            spinner.SpinOnce();
        }

        // Reset turns off any sounding notes and returns every queued long-message buffer as done.
        _ = WinMm.midiOutReset(_handle);
        for (var i = 0; i < LongMessageSlots; i++)
        {
            if (_queued[i])
            {
                _ = WinMm.midiOutUnprepareHeader(_handle, &_headers[i], HeaderSize);
                _queued[i] = false;
            }
        }

        _ = WinMm.midiOutClose(_handle);
        for (var i = 0; i < LongMessageSlots; i++)
        {
            NativeMemory.Free(_headers[i].Data);
        }

        NativeMemory.Free(_headers);
        _provider.Forget(this);
    }

    private SendResult SendLong(ReadOnlySpan<byte> message)
    {
        var slot = ClaimSlot();
        if (slot < 0)
        {
            return SendResult.QueueFull;
        }

        var header = &_headers[slot];
        if (message.Length > _capacities[slot])
        {
            header->Data = (byte*)NativeMemory.Realloc(header->Data, (nuint)message.Length);
            _capacities[slot] = message.Length;
        }

        message.CopyTo(new Span<byte>(header->Data, message.Length));
        header->BufferLength = (uint)message.Length;
        header->BytesRecorded = (uint)message.Length;
        header->Flags = 0;

        var status = WinMm.midiOutPrepareHeader(_handle, header, HeaderSize);
        if (status != WinMm.NoError)
        {
            return Map(status);
        }

        status = WinMm.midiOutLongMsg(_handle, header, HeaderSize);
        if (status != WinMm.NoError)
        {
            _ = WinMm.midiOutUnprepareHeader(_handle, header, HeaderSize);
            return Map(status);
        }

        _queued[slot] = true;
        return SendResult.Sent;
    }

    /// <summary>Returns a slot the driver is not using, unpreparing finished ones; -1 if all are busy.</summary>
    private int ClaimSlot()
    {
        for (var i = 0; i < LongMessageSlots; i++)
        {
            if (!_queued[i])
            {
                return i;
            }

            if ((Volatile.Read(ref _headers[i].Flags) & WinMm.HeaderDone) != 0
                && WinMm.midiOutUnprepareHeader(_handle, &_headers[i], HeaderSize) != WinMm.StillPlaying)
            {
                _queued[i] = false;
                return i;
            }
        }

        return -1;
    }

    private SendResult Map(uint status)
    {
        switch (status)
        {
            case WinMm.NoError:
                return SendResult.Sent;
            case WinMm.NotReady:
                return SendResult.QueueFull;
            case WinMm.NoDriver or WinMm.NoDevice or WinMm.InvalidHandle:
                Transition(EndpointState.Disconnected, "The WinMM device went away.");
                return SendResult.Disconnected;
            default:
                Transition(EndpointState.Faulted, "WinMM reported an error.");
                return SendResult.Closed;
        }
    }

    private bool Transition(EndpointState state, string? reason)
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
