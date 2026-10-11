using Bluestone.Plugins.Protocol;

namespace Bluestone.Plugins;

/// <summary>A plugin-hosting operation failed: the worker refused a request, or it is not available.</summary>
public class PluginHostException : Exception
{
    public PluginHostException()
        : base("The plugin host operation failed.")
    {
    }

    public PluginHostException(string message)
        : base(message)
    {
    }

    public PluginHostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PluginHostException(ErrorCode code, string message)
        : base(message) => Code = code;

    /// <summary>The worker's error code, when the worker refused the request.</summary>
    public ErrorCode? Code { get; }
}

/// <summary>The instance (or its worker) is not running, so the request could not be served.</summary>
public sealed class PluginUnavailableException : PluginHostException
{
    public PluginUnavailableException()
        : base("The plugin instance is not available.")
    {
    }

    public PluginUnavailableException(string message)
        : base(message)
    {
    }

    public PluginUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
