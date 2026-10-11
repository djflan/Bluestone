using System.Collections.Immutable;
using System.Text.Json;

namespace Bluestone.Audio.Devices;

/// <summary>An audio output device as the operating system's audio service reports it.</summary>
/// <param name="Id">Stable id (host and device), used to open it and to remember it in settings.</param>
/// <param name="Name">Display name.</param>
/// <param name="Host">The audio service: CoreAudio, WASAPI, ALSA.</param>
/// <param name="IsDefault">The system's default output.</param>
/// <param name="DefaultSampleRate">The device's current or preferred rate, in Hz (0 when unknown).</param>
/// <param name="DefaultChannels">The device's preferred channel count (0 when unknown).</param>
/// <param name="SampleRates">Standard rates the device supports.</param>
/// <param name="ChannelCounts">Channel counts the device supports.</param>
/// <param name="MinBufferFrames">Smallest buffer the device accepts, in frames (0 when unknown).</param>
/// <param name="MaxBufferFrames">Largest buffer the device accepts, in frames (0 when unknown).</param>
public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    string Host,
    bool IsDefault,
    int DefaultSampleRate,
    int DefaultChannels,
    ImmutableArray<int> SampleRates,
    ImmutableArray<int> ChannelCounts,
    int MinBufferFrames,
    int MaxBufferFrames);

/// <summary>Lists output devices. Not for the audio thread (it allocates and asks the operating system).</summary>
public static class AudioDevices
{
    /// <summary>Whether audio output is available in this process: the native library loads and speaks this ABI.</summary>
    public static bool IsAvailable(out string? problem) => NativeMethods.TryCheck(out problem);

    /// <summary>The output devices, or none when the native library is unavailable.</summary>
    public static unsafe ImmutableArray<AudioDeviceInfo> List()
    {
        if (!NativeMethods.TryCheck(out _))
        {
            return [];
        }

        // The list can grow between the two calls; retry with the new length.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var length = NativeMethods.DevicesJson(null, 0);
            var buffer = new byte[length + 1024];
            long written;
            fixed (byte* pointer = buffer)
            {
                written = NativeMethods.DevicesJson(pointer, (nuint)buffer.Length);
            }

            if (written <= buffer.Length)
            {
                return Parse(buffer.AsSpan(0, (int)written));
            }
        }

        return [];
    }

    internal static ImmutableArray<AudioDeviceInfo> Parse(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var devices = ImmutableArray.CreateBuilder<AudioDeviceInfo>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            devices.Add(new AudioDeviceInfo(
                item.GetProperty("id").GetString() ?? string.Empty,
                item.GetProperty("name").GetString() ?? string.Empty,
                item.GetProperty("host").GetString() ?? string.Empty,
                item.GetProperty("isDefault").GetBoolean(),
                item.GetProperty("defaultSampleRate").GetInt32(),
                item.GetProperty("defaultChannels").GetInt32(),
                [.. item.GetProperty("sampleRates").EnumerateArray().Select(e => e.GetInt32())],
                [.. item.GetProperty("channelCounts").EnumerateArray().Select(e => e.GetInt32())],
                item.GetProperty("minBufferFrames").GetInt32(),
                item.GetProperty("maxBufferFrames").GetInt32()));
        }

        return devices.ToImmutable();
    }
}
