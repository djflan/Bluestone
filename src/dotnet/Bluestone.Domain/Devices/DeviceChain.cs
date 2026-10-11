using System.Collections.Immutable;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Domain.Devices;

public enum ChainOwnerKind
{
    /// <summary>Hosted by a track, as its device strip.</summary>
    Track,

    /// <summary>Stands alone in the project: a shared instrument or processing rack that tracks route into.</summary>
    Rack,
}

/// <summary>
/// What hosts a chain. Every chain has exactly one owner. Ownership is separate from signal routing: any
/// number of tracks can route into a rack's chain, and the chain still has one owner.
/// </summary>
public readonly record struct ChainOwner
{
    private ChainOwner(ChainOwnerKind kind, TrackId track)
    {
        Kind = kind;
        Track = track;
    }

    public ChainOwnerKind Kind { get; }

    /// <summary>The hosting track, when <see cref="Kind"/> is <see cref="ChainOwnerKind.Track"/>.</summary>
    public TrackId Track { get; }

    public static ChainOwner ForTrack(TrackId track) => new(ChainOwnerKind.Track, track);

    public static ChainOwner Rack { get; } = new(ChainOwnerKind.Rack, default);

    public override string ToString() => Kind == ChainOwnerKind.Track ? $"track {Track}" : "rack";
}

/// <summary>
/// An ordered, independently identified list of devices. Signals pass through it first to last (ADR 0022).
/// Immutable: every edit returns a new chain, and devices keep their <see cref="DeviceId"/> through all of them.
/// </summary>
public sealed record DeviceChain
{
    public const int MaxDevices = 256;
    public const int MaxNameLength = 256;

    private readonly string _name = string.Empty;
    private readonly ImmutableArray<DeviceInstance> _devices = [];

    public required DeviceChainId Id { get; init; }

    public required ChainOwner Owner { get; init; }

    /// <summary>A name for a rack. Track chains are shown under their track's name, so this may stay empty.</summary>
    public string Name
    {
        get => _name;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _name = value.Length <= MaxNameLength ? value : throw new ArgumentException($"Chain names are limited to {MaxNameLength} characters.", nameof(Name));
        }
    }

    /// <summary>Devices in processing order.</summary>
    /// <exception cref="ArgumentException">A device appears twice, or there are too many.</exception>
    public ImmutableArray<DeviceInstance> Devices
    {
        get => _devices;
        init => _devices = Validate(value);
    }

    public static DeviceChain Create(ChainOwner owner, string name = "") => new() { Id = DeviceChainId.New(), Owner = owner, Name = name };

    public DeviceInstance? Find(DeviceId id)
    {
        var index = IndexOf(id);
        return index < 0 ? null : _devices[index];
    }

    public int IndexOf(DeviceId id)
    {
        for (var i = 0; i < _devices.Length; i++)
        {
            if (_devices[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Inserts <paramref name="device"/> at <paramref name="index"/> (0 to the device count).</summary>
    /// <exception cref="ArgumentException">The chain already holds a device with that ID.</exception>
    public DeviceChain Insert(int index, DeviceInstance device)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, _devices.Length);
        return this with { Devices = _devices.Insert(index, device) };
    }

    public DeviceChain Add(DeviceInstance device) => Insert(_devices.Length, device);

    /// <summary>Removes a device, or returns the same chain when it is not here.</summary>
    public DeviceChain Remove(DeviceId id)
    {
        var index = IndexOf(id);
        return index < 0 ? this : this with { Devices = _devices.RemoveAt(index) };
    }

    /// <summary>
    /// Moves a device so it ends up at <paramref name="newIndex"/> in the resulting order (0 to count - 1).
    /// The device keeps its identity, so automation and routing taps that name it stay attached.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The chain has no such device.</exception>
    public DeviceChain Move(DeviceId id, int newIndex)
    {
        var index = IndexOf(id);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Device {id} is not in this chain.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(newIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(newIndex, _devices.Length);
        if (index == newIndex)
        {
            return this;
        }

        var device = _devices[index];
        return this with { Devices = _devices.RemoveAt(index).Insert(newIndex, device) };
    }

    /// <summary>Replaces the device with the same ID, in place.</summary>
    /// <exception cref="KeyNotFoundException">The chain has no such device.</exception>
    public DeviceChain Replace(DeviceInstance device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var index = IndexOf(device.Id);
        return index < 0 ? throw new KeyNotFoundException($"Device {device.Id} is not in this chain.") : this with { Devices = _devices.SetItem(index, device) };
    }

    private static ImmutableArray<DeviceInstance> Validate(ImmutableArray<DeviceInstance> devices)
    {
        if (devices.IsDefault)
        {
            return [];
        }

        if (devices.Length > MaxDevices)
        {
            throw new ArgumentException($"A chain holds at most {MaxDevices} devices.", nameof(devices));
        }

        var ids = new HashSet<DeviceId>();
        foreach (var device in devices)
        {
            ArgumentNullException.ThrowIfNull(device, nameof(devices));
            if (!ids.Add(device.Id))
            {
                throw new ArgumentException($"Device {device.Id} appears more than once.", nameof(devices));
            }
        }

        return devices;
    }
}
