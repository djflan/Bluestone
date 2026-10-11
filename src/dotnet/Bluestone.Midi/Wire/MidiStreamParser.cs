namespace Bluestone.Midi.Wire;

/// <summary>Receives one complete message. The span is only valid during the call.</summary>
public delegate void MidiMessageHandler(ReadOnlySpan<byte> message);

/// <summary>
/// Splits a MIDI 1.0 byte stream (as delivered by OS drivers) into complete messages: applies
/// running status, passes realtime bytes through immediately even inside other messages, and
/// accumulates SysEx across chunks up to a bound. Malformed bytes are dropped, never thrown.
/// </summary>
/// <remarks>Not thread-safe; use one parser per input. Allocates only its fixed buffers.</remarks>
public sealed class MidiStreamParser
{
    private readonly byte[] _sysEx;
    private readonly byte[] _message = new byte[3];
    private int _sysExLength;
    private bool _inSysEx;
    private bool _sysExOverflow;
    private byte _runningStatus;
    private int _messageLength;
    private int _expected;

    /// <param name="maxSysExLength">SysEx messages longer than this are discarded and counted in <see cref="DroppedMessages"/>.</param>
    public MidiStreamParser(int maxSysExLength = 65536)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSysExLength, 2);
        _sysEx = new byte[maxSysExLength];
    }

    /// <summary>Messages discarded because they were malformed or too large.</summary>
    public long DroppedMessages { get; private set; }

    public void Feed(ReadOnlySpan<byte> bytes, MidiMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        foreach (var b in bytes)
        {
            Feed(b, handler);
        }
    }

    private void Feed(byte b, MidiMessageHandler handler)
    {
        if (b >= 0xF8)
        {
            // Realtime messages may appear anywhere, including inside SysEx, and never disturb parsing.
            if (b is not (0xF9 or 0xFD))
            {
                ReadOnlySpan<byte> single = [b];
                handler(single);
            }

            return;
        }

        if (_inSysEx)
        {
            if (b == 0xF7)
            {
                AppendSysEx(b);
                if (!_sysExOverflow)
                {
                    handler(_sysEx.AsSpan(0, _sysExLength));
                }

                _inSysEx = false;
                return;
            }

            if (b < 0x80)
            {
                AppendSysEx(b);
                return;
            }

            // Any other status byte ends an unterminated SysEx, which is discarded.
            DroppedMessages++;
            _inSysEx = false;
        }

        if (b == 0xF0)
        {
            _inSysEx = true;
            _sysExOverflow = false;
            _sysExLength = 0;
            _runningStatus = 0;
            _messageLength = 0;
            AppendSysEx(b);
            return;
        }

        if (b >= 0x80)
        {
            if (_messageLength > 0)
            {
                DroppedMessages++;
            }

            _messageLength = 0;
            if (b >= 0xF0)
            {
                // System common messages cancel running status.
                _runningStatus = 0;
                _expected = b switch
                {
                    0xF1 or 0xF3 => 2,
                    0xF2 => 3,
                    0xF6 => 1,
                    _ => 0,
                };

                if (_expected == 0)
                {
                    DroppedMessages++;
                    return;
                }
            }
            else
            {
                _runningStatus = b;
                _expected = (b & 0xF0) is 0xC0 or 0xD0 ? 2 : 3;
            }

            _message[_messageLength++] = b;
            Complete(handler);
            return;
        }

        if (_messageLength == 0)
        {
            if (_runningStatus == 0)
            {
                DroppedMessages++;
                return;
            }

            _message[_messageLength++] = _runningStatus;
            _expected = (_runningStatus & 0xF0) is 0xC0 or 0xD0 ? 2 : 3;
        }

        _message[_messageLength++] = b;
        Complete(handler);
    }

    private void Complete(MidiMessageHandler handler)
    {
        if (_messageLength == _expected)
        {
            handler(_message.AsSpan(0, _messageLength));
            _messageLength = 0;
        }
    }

    private void AppendSysEx(byte b)
    {
        if (_sysExLength == _sysEx.Length)
        {
            if (!_sysExOverflow)
            {
                DroppedMessages++;
            }

            _sysExOverflow = true;
            return;
        }

        _sysEx[_sysExLength++] = b;
    }
}
