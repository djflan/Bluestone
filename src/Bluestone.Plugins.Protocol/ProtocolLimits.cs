namespace Bluestone.Plugins.Protocol;

/// <summary>Hard limits of the plugin protocol. Every length read from the wire is checked against these before anything is allocated.</summary>
public static class ProtocolLimits
{
    /// <summary>The protocol version both sides must agree on in the Hello handshake.</summary>
    public const int ProtocolVersion = 3;

    /// <summary>Bytes of the little-endian length prefix in front of every frame.</summary>
    public const int LengthPrefixBytes = 4;

    /// <summary>Bytes of the frame header after the length prefix: message type (uint16) and request id (uint32).</summary>
    public const int FrameHeaderBytes = 6;

    /// <summary>Largest frame body (header plus payload) accepted, in bytes.</summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    /// <summary>Largest UTF-8 string, in bytes.</summary>
    public const int MaxStringBytes = 4096;

    /// <summary>Largest opaque plugin state, in bytes. Leaves room for the rest of the frame.</summary>
    public const int MaxStateBytes = MaxFrameBytes - (64 * 1024);

    public const int MaxParameters = 4096;

    public const int MaxPluginsPerModule = 1024;

    public const int MaxChannels = 64;

    public const int MaxBlockFrames = 8192;

    public const int MinSlotCount = 2;

    public const int MaxSlotCount = 8;

    public const int MaxEventCapacity = 4096;

    public const int MaxParameterChangeCapacity = 4096;

    /// <summary>Most events in one render request or reply (each costs at least 12 bytes on the wire).</summary>
    public const int MaxRenderEvents = 131_072;

    /// <summary>Most parameter changes in one render request (20 bytes each on the wire).</summary>
    public const int MaxRenderParameterChanges = 32_768;

    /// <summary>Most tempo changes in one render request.</summary>
    public const int MaxRenderTempoChanges = 4096;

    /// <summary>Longest timeline one render request may cover: 24 hours at the highest sample rate.</summary>
    public const long MaxRenderFrames = 24L * 60 * 60 * 768_000;

    public const double MinSampleRate = 1000;

    public const double MaxSampleRate = 768_000;
}
