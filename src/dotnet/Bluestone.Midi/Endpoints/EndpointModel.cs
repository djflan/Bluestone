using System.Globalization;

namespace Bluestone.Midi.Endpoints;

public enum EndpointDirection
{
    /// <summary>Bluestone sends to this endpoint.</summary>
    Output,

    /// <summary>Bluestone receives from this endpoint.</summary>
    Input,
}

public enum EndpointTransport
{
    Unknown,
    Physical,
    Virtual,
    Network,
    Software,
    Test,
}

[Flags]
public enum EndpointCapabilities
{
    None = 0,

    /// <summary>The endpoint accepts future timestamps and delivers on time without Bluestone waiting.</summary>
    ScheduledDelivery = 1,

    /// <summary>System exclusive messages are passed through.</summary>
    SystemExclusive = 2,
}

/// <summary>
/// An endpoint's identity within one provider, stable for as long as the operating system keeps it
/// stable (for example a CoreMIDI unique ID). Never an index into a port list.
/// </summary>
public readonly record struct EndpointId
{
    public const int MaxLength = 256;

    public EndpointId(string provider, string value)
    {
        Provider = Validate(provider, nameof(provider));
        Value = Validate(value, nameof(value));
    }

    /// <summary>The <see cref="IMidiEndpointProvider.Id"/> of the provider that owns the endpoint.</summary>
    public string Provider { get; }

    /// <summary>The provider's opaque stable identifier.</summary>
    public string Value { get; }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Provider}:{Value}");

    private static string Validate(string text, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text, paramName);
        return text.Length <= MaxLength ? text : throw new ArgumentException($"Must be at most {MaxLength} characters.", paramName);
    }
}

/// <summary>
/// What a provider knows about an endpoint. Display and identity hints (name, manufacturer, model)
/// help find an endpoint again after its ID changes, but they never identify a device profile.
/// </summary>
public sealed record EndpointDescriptor(
    EndpointId Id,
    string DisplayName,
    EndpointDirection Direction,
    EndpointTransport Transport,
    EndpointCapabilities Capabilities,
    string? Manufacturer = null,
    string? Model = null);

public enum EndpointState
{
    Open,

    /// <summary>The endpoint went away (unplugged, renamed out of existence, session ended).</summary>
    Disconnected,

    /// <summary>The adapter reported an unrecoverable error.</summary>
    Faulted,

    /// <summary>The handle was disposed.</summary>
    Closed,
}

public sealed class EndpointStateChangedEventArgs(EndpointState state, string? reason) : EventArgs
{
    public EndpointState State { get; } = state;

    public string? Reason { get; } = reason;
}

/// <summary>An endpoint could not be opened: it does not exist, faces the wrong way, or the OS refused.</summary>
public sealed class EndpointUnavailableException : Exception
{
    public EndpointUnavailableException()
    {
    }

    public EndpointUnavailableException(string message)
        : base(message)
    {
    }

    public EndpointUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public EndpointUnavailableException(EndpointId id, string reason)
        : base($"Endpoint {id} is unavailable: {reason}") => EndpointId = id;

    public EndpointId? EndpointId { get; }
}
