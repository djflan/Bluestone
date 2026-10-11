using System.Collections.Immutable;

namespace Bluestone.Domain.Devices;

/// <summary>
/// A device as a template: everything that configures it and nothing that identifies it. It has no ID,
/// so there is no way to load a preset and end up sharing a device (or its automation) with the chain
/// it was saved from.
/// </summary>
public sealed record DevicePreset
{
    public required DeviceReference Definition { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool IsBypassed { get; init; }

    public ImmutableArray<ParameterValue> Parameters { get; init; } = [];

    public PluginState? State { get; init; }

    /// <summary>The configuration of <paramref name="device"/>, without its identity.</summary>
    public static DevicePreset From(DeviceInstance device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new DevicePreset
        {
            Definition = device.Definition,
            Name = device.Name,
            IsBypassed = device.IsBypassed,
            Parameters = device.Parameters,
            State = device.State,
        };
    }

    /// <summary>A new device with a new identity and this configuration.</summary>
    public DeviceInstance Instantiate() => new()
    {
        Id = DeviceId.New(),
        Definition = Definition,
        Name = Name,
        IsBypassed = IsBypassed,
        Parameters = Parameters,
        State = State,
    };
}

/// <summary>
/// A saved chain: devices in order, as templates. Loading it creates independent device instances
/// with new identities (ADR 0022); moving an existing chain is a different operation and keeps them.
/// </summary>
/// <remarks>
/// Only the sequential device list is a preset. Routing connections, including taps from a device to
/// other tracks or to hardware, belong to a project's destinations and are not part of it, so a preset
/// carries no reference to hardware and means the same on every machine.
/// </remarks>
public sealed record DeviceChainPreset
{
    public const int MaxNameLength = 256;

    private readonly string _name = string.Empty;

    public string Name
    {
        get => _name;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _name = value.Length <= MaxNameLength ? value : throw new ArgumentException($"Preset names are limited to {MaxNameLength} characters.", nameof(Name));
        }
    }

    public ImmutableArray<DevicePreset> Devices { get; init; } = [];

    public static DeviceChainPreset From(DeviceChain chain, string name)
    {
        ArgumentNullException.ThrowIfNull(chain);
        return new DeviceChainPreset { Name = name, Devices = [.. chain.Devices.Select(DevicePreset.From)] };
    }

    /// <summary>A new chain with a new identity, holding new instances of every device.</summary>
    public DeviceChain Instantiate(ChainOwner owner, string name = "") =>
        DeviceChain.Create(owner, name) with { Devices = [.. Devices.Select(d => d.Instantiate())] };
}
