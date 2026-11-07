using Bluestone.Domain.Devices;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Domain.Routing;

/// <summary>
/// What may be present in a signal at one point of a chain, worked out from the devices before it. It is
/// an over-approximation: it says what <em>could</em> be there, so "nothing could be there" is a reliable
/// reason to refuse a connection, and a device that is not installed makes the answer
/// <see cref="Uncertain"/> instead of wrong.
/// </summary>
/// <param name="Events">Event classes that may be present.</param>
/// <param name="Audio">Whether audio may be present.</param>
/// <param name="Uncertain">A device on the way is unknown, so anything could be present.</param>
public readonly record struct SignalPresence(EventClass Events, bool Audio, bool Uncertain = false)
{
    /// <summary>What a track of this role starts with. Effect and group tracks take whatever is routed into them.</summary>
    public static SignalPresence ForRole(TrackRole role) => role switch
    {
        TrackRole.Instrument => new(EventClass.Transmitted, false),
        TrackRole.Audio => new(EventClass.None, true),
        _ => new(EventClass.Transmitted, true),
    };

    /// <summary>What a free-standing rack may receive: anything.</summary>
    public static SignalPresence Anything => new(EventClass.Transmitted, true);

    public bool Carries(SignalKind kind) => Uncertain || (kind == SignalKind.Events ? Events != EventClass.None : Audio);

    /// <summary>
    /// The presence after <paramref name="device"/>. A bypassed device changes nothing. A device that does
    /// not send events on (an instrument) takes out only the handled classes it does not pass through
    /// (<see cref="DeviceDefinition.PassesThrough"/>, as <c>ChainRunner</c> does; ADR 0037), and an effect
    /// that sends events on leaves what was there. Audio appears after anything that makes it, and a device with an
    /// input (a receiver) may add what that input takes.
    /// </summary>
    public SignalPresence Through(DeviceInstance device, DeviceDefinitionLookup definitions)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(definitions);
        if (device.IsBypassed)
        {
            return this;
        }

        if (definitions(device.Definition.Id) is not { } definition)
        {
            return this with { Uncertain = true };
        }

        var events = Events;
        if (definition.Consumes.HasFlag(SignalKinds.Events) && !definition.Produces.HasFlag(SignalKinds.Events))
        {
            events &= ~(definition.Handles & ~definition.PassesThrough);
        }

        var audio = Audio || (definition.Produces.HasFlag(SignalKinds.Audio) && !definition.Consumes.HasFlag(SignalKinds.Audio));

        // A device with an input (a receiver) may bring in whatever that input takes.
        foreach (var input in definition.Inputs)
        {
            if (input.Kind == SignalKind.Events && definition.Produces.HasFlag(SignalKinds.Events))
            {
                events |= EventClass.Transmitted & definition.Handles;
            }
            else if (input.Kind == SignalKind.Audio && definition.Produces.HasFlag(SignalKinds.Audio))
            {
                audio = true;
            }
        }

        return this with { Events = events, Audio = audio };
    }
}

/// <summary>Finds what signal a connection's source carries, and where in a chain a point is.</summary>
public static class SignalFlow
{
    /// <summary>
    /// The chain <paramref name="node"/> belongs to and its junction there (ADR 0037): 0 is the chain's input, i + 1
    /// the output of device i, and the device count its output. For a device input port, the junction is the one
    /// arriving at the device (its index). Null for a track without a chain, and for nodes not in a chain.
    /// </summary>
    public static (DeviceChain Chain, int Junction)? JunctionOf(Project project, SignalNode node)
    {
        ArgumentNullException.ThrowIfNull(project);
        switch (node.Kind)
        {
            case SignalNodeKind.Track when project.ChainOf(node.AsTrack()) is { } chain:
                return (chain, chain.Devices.Length);
            case SignalNodeKind.Rack when project.FindChain(node.AsRack()) is { } rack:
                return (rack, rack.Devices.Length);
            case SignalNodeKind.ChainInput when project.FindChain(node.AsChain()) is { } chain:
                return (chain, 0);
            case SignalNodeKind.Device when project.FindDevice(node.AsDevice()) is var (chain, _):
                return (chain, chain.IndexOf(node.AsDevice()) + 1);
            case SignalNodeKind.DeviceInput when project.FindDevice(node.AsDevice()) is var (chain, _):
                return (chain, chain.IndexOf(node.AsDevice()));
            default:
                return null;
        }
    }

    /// <summary>The presence at a source: a chain's input or output, or the point after one of its devices.</summary>
    public static SignalPresence PresenceOf(Project project, SignalNode source, DeviceDefinitionLookup definitions)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (JunctionOf(project, source) is not var (chain, junction))
        {
            return source.Kind == SignalNodeKind.Track ? StartOf(project, ChainOwner.ForTrack(source.AsTrack())) : SignalPresence.Anything;
        }

        var presence = StartOf(project, chain.Owner);
        for (var i = 0; i < junction; i++)
        {
            presence = presence.Through(chain.Devices[i], definitions);
        }

        return presence;
    }
    /// <summary>
    /// The presence at the end of <paramref name="chain"/>, or after the device <paramref name="tap"/> when
    /// given (the output a tap connection reads).
    /// </summary>
    public static SignalPresence PresenceAt(Project project, DeviceChain chain, DeviceId? tap, DeviceDefinitionLookup definitions)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(chain);
        var presence = StartOf(project, chain.Owner);
        foreach (var device in chain.Devices)
        {
            presence = presence.Through(device, definitions);
            if (tap == device.Id)
            {
                break;
            }
        }

        return presence;
    }

    /// <summary>What a track's output carries when it has no devices: the content its role holds.</summary>
    public static SignalPresence StartOf(Project project, ChainOwner owner)
    {
        ArgumentNullException.ThrowIfNull(project);
        return owner.Kind == ChainOwnerKind.Track && project.Sequence.FindTrack(owner.Track) is { } track
            ? SignalPresence.ForRole(track.Role)
            : SignalPresence.Anything;
    }
}
