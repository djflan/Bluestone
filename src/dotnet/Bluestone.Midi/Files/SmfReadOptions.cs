namespace Bluestone.Midi.Files;

/// <summary>Limits applied while reading untrusted MIDI files.</summary>
public sealed record SmfReadOptions
{
    public static readonly SmfReadOptions Default = new();

    public int MaxFileBytes { get; init; } = 64 * 1024 * 1024;

    public int MaxTracks { get; init; } = 4096;

    public int MaxEvents { get; init; } = 10_000_000;

    /// <summary>Largest meta or SysEx payload accepted; larger events end parsing of their track.</summary>
    public int MaxEventDataBytes { get; init; } = 1 << 20;
}
