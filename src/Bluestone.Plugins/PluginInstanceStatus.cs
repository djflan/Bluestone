using System.Runtime.InteropServices;
using Bluestone.Plugins.Protocol;

namespace Bluestone.Plugins;

public enum PluginInstanceState
{
    Starting,
    Running,
    Unavailable,
    Restarting,
    Quarantined,
    Unloaded,
}

/// <summary>Why an instance is <see cref="PluginInstanceState.Unavailable"/>.</summary>
public enum UnavailableReason
{
    None,

    /// <summary>The worker process died unexpectedly.</summary>
    Crashed,

    /// <summary>The worker stopped answering heartbeats or requests and was killed.</summary>
    Hung,

    /// <summary>The worker could not be launched, did not connect, or refused to create the instance.</summary>
    FailedToStart,

    /// <summary>The worker sent invalid frames or an invalid data-plane file and was killed.</summary>
    ProtocolError,

    /// <summary>The worker does not have the requested plugin.</summary>
    Missing,
}

/// <summary>
/// The user-visible state of a plugin instance. Allowed transitions (anything else is a bug and throws):
/// <code>
/// Starting    -> Running | Unavailable | Unloaded
/// Running     -> Unavailable | Restarting (moved to another worker) | Unloaded
/// Unavailable -> Restarting | Quarantined | Unloaded
/// Restarting  -> Running | Unavailable | Unloaded
/// Quarantined -> Restarting (manual restart only) | Unloaded
/// Unloaded    -> (final)
/// </code>
/// </summary>
public sealed record PluginInstanceStatus
{
    public PluginInstanceStatus(PluginInstanceState state, UnavailableReason reason = UnavailableReason.None, string? message = null)
    {
        if ((state == PluginInstanceState.Unavailable) != (reason != UnavailableReason.None))
        {
            throw new ArgumentException("A reason is given exactly when the state is Unavailable.", nameof(reason));
        }

        State = state;
        Reason = reason;
        Message = message;
    }

    public static PluginInstanceStatus Starting { get; } = new(PluginInstanceState.Starting);

    public static PluginInstanceStatus Running { get; } = new(PluginInstanceState.Running);

    public static PluginInstanceStatus Restarting { get; } = new(PluginInstanceState.Restarting);

    public static PluginInstanceStatus Unloaded { get; } = new(PluginInstanceState.Unloaded);

    public PluginInstanceState State { get; }

    public UnavailableReason Reason { get; }

    /// <summary>Human-readable detail for the user, if any.</summary>
    public string? Message { get; }

    public static PluginInstanceStatus Unavailable(UnavailableReason reason, string? message = null) => new(PluginInstanceState.Unavailable, reason, message);

    public static PluginInstanceStatus Quarantined(string? message = null) => new(PluginInstanceState.Quarantined, UnavailableReason.None, message);

    public static bool IsAllowed(PluginInstanceState from, PluginInstanceState to) => (from, to) switch
    {
        (PluginInstanceState.Starting, PluginInstanceState.Running or PluginInstanceState.Unavailable or PluginInstanceState.Unloaded) => true,
        (PluginInstanceState.Running, PluginInstanceState.Unavailable or PluginInstanceState.Restarting or PluginInstanceState.Unloaded) => true,
        (PluginInstanceState.Unavailable, PluginInstanceState.Restarting or PluginInstanceState.Quarantined or PluginInstanceState.Unloaded) => true,
        (PluginInstanceState.Restarting, PluginInstanceState.Running or PluginInstanceState.Unavailable or PluginInstanceState.Unloaded) => true,
        (PluginInstanceState.Quarantined, PluginInstanceState.Restarting or PluginInstanceState.Unloaded) => true,
        _ => false,
    };

    public override string ToString() => State == PluginInstanceState.Unavailable ? $"Unavailable({Reason})" : State.ToString();
}

public sealed class PluginStatusChangedEventArgs(PluginInstanceStatus previous, PluginInstanceStatus current) : EventArgs
{
    public PluginInstanceStatus Previous { get; } = previous;

    public PluginInstanceStatus Current { get; } = current;
}

/// <summary>
/// How plugin instances share worker processes (ADR 0034). A worker crash takes down every instance in it, so the mode
/// sets the blast radius of a crash against the number of processes. Instances share a worker only when their
/// <see cref="WorkerKeys">worker key</see> is equal, which also keeps plugins of different architectures apart.
/// </summary>
public enum HostingMode
{
    /// <summary>One worker per instance: a crash takes down exactly one instance.</summary>
    Individually,

    /// <summary>One worker per plugin module (a VST3 bundle, a CLAP file): all instances of it, and of other plugins in the same module.</summary>
    ByPlugin,

    /// <summary>One worker per manufacturer (the vendor the scan reported); a plugin without a vendor is hosted by plugin.</summary>
    ByManufacturer,

    /// <summary>One worker for every instance in this mode.</summary>
    Together,

    /// <summary>
    /// Hosting inside the Bluestone process. Not implemented: refused unless explicitly allowed by
    /// <see cref="PluginHostOptions.AllowWithinBluestone"/>, and even then it throws <see cref="NotSupportedException"/>.
    /// </summary>
    WithinBluestone,
}

/// <summary>The worker keys of <see cref="HostingMode"/>: instances with equal keys share one worker process.</summary>
public static class WorkerKeys
{
    /// <summary>
    /// The key for an instance of <paramref name="plugin"/> hosted in <paramref name="mode"/> by a worker of
    /// <paramref name="architecture"/>. Every shared key names the architecture, because x64 and ARM64 plugin binaries
    /// cannot be loaded into one process.
    /// </summary>
    public static string For(HostingMode mode, PluginIdentity plugin, PluginInstanceId instance, Architecture architecture)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var arch = architecture.ToString().ToLowerInvariant();
        var vendor = plugin.Vendor.Trim();
        return mode switch
        {
            HostingMode.Individually => $"instance:{instance}",
            HostingMode.ByManufacturer when vendor.Length > 0 => $"{arch}:vendor:{vendor.ToUpperInvariant()}",
            HostingMode.ByPlugin or HostingMode.ByManufacturer => $"{arch}:module:{plugin.Format}:{plugin.ModuleId}",
            HostingMode.Together => $"{arch}:together",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Plugins are hosted only in worker processes."),
        };
    }
}

/// <summary>What <see cref="PluginInstance.ProcessBlock"/> outputs when the plugin cannot deliver a block.</summary>
public enum FailurePolicy
{
    /// <summary>Exact digital silence and no events. The default, and the only choice for instruments and MIDI effects.</summary>
    Silence,

    /// <summary>
    /// Opt-in for audio effects whose input and output channel counts match: the dry input, delayed by the pipeline
    /// so its timing does not jump. Never the plugin's (possibly stale) output.
    /// </summary>
    BypassInput,
}

/// <summary>A parameter the plugin changed itself, with its new normalized value.</summary>
public sealed class PluginParameterEditedEventArgs(uint parameterId, double value) : EventArgs
{
    public uint ParameterId { get; } = parameterId;

    public double Value { get; } = value;
}
