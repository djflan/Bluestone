using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Domain.Projects;

/// <summary>Stable identity of a project.</summary>
public readonly record struct ProjectId(Guid Value)
{
    public static ProjectId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>A half-open range of ticks, such as a loop.</summary>
public sealed record TickRange
{
    public TickRange(Tick start, Tick end)
    {
        if (end <= start)
        {
            throw new ArgumentException("A range must end after it starts.", nameof(end));
        }

        Start = start;
        End = end;
    }

    public Tick Start { get; }

    public Tick End { get; }

    public TickSpan Length => End - Start;
}

/// <summary>
/// Everything a musician saves: the sequence (arrangement), the device chains that process it, the
/// external instruments and mixer channels it can reach, the connections between them, and transport
/// settings. One model: the device strip, the routing inspector, the mixer, and the playback plan are
/// all read from it. Things the machine may lack (an endpoint, a profile, a plugin) are kept as
/// references and never dropped. Immutable.
/// </summary>
public sealed record Project
{
    public const int MaxNameLength = 256;

    private readonly string _name = "Untitled";
    private readonly Sequence _sequence = Sequence.CreateEmpty(Ppqn.Default);
    private readonly ImmutableArray<ExternalInstrument> _instruments = [];
    private readonly ImmutableArray<DeviceChain> _chains = [];
    private readonly ImmutableArray<SignalConnection> _connections = [];
    private readonly ImmutableArray<SignalConnection> _parked = [];
    private readonly Mixer _mixer = Mixer.Empty;
    private readonly ArrangerLoop _loop = ArrangerLoop.None;

    public required ProjectId Id { get; init; }

    public string Name
    {
        get => _name;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _name = value.Length <= MaxNameLength ? value : throw new ArgumentException($"Project names are limited to {MaxNameLength} characters.", nameof(Name));
        }
    }

    public Sequence Sequence
    {
        get => _sequence;
        init => _sequence = value ?? throw new ArgumentNullException(nameof(Sequence));
    }

    /// <summary>Hardware and hardware-like MIDI instruments, independent of any track (ADR 0023).</summary>
    public ImmutableArray<ExternalInstrument> Instruments
    {
        get => _instruments;
        init => _instruments = UniqueBy(value, i => i.Id, nameof(value), "External instrument");
    }

    /// <summary>
    /// Device chains: each track's device strip and any free-standing racks. A track has at most one
    /// chain, and every chain has exactly one owner.
    /// </summary>
    public ImmutableArray<DeviceChain> Chains
    {
        get => _chains;
        init => _chains = ValidateChains(value);
    }

    /// <summary>The routing model: every connection of events or audio, from a source to a destination.</summary>
    public ImmutableArray<SignalConnection> Connections
    {
        get => _connections;
        init => _connections = UniqueBy(value, c => c.Id, nameof(value), "Connection");
    }

    /// <summary>
    /// A grouped track's own outputs, set aside when it joined its group and put back when it leaves (ADR 0030).
    /// They are not routing: nothing plays, validates, or shows them. Their IDs are not in <see cref="Connections"/>.
    /// </summary>
    public ImmutableArray<SignalConnection> ParkedConnections
    {
        get => _parked;
        init => _parked = UniqueBy(value, c => c.Id, nameof(value), "Parked connection");
    }

    /// <summary>Mixer channels, which are audio paths and are independent of tracks.</summary>
    public Mixer Mixer
    {
        get => _mixer;
        init => _mixer = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The arranger loop: its range, kept while looping is off, and whether playback loops.</summary>
    public ArrangerLoop Loop
    {
        get => _loop;
        init => _loop = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The chain a track hosts, or null if it has none yet.</summary>
    public DeviceChain? ChainOf(TrackId track)
    {
        foreach (var chain in _chains)
        {
            if (chain.Owner.Kind == ChainOwnerKind.Track && chain.Owner.Track == track)
            {
                return chain;
            }
        }

        return null;
    }

    public DeviceChain? FindChain(DeviceChainId id)
    {
        foreach (var chain in _chains)
        {
            if (chain.Id == id)
            {
                return chain;
            }
        }

        return null;
    }

    public ExternalInstrument? FindInstrument(ExternalInstrumentId id)
    {
        foreach (var instrument in _instruments)
        {
            if (instrument.Id == id)
            {
                return instrument;
            }
        }

        return null;
    }

    /// <summary>The chain holding <paramref name="id"/> and the device, wherever the device has been moved to.</summary>
    public (DeviceChain Chain, DeviceInstance Device)? FindDevice(DeviceId id)
    {
        foreach (var chain in _chains)
        {
            if (chain.Find(id) is { } device)
            {
                return (chain, device);
            }
        }

        return null;
    }

    /// <summary>Replaces the chain with the same ID, or adds it.</summary>
    /// <exception cref="ArgumentException">The chain's owner already has another chain, or a device is already in another chain.</exception>
    public Project WithChain(DeviceChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var index = -1;
        for (var i = 0; i < _chains.Length; i++)
        {
            if (_chains[i].Id == chain.Id)
            {
                index = i;
                break;
            }
        }

        return this with { Chains = index < 0 ? _chains.Add(chain) : _chains.SetItem(index, chain) };
    }

    public Project WithoutChain(DeviceChainId id) => this with { Chains = _chains.RemoveAll(c => c.Id == id) };

    /// <summary>
    /// Removes a track with everything it owns: its chain, every connection from or to it or from one of its
    /// devices, and parked connections from or to it. Instruments, racks, mixer channels, and other tracks'
    /// automation stay.
    /// </summary>
    public Project WithoutTrack(TrackId track)
    {
        var node = SignalNode.Track(track);
        var chain = ChainOf(track);
        return this with
        {
            Sequence = _sequence.WithoutTrack(track),
            Chains = chain is null ? _chains : _chains.Remove(chain),
            Connections = _connections.RemoveAll(c =>
                c.Source == node
                || c.Destination == node
                || (c.Source.Kind == SignalNodeKind.Device && chain?.Find(c.Source.AsDevice()) is not null)),
            ParkedConnections = _parked.IsEmpty ? _parked : _parked.RemoveAll(c => c.Source == node || c.Destination == node),
        };
    }

    public IEnumerable<SignalConnection> ConnectionsFrom(SignalNode source) => _connections.Where(c => c.Source == source);

    public IEnumerable<SignalConnection> ConnectionsTo(SignalNode destination) => _connections.Where(c => c.Destination == destination);

    public static Project CreateNew(string name = "Untitled") => new() { Id = ProjectId.New(), Name = name };

    private static ImmutableArray<T> UniqueBy<T, TKey>(ImmutableArray<T> items, Func<T, TKey> key, string paramName, string what)
        where TKey : notnull
    {
        if (items.IsDefault)
        {
            return [];
        }

        var seen = new HashSet<TKey>();
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item, paramName);
            if (!seen.Add(key(item)))
            {
                throw new ArgumentException($"{what} {key(item)} appears more than once.", paramName);
            }
        }

        return items;
    }

    private static ImmutableArray<DeviceChain> ValidateChains(ImmutableArray<DeviceChain> chains)
    {
        var unique = UniqueBy(chains, c => c.Id, nameof(chains), "Chain");
        var owners = new HashSet<TrackId>();
        var devices = new HashSet<DeviceId>();
        foreach (var chain in unique)
        {
            if (chain.Owner.Kind == ChainOwnerKind.Track && !owners.Add(chain.Owner.Track))
            {
                throw new ArgumentException($"Track {chain.Owner.Track} has more than one chain.", nameof(chains));
            }

            foreach (var device in chain.Devices)
            {
                if (!devices.Add(device.Id))
                {
                    throw new ArgumentException($"Device {device.Id} is in more than one chain.", nameof(chains));
                }
            }
        }

        return unique;
    }
}
