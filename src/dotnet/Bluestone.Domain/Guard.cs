namespace Bluestone.Domain;

internal static class Guard
{
    public static byte SevenBit(int value, string paramName)
    {
        if ((uint)value > 127)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "MIDI data values must be in the range 0-127.");
        }

        return (byte)value;
    }

    public static int InRange(int value, int min, int max, string paramName)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"Value must be in the range {min}-{max}.");
        }

        return value;
    }

    public static long NonNegative(long value, string paramName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Value must not be negative.");
        }

        return value;
    }
}
