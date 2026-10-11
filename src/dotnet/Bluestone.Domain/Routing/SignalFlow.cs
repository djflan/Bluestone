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
    /// The presence after <paramref name="device"/>. A bypassed device changes nothing. A device takes
    /// out the event classes it handles only when it does not send events on (an instrument), and an
    /// effect that sends events on leaves what was there. Audio appears after anything that makes it.
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
            events &= ~definition.Handles;
        }

        var audio = Audio || (definition.Produces.HasFlag(SignalKinds.Audio) && !definition.Consumes.HasFlag(SignalKinds.Audio));
        return this with { Events = events, Audio = audio };
    }
}

/// <summary>Finds what signal a connection's source carries.</summary>
public static class SignalFlow
{
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
