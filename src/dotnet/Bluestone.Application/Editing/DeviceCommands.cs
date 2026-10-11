using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Application.Editing;

/// <summary>
/// Editing device chains and racks (ADR 0022). Devices keep their identity through every edit, so
/// automation and routing taps that name them stay attached. Edits that would break routing are refused
/// with <see cref="CommandRefusedException"/>.
/// </summary>
public static class DeviceCommands
{
    /// <summary>
    /// Inserts <paramref name="device"/> into <paramref name="track"/>'s chain at <paramref name="index"/>
    /// (null for the end), creating the chain when the track has none.
    /// </summary>
    public static IProjectCommand InsertDevice(TrackId track, DeviceInstance device, DeviceDefinitionLookup definitions, int? index = null) =>
        RoutingGuard.Command("Add Device", definitions, p =>
        {
            ArgumentNullException.ThrowIfNull(device);
            var chain = p.ChainOf(track) ?? DeviceChain.Create(ChainOwner.ForTrack(track));
            return p.WithChain(chain.Insert(index ?? chain.Devices.Length, device));
        });

    /// <summary>Inserts <paramref name="device"/> into the chain <paramref name="chain"/> (a track's or a rack's).</summary>
    public static IProjectCommand InsertDevice(DeviceChainId chain, DeviceInstance device, DeviceDefinitionLookup definitions, int? index = null) =>
        RoutingGuard.Command("Add Device", definitions, p =>
        {
            ArgumentNullException.ThrowIfNull(device);
            var found = p.FindChain(chain) ?? throw new KeyNotFoundException($"Chain {chain} is not in the project.");
            return p.WithChain(found.Insert(index ?? found.Devices.Length, device));
        });

    /// <summary>
    /// Removes a device, and the connections that tap its output (they have nothing left to tap). Automation
    /// lanes that target it are kept and reported, so nothing the musician drew is lost.
    /// </summary>
    public static IProjectCommand RemoveDevice(DeviceId device, DeviceDefinitionLookup definitions) =>
        RoutingGuard.Command("Remove Device", definitions, p =>
        {
            if (p.FindDevice(device) is not { } found)
            {
                return p;
            }

            var tap = SignalNode.Device(device);
            return p.WithChain(found.Chain.Remove(device)) with { Connections = p.Connections.RemoveAll(c => c.Source == tap) };
        });

    /// <summary>Moves a device within its chain. Its identity, parameters, automation, and taps are unchanged.</summary>
    public static IProjectCommand MoveDevice(DeviceId device, int newIndex, DeviceDefinitionLookup definitions) =>
        RoutingGuard.Command("Move Device", definitions, p =>
            p.FindDevice(device) is { } found ? p.WithChain(found.Chain.Move(device, newIndex)) : p);

    public static IProjectCommand SetBypassed(DeviceId device, bool bypassed, DeviceDefinitionLookup definitions) =>
        RoutingGuard.Command(bypassed ? "Bypass Device" : "Enable Device", definitions, p =>
            p.FindDevice(device) is { } found && found.Device.IsBypassed != bypassed
                ? p.WithChain(found.Chain.Replace(found.Device with { IsBypassed = bypassed }))
                : p);

    public static IProjectCommand RenameDevice(DeviceId device, string name) =>
        new ProjectCommand("Rename Device", p =>
            p.FindDevice(device) is { } found && found.Device.Name != name
                ? p.WithChain(found.Chain.Replace(found.Device with { Name = name }))
                : p);

    /// <summary>Sets a stored parameter value (normalized 0 to 1, see <see cref="ControlValue.FromFraction"/>).</summary>
    /// <remarks>Repeated changes of one parameter in quick succession (a slider drag) are one undo step.</remarks>
    public static IProjectCommand SetParameter(DeviceId device, ParameterId parameter, ControlValue value) =>
        new ProjectCommand("Change Parameter", p =>
            p.FindDevice(device) is { } found && found.Device.ValueOf(parameter) != value
                ? p.WithChain(found.Chain.Replace(found.Device.WithParameter(parameter, value)))
                : p)
        {
            MergeKey = $"parameter:{device}:{parameter}",
        };

    /// <summary>Renames a rack (or a track's chain, which is shown under its track's name).</summary>
    public static IProjectCommand RenameChain(DeviceChainId chain, string name) =>
        new ProjectCommand("Rename Rack", p =>
            p.FindChain(chain) is { } found && found.Name != name ? p.WithChain(found with { Name = name }) : p);

    /// <summary>Inserts a preset's devices into the chain <paramref name="chain"/> (a track's or a rack's), as new devices.</summary>
    public static IProjectCommand LoadPreset(DeviceChainId chain, DeviceChainPreset preset, DeviceDefinitionLookup definitions, int? index = null) =>
        RoutingGuard.Command("Load Chain Preset", definitions, p =>
        {
            ArgumentNullException.ThrowIfNull(preset);
            var found = p.FindChain(chain) ?? throw new KeyNotFoundException($"Chain {chain} is not in the project.");
            var at = index ?? found.Devices.Length;
            foreach (var device in preset.Devices.Select(d => d.Instantiate()).Reverse())
            {
                found = found.Insert(at, device);
            }

            return p.WithChain(found);
        });

    /// <summary>Stores a plugin's state as last captured from its worker, so a restart or reopen can restore it (ADR 0025).</summary>
    public static IProjectCommand SetPluginState(DeviceId device, PluginState state) =>
        new ProjectCommand("Store Plugin State", p =>
            p.FindDevice(device) is { } found && !Equals(found.Device.State, state)
                ? p.WithChain(found.Chain.Replace(found.Device with { State = state }))
                : p);

    /// <summary>
    /// Inserts a preset's devices into <paramref name="track"/>'s chain at <paramref name="index"/> (null for
    /// the end). Every device is a new instance: loading the same preset twice shares nothing.
    /// </summary>
    public static IProjectCommand LoadPreset(TrackId track, DeviceChainPreset preset, DeviceDefinitionLookup definitions, int? index = null) =>
        RoutingGuard.Command("Load Chain Preset", definitions, p =>
        {
            ArgumentNullException.ThrowIfNull(preset);
            var chain = p.ChainOf(track) ?? DeviceChain.Create(ChainOwner.ForTrack(track));
            var at = index ?? chain.Devices.Length;
            foreach (var device in preset.Devices.Select(d => d.Instantiate()).Reverse())
            {
                chain = chain.Insert(at, device);
            }

            return p.WithChain(chain);
        });

    /// <summary>A preset of <paramref name="chain"/>: its devices as templates, without identities or connections.</summary>
    public static DeviceChainPreset SavePreset(Project project, DeviceChainId chain, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        var found = project.FindChain(chain) ?? throw new KeyNotFoundException($"Chain {chain} is not in the project.");
        return DeviceChainPreset.From(found, name);
    }

    /// <summary>Adds a free-standing rack (a shared instrument or processing chain) that tracks can route into.</summary>
    public static IProjectCommand AddRack(DeviceChain rack)
    {
        ArgumentNullException.ThrowIfNull(rack);
        return new ProjectCommand("Add Rack", p => rack.Owner.Kind != ChainOwnerKind.Rack
            ? throw new CommandRefusedException("Only a free-standing rack can be added this way; a track's chain belongs to its track.")
            : p.WithChain(rack));
    }

    /// <summary>Removes a rack with every connection to or from it or its devices.</summary>
    public static IProjectCommand RemoveRack(DeviceChainId rack) =>
        new ProjectCommand("Remove Rack", p =>
        {
            if (p.FindChain(rack) is not { Owner.Kind: ChainOwnerKind.Rack } found)
            {
                return p;
            }

            var node = SignalNode.Rack(rack);
            return p.WithoutChain(rack) with
            {
                Connections = p.Connections.RemoveAll(c => c.Source == node || c.Destination == node
                    || (c.Source.Kind == SignalNodeKind.Device && found.Find(c.Source.AsDevice()) is not null)),
            };
        });

    /// <summary>
    /// Moves a whole chain to another owner, keeping its identity and its devices' identities (unlike loading
    /// a preset, which creates new ones). A track can host only one chain, so the target must have none.
    /// </summary>
    public static IProjectCommand MoveChain(DeviceChainId chain, ChainOwner owner, DeviceDefinitionLookup definitions) =>
        RoutingGuard.Command("Move Chain", definitions, p =>
        {
            var found = p.FindChain(chain) ?? throw new KeyNotFoundException($"Chain {chain} is not in the project.");
            if (found.Owner == owner)
            {
                return p;
            }

            if (owner.Kind == ChainOwnerKind.Track && p.ChainOf(owner.Track) is not null)
            {
                throw new CommandRefusedException("That track already has devices. Remove them, or load the chain as a preset instead.");
            }

            return p.WithChain(found with { Owner = owner });
        });
}
