namespace Bluestone.Domain.Midi;

/// <summary>
/// The 7-bit checksum Roland data set (DT1) messages and Yamaha bulk dumps end with: the value that makes
/// the covered bytes sum to 0 (mod 128). Roland covers the address and data; Yamaha covers the byte count,
/// address, and data. The arithmetic is the same.
/// </summary>
public static class SysExChecksum
{
    public static byte Compute(ReadOnlySpan<byte> covered)
    {
        var sum = 0;
        foreach (var b in covered)
        {
            sum += b;
        }

        return (byte)((128 - (sum & 0x7F)) & 0x7F);
    }
}
