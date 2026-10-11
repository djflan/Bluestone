using System.Globalization;

namespace Bluestone.Domain.Midi;

/// <summary>
/// A complete MIDI 1.0 System Exclusive message, including the leading <c>F0</c> and trailing <c>F7</c>.
/// Every byte in between is a 7-bit data byte.
/// </summary>
public sealed class SysExMessage : IEquatable<SysExMessage>
{
    public const byte Start = 0xF0;
    public const byte End = 0xF7;

    /// <summary>Upper bound on message size, to keep untrusted input from exhausting memory.</summary>
    public const int MaxLength = 1 << 20;

    private SysExMessage(ByteBlock bytes) => Bytes = bytes;

    /// <summary>The full message, <c>F0</c> through <c>F7</c>.</summary>
    public ByteBlock Bytes { get; }

    public int Length => Bytes.Length;

    /// <summary>The manufacturer ID: one byte, or three when the first byte is <c>00</c> (extended IDs).</summary>
    public ReadOnlySpan<byte> ManufacturerId
    {
        get
        {
            var body = Bytes.Span[1..^1];
            if (body.IsEmpty)
            {
                return [];
            }

            return body[0] == 0x00 && body.Length >= 3 ? body[..3] : body[..1];
        }
    }

    /// <summary>Validates and copies a complete message.</summary>
    /// <exception cref="FormatException">The bytes are not a well-formed, bounded SysEx message.</exception>
    public static SysExMessage Create(ReadOnlySpan<byte> bytes) =>
        TryCreate(bytes, out var message, out var error) ? message : throw new FormatException(error);

    public static bool TryCreate(ReadOnlySpan<byte> bytes, out SysExMessage message, out string error)
    {
        message = null!;
        if (bytes.Length < 2 || bytes[0] != Start || bytes[^1] != End)
        {
            error = "A SysEx message must start with F0 and end with F7.";
            return false;
        }

        if (bytes.Length > MaxLength)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"A SysEx message must not exceed {MaxLength} bytes.");
            return false;
        }

        var body = bytes[1..^1];
        var invalid = body.IndexOfAnyInRange((byte)0x80, (byte)0xFF);
        if (invalid >= 0)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"SysEx data byte at offset {invalid + 1} is not a 7-bit value.");
            return false;
        }

        message = new SysExMessage(ByteBlock.Copy(bytes));
        error = string.Empty;
        return true;
    }

    public bool Equals(SysExMessage? other) => other is not null && Bytes.Equals(other.Bytes);

    public override bool Equals(object? obj) => Equals(obj as SysExMessage);

    public override int GetHashCode() => Bytes.GetHashCode();

    /// <summary>Describes the message without revealing its payload.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"SysEx ({Length} bytes, manufacturer {Convert.ToHexString(ManufacturerId)})");
}
