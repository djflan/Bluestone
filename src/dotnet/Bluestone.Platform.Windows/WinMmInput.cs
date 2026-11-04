using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Midi.Wire;

namespace Bluestone.Platform.Windows;

/// <summary>Receives timestamped MIDI 1.0 data from WinMM and requeues a bounded pool of SysEx buffers.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WinMmInput : IMidiInput
{
    private const int LongMessageSlots = 8;
    private const int LongMessageCapacity = 8192;
    private static readonly uint HeaderSize = (uint)sizeof(WinMm.MidiHeader);

    private readonly WinMmProvider _provider;
    private readonly WinMm.MidiHeader* _headers;
    private readonly byte* _buffers;
    private readonly bool[] _prepared = new bool[LongMessageSlots];
    private readonly MidiStreamParser _parser = new();
    private readonly MidiMessageHandler _onMessage;
    private GCHandle _self;
    private IntPtr _handle;
    private MidiReceiveHandler? _receiver;
    private readonly TimeSpan _startTime;
    private TimeSpan _timestamp;
    private int _state = (int)EndpointState.Open;
    private int _callbacks;

    public WinMmInput(WinMmProvider provider, IMonotonicClock clock, EndpointDescriptor endpoint, uint device)
    {
        _provider = provider;
        Endpoint = endpoint;
        _onMessage = Deliver;
        _self = GCHandle.Alloc(this);

        var status = WinMm.midiInOpen(out _handle, device, &OnMessage, (nuint)(nint)GCHandle.ToIntPtr(_self), WinMm.CallbackFunction);
        if (status != WinMm.NoError)
        {
            _self.Free();
            throw Unavailable(endpoint, "could not open", status);
        }

        try
        {
            _headers = (WinMm.MidiHeader*)NativeMemory.AllocZeroed(LongMessageSlots, HeaderSize);
            _buffers = (byte*)NativeMemory.Alloc((nuint)(LongMessageSlots * LongMessageCapacity));
            for (var i = 0; i < LongMessageSlots; i++)
            {
                var header = &_headers[i];
                header->Data = _buffers + i * LongMessageCapacity;
                header->BufferLength = LongMessageCapacity;
                status = WinMm.midiInPrepareHeader(_handle, header, HeaderSize);
                if (status != WinMm.NoError)
                {
                    throw Unavailable(endpoint, "could not prepare a SysEx buffer", status);
                }

                _prepared[i] = true;
                status = WinMm.midiInAddBuffer(_handle, header, HeaderSize);
                if (status != WinMm.NoError)
                {
                    throw Unavailable(endpoint, "could not queue a SysEx buffer", status);
                }
            }

            _startTime = clock.Now;
            status = WinMm.midiInStart(_handle);
            if (status != WinMm.NoError)
            {
                throw Unavailable(endpoint, "could not start", status);
            }
        }
        catch
        {
            Volatile.Write(ref _state, (int)EndpointState.Closed);
            if (CloseNative() && _self.IsAllocated)
            {
                _self.Free();
            }
            throw;
        }
    }

    public EndpointDescriptor Endpoint { get; }

    public EndpointState State => (EndpointState)Volatile.Read(ref _state);

    public event EventHandler<EndpointStateChangedEventArgs>? StateChanged;

    public void MarkDisconnected() => Transition(EndpointState.Disconnected, "The WinMM endpoint was removed.");

    public void SetReceiver(MidiReceiveHandler? handler) => Volatile.Write(ref _receiver, handler);

    public void Dispose()
    {
        if (!Transition(EndpointState.Closed, null))
        {
            return;
        }

        if (CloseNative() && _self.IsAllocated)
        {
            _self.Free();
        }

        _provider.Forget(this);
    }

    private static EndpointUnavailableException Unavailable(EndpointDescriptor endpoint, string action, uint status) =>
        new(endpoint.Id, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"WinMM {action} the device (MMRESULT {status})"));

    private bool CloseNative()
    {
        if (_handle == IntPtr.Zero)
        {
            return true;
        }

        _ = WinMm.midiInStop(_handle);
        var reset = WinMm.midiInReset(_handle);
        if (reset != WinMm.NoError)
        {
            Trace.TraceWarning($"WinMM could not reset input {Endpoint.Id} (MMRESULT {reset}); retaining its native buffers.");
            return false;
        }

        var deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 2);
        while (Volatile.Read(ref _callbacks) != 0 && Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(1);
        }

        if (Volatile.Read(ref _callbacks) != 0)
        {
            Trace.TraceWarning($"WinMM callbacks did not drain for input {Endpoint.Id}; retaining its native buffers.");
            return false;
        }

        if (_headers != null)
        {
            for (var i = 0; i < LongMessageSlots; i++)
            {
                if (_prepared[i])
                {
                    while (true)
                    {
                        var status = WinMm.midiInUnprepareHeader(_handle, &_headers[i], HeaderSize);
                        if (status == WinMm.NoError)
                        {
                            _prepared[i] = false;
                            break;
                        }

                        if (status != WinMm.StillPlaying || Stopwatch.GetTimestamp() >= deadline)
                        {
                            Trace.TraceWarning($"WinMM could not unprepare input buffer {i} for {Endpoint.Id} (MMRESULT {status}); retaining its native buffers.");
                            return false;
                        }

                        Thread.Sleep(1);
                    }
                }
            }
        }

        var close = WinMm.midiInClose(_handle);
        if (close != WinMm.NoError)
        {
            Trace.TraceWarning($"WinMM could not close input {Endpoint.Id} (MMRESULT {close}); retaining its native buffers.");
            return false;
        }

        _handle = IntPtr.Zero;
        NativeMemory.Free(_buffers);
        NativeMemory.Free(_headers);
        return true;
    }

    private void Deliver(ReadOnlySpan<byte> message) => Volatile.Read(ref _receiver)?.Invoke(message, _timestamp);

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
                StateChanged?.Invoke(this, new EndpointStateChangedEventArgs(state, reason));
                return true;
            }
        }
    }

    [UnmanagedCallersOnly]
    private static void OnMessage(IntPtr handle, uint message, nuint instance, nuint param1, nuint param2)
    {
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)instance).Target is not WinMmInput input)
            {
                return;
            }

            Interlocked.Increment(ref input._callbacks);
            try
            {
                input.Receive(message, param1, param2);
            }
            finally
            {
                Interlocked.Decrement(ref input._callbacks);
            }
        }
#pragma warning disable CA1031 // An exception must never unwind into the WinMM callback.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void Receive(uint message, nuint param1, nuint param2)
    {
        if (State != EndpointState.Open)
        {
            return;
        }

        _timestamp = _startTime + TimeSpan.FromMilliseconds((uint)param2);
        if (message == WinMm.MidiData)
        {
            var packed = (uint)param1;
            var status = (byte)packed;
            var length = status switch
            {
                >= 0xF8 => 1,
                0xF1 or 0xF3 => 2,
                0xF2 => 3,
                0xF6 => 1,
                >= 0x80 and < 0xF0 => (status & 0xE0) == 0xC0 ? 2 : 3,
                _ => 0,
            };
            if (length == 0)
            {
                return;
            }

            Span<byte> bytes = stackalloc byte[3];
            bytes[0] = status;
            bytes[1] = (byte)(packed >> 8);
            bytes[2] = (byte)(packed >> 16);
            _parser.Feed(bytes[..length], _onMessage);
        }
        else if (message == WinMm.MidiLongData)
        {
            var header = (WinMm.MidiHeader*)param1;
            if (header != null && header->Data != null && header->BytesRecorded > 0 && header->BytesRecorded <= header->BufferLength)
            {
                _parser.Feed(new ReadOnlySpan<byte>(header->Data, (int)header->BytesRecorded), _onMessage);
            }

            if (header != null && State == EndpointState.Open && WinMm.midiInAddBuffer(_handle, header, HeaderSize) != WinMm.NoError)
            {
                Transition(EndpointState.Disconnected, "WinMM could not requeue a SysEx buffer.");
            }
        }
        else if (message == WinMm.MidiLongError)
        {
            _parser.Reset();
            var header = (WinMm.MidiHeader*)param1;
            if (header != null && State == EndpointState.Open)
            {
                header->BytesRecorded = 0;
                if (WinMm.midiInAddBuffer(_handle, header, HeaderSize) != WinMm.NoError)
                {
                    Transition(EndpointState.Disconnected, "WinMM could not requeue an errored SysEx buffer.");
                }
            }
        }
        else if (message == WinMm.MidiError)
        {
            _parser.Reset();
        }
    }
}
