using System.Buffers.Binary;

namespace Bluestone.Tests.Unit.Midi.Files;

/// <summary>Builds small, hand-authored MIDI files for tests.</summary>
internal static class SmfBytes
{
    public static readonly byte[] EndOfTrack = [0x00, 0xFF, 0x2F, 0x00];

    public static byte[] File(int format, int division, params byte[][] trackBodies) =>
        [.. Header(format, trackBodies.Length, division), .. trackBodies.SelectMany(body => Chunk("MTrk", body))];

    public static byte[] Header(int format, int tracks, int division)
    {
        var bytes = new byte[14];
        "MThd"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 6);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), (ushort)format);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), (ushort)tracks);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), (ushort)division);
        return bytes;
    }

    public static byte[] Chunk(string type, byte[] body, int? declaredLength = null)
    {
        var bytes = new byte[8 + body.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)(declaredLength ?? body.Length));
        body.CopyTo(bytes, 8);
        return bytes;
    }

    public static byte[] MTrk(params byte[] events) => [.. events, .. EndOfTrack];
}
