using System.Collections.Immutable;

namespace Bluestone.Domain.Routing;

/// <summary>Stable identity of an external instrument within a project.</summary>
public readonly record struct ExternalInstrumentId(Guid Value)
{
    public static ExternalInstrumentId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// A logical port of an external instrument: the unit that carries sixteen channels. A multi-port module
/// such as a Yamaha MU2000 has several; each is carried by its own physical endpoint (or cable).
/// </summary>
public sealed record ExternalPort
{
    public const int MaxLength = 64;

    /// <param name="id">Stable within the instrument, such as "A". Connections name it.</param>
    /// <param name="name">What the musician calls it.</param>
    /// <param name="endpoint">The physical endpoint that carries it. It may be missing on this machine without affecting anything else.</param>
    public ExternalPort(string id, string name, EndpointReference? endpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(name);
        Id = id.Length <= MaxLength ? id : throw new ArgumentException($"Port IDs are at most {MaxLength} characters.", nameof(id));
        Name = name;
        Endpoint = endpoint;
    }

    public string Id { get; }

    public string Name { get; }

    public EndpointReference? Endpoint { get; }
}

/// <summary>
/// A hardware (or hardware-like) MIDI instrument as a project entity (ADR 0023). Tracks do not carry the
/// instrument's identity: they connect to one of its ports and channels, so a profile and an endpoint
/// are stated once however many tracks play it.
/// </summary>
/// <remarks>
/// These stay separate: the instrument's identity (this record), its physical endpoints (each port's
/// <see cref="ExternalPort.Endpoint"/>), its logical ports, its device profile (what it understands), the
/// standards it supports (a property of the profile), the mode it is currently in
/// (<see cref="OperatingMode"/>), and which part each track plays (the connection's channel and voice).
/// A device that supports both XG and GS is not thereby in both modes.
/// </remarks>
public sealed record ExternalInstrument
{
    public const int MaxNameLength = 256;
    public const int MaxPorts = 64;

    private readonly string _name = string.Empty;
    private readonly ImmutableArray<ExternalPort> _ports = [];

    public required ExternalInstrumentId Id { get; init; }

    public string Name
    {
        get => _name;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _name = value.Length <= MaxNameLength ? value : throw new ArgumentException($"Instrument names are limited to {MaxNameLength} characters.", nameof(Name));
        }
    }

    /// <summary>What the instrument understands. Missing profiles never block playback (ADR 0008).</summary>
    public ProfileReference? Profile { get; init; }

    /// <summary>The ports, at least one.</summary>
    /// <exception cref="ArgumentException">There are none, too many, or two share an ID.</exception>
    public ImmutableArray<ExternalPort> Ports
    {
        get => _ports;
        init => _ports = ValidatePorts(value);
    }

    /// <summary>
    /// The mode the instrument is set to now, as an ID from its profile's protocols ("xg", "gs", "gm1").
    /// Null when it is not recorded. Never inferred from what the instrument supports.
    /// </summary>
    public string? OperatingMode { get; init; }

    /// <summary>A single-port instrument with the usual port "A".</summary>
    public static ExternalInstrument Create(string name, EndpointReference? endpoint = null, ProfileReference? profile = null) => new()
    {
        Id = ExternalInstrumentId.New(),
        Name = name,
        Profile = profile,
        Ports = [new ExternalPort(DefaultPortId, "Port A", endpoint)],
    };

    public const string DefaultPortId = "A";

    /// <summary>The port with <paramref name="id"/>; the first port when <paramref name="id"/> is null.</summary>
    public ExternalPort? FindPort(string? id)
    {
        if (id is null)
        {
            return _ports.IsEmpty ? null : _ports[0];
        }

        foreach (var port in _ports)
        {
            if (port.Id == id)
            {
                return port;
            }
        }

        return null;
    }

    public ExternalInstrument WithPort(ExternalPort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        var index = -1;
        for (var i = 0; i < _ports.Length; i++)
        {
            if (_ports[i].Id == port.Id)
            {
                index = i;
                break;
            }
        }

        return this with { Ports = index < 0 ? _ports.Add(port) : _ports.SetItem(index, port) };
    }

    private static ImmutableArray<ExternalPort> ValidatePorts(ImmutableArray<ExternalPort> ports)
    {
        if (ports.IsDefaultOrEmpty)
        {
            throw new ArgumentException("An instrument needs at least one port.", nameof(ports));
        }

        if (ports.Length > MaxPorts)
        {
            throw new ArgumentException($"An instrument has at most {MaxPorts} ports.", nameof(ports));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var port in ports)
        {
            ArgumentNullException.ThrowIfNull(port, nameof(ports));
            if (!ids.Add(port.Id))
            {
                throw new ArgumentException($"Port {port.Id} appears more than once.", nameof(ports));
            }
        }

        return ports;
    }
}
