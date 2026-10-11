namespace Bluestone.Plugins.Protocol;

/// <summary>
/// Raised for any malformed, truncated, oversized, unknown, or out-of-contract data on the plugin control plane or
/// in a data-plane file header. Decoding never fails with any other exception type.
/// </summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException()
        : base("Plugin protocol violation.")
    {
    }

    public ProtocolException(string message)
        : base(message)
    {
    }

    public ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
