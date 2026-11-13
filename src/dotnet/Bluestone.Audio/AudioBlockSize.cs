namespace Bluestone.Audio;

/// <summary>
/// The engine's render block for a device. Plugins need one block size throughout, so the engine renders fixed blocks;
/// matching them to the device's own callback means every callback renders the same whole number of blocks, instead of
/// alternating between one and two (which doubles the work, and the plugin collect waits, in some callbacks).
/// </summary>
public static class AudioBlockSize
{
    /// <summary>The smallest block the engine renders.</summary>
    public const int Minimum = 32;

    /// <summary>The largest block plugin instances are prepared for, and so the largest the engine renders.</summary>
    public const int DefaultMaximum = 1024;

    /// <summary>The block when neither the device nor the settings name a size.</summary>
    public const int Fallback = 256;

    /// <summary>
    /// The device's callback size when it is known and fits <paramref name="maxBlockFrames"/>; otherwise its largest
    /// divisor that fits; otherwise the requested buffer size (or <see cref="Fallback"/>) within the limits.
    /// </summary>
    /// <param name="deviceBufferFrames">The device's actual callback size, or 0 when it does not say.</param>
    /// <param name="requestedBufferFrames">The buffer size asked for, or 0 for the device's default.</param>
    /// <param name="maxBlockFrames">The largest block the plugins accept.</param>
    public static int For(int deviceBufferFrames, int requestedBufferFrames, int maxBlockFrames)
    {
        var maximum = Math.Max(Minimum, maxBlockFrames);
        if (deviceBufferFrames >= Minimum)
        {
            for (var parts = 1; deviceBufferFrames / parts >= Minimum; parts++)
            {
                if (deviceBufferFrames % parts == 0 && deviceBufferFrames / parts <= maximum)
                {
                    return deviceBufferFrames / parts;
                }
            }
        }

        return Math.Clamp(requestedBufferFrames > 0 ? requestedBufferFrames : Fallback, Minimum, maximum);
    }
}
