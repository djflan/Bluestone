using System.Collections;

namespace Bluestone.Domain.Midi;

/// <summary>
/// An immutable, value-equal block of bytes used for SysEx and meta-event payloads.
/// </summary>
/// <remarks>
/// <see cref="ToString"/> reports only the length: payloads may contain proprietary or personal
/// data and must not reach logs by default.
/// </remarks>
public sealed class ByteBlock : IEquatable<ByteBlock>, IReadOnlyList<byte>
{
    private readonly byte[] _bytes;

    public static readonly ByteBlock Empty = new([]);

    private ByteBlock(byte[] bytes) => _bytes = bytes;

    public int Length => _bytes.Length;

    int IReadOnlyCollection<byte>.Count => _bytes.Length;

    public byte this[int index] => _bytes[index];

    public ReadOnlySpan<byte> Span => _bytes;

    public ReadOnlyMemory<byte> Memory => _bytes;

    /// <summary>Copies <paramref name="bytes"/>; later changes to the source do not affect the block.</summary>
    public static ByteBlock Copy(ReadOnlySpan<byte> bytes) => bytes.IsEmpty ? Empty : new ByteBlock(bytes.ToArray());

    /// <summary>Takes ownership of <paramref name="bytes"/> without copying. The caller must not modify the array afterwards.</summary>
    internal static ByteBlock Wrap(byte[] bytes) => bytes.Length == 0 ? Empty : new ByteBlock(bytes);

    public byte[] ToArray() => _bytes.ToArray();

    public bool Equals(ByteBlock? other) => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => Equals(obj as ByteBlock);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_bytes);
        return hash.ToHashCode();
    }

    public IEnumerator<byte> GetEnumerator() => ((IEnumerable<byte>)_bytes).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"{Length} bytes";

    public static bool operator ==(ByteBlock? left, ByteBlock? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(ByteBlock? left, ByteBlock? right) => !(left == right);
}
