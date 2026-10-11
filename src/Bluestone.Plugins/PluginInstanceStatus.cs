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
/// Running     -> Unavailable | Unloaded
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
        (PluginInstanceState.Running, PluginInstanceState.Unavailable or PluginInstanceState.Unloaded) => true,
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

/// <summary>How plugin instances are grouped into worker processes.</summary>
public enum IsolationPolicy
{
    /// <summary>One worker process per instance. The default: a crash takes down exactly one instance.</summary>
    PerInstance,

    /// <summary>Instances of the same module share a worker; a crash takes down all of them.</summary>
    PerModule,

    /// <summary>Opt-in: all instances with this policy share one worker. For plugins the user trusts.</summary>
    SharedTrusted,

    /// <summary>
    /// Hosting inside the Bluestone process. Not implemented: refused unless explicitly allowed by
    /// <see cref="PluginHostOptions.AllowInProcessTrusted"/>, and even then it throws <see cref="NotSupportedException"/>.
    /// </summary>
    InProcessTrusted,
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
