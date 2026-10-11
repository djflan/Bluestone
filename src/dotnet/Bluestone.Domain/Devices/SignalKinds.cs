namespace Bluestone.Domain.Devices;

/// <summary>What a connection carries.</summary>
public enum SignalKind
{
    /// <summary>Musical events: notes, controllers, program changes, SysEx. MIDI is the first such kind.</summary>
    Events,

    /// <summary>Audio sample blocks.</summary>
    Audio,
}

/// <summary>A set of <see cref="SignalKind"/>s, for what a device takes in and what it puts out.</summary>
[Flags]
public enum SignalKinds
{
    None = 0,
    Events = 1,
    Audio = 2,
}

public static class SignalKindExtensions
{
    public static bool Includes(this SignalKinds kinds, SignalKind kind) => kind switch
    {
        SignalKind.Events => kinds.HasFlag(SignalKinds.Events),
        SignalKind.Audio => kinds.HasFlag(SignalKinds.Audio),
        _ => false,
    };

    public static SignalKinds ToFlags(this SignalKind kind) => kind == SignalKind.Events ? SignalKinds.Events : SignalKinds.Audio;
}
