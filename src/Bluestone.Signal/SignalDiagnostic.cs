using Bluestone.Domain.Devices;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Signal;

public enum SignalDiagnosticCode
{
    /// <summary>A connection failed routing validation and was left out of the graph.</summary>
    InvalidConnection,

    /// <summary>A connection would close a loop and was left out.</summary>
    Feedback,

    /// <summary>A track's events reach no output, no instrument, and no other track.</summary>
    Unrouted,

    /// <summary>A device's definition is not known on this machine. Its signal passes through it unchanged.</summary>
    DeviceUnavailable,

    /// <summary>A device that changes events has no processor in Bluestone's process (a plugin runs only in a worker), so its events pass through unchanged here.</summary>
    NotProcessedHere,

    /// <summary>Something a device reported, such as notes it dropped.</summary>
    DeviceReport,

    /// <summary>A software instrument receives events; only a plugin instrument sounds, and only while audio output is on (ADR 0032).</summary>
    NotAudible,

    /// <summary>Clip events were replaced by automation of the same target.</summary>
    ReplacedByAutomation,

    /// <summary>An automation lane was left out because another lane controls the same target.</summary>
    LaneDropped,

    /// <summary>A device automation lane targets a device that is not in the project. The lane is kept.</summary>
    AutomationTargetMissing,
}

/// <summary>A finding from evaluating the signal graph, worded for the musician, with what it is about.</summary>
public sealed record SignalDiagnostic(SignalDiagnosticCode Code, string Message)
{
    public TrackId? Track { get; init; }

    public DeviceId? Device { get; init; }

    public ConnectionId? Connection { get; init; }
}
