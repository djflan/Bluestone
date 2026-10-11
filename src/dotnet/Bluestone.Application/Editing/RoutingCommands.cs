using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Application.Editing;

/// <summary>
/// Editing the routing model: connections, external instruments, and mixer channels (ADR 0023). The
/// device strip's routing indicators and the routing inspector both use these, so they edit one list.
/// A connection that would not work, or would close a loop, is refused with <see cref="CommandRefusedException"/>.
/// </summary>
public static class RoutingCommands
{
    public static IProjectCommand Connect(SignalConnection connection, DeviceDefinitionLookup definitions)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return RoutingGuard.Command("Connect", definitions, p => p with { Connections = p.Connections.Add(connection) });
    }

    /// <summary>Replaces the connection with the same ID: a new destination, channel mapping, or voice.</summary>
    public static IProjectCommand UpdateConnection(SignalConnection connection, DeviceDefinitionLookup definitions)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return RoutingGuard.Command("Change Connection", definitions, p =>
        {
            var index = p.Connections.Select(c => c.Id).ToList().IndexOf(connection.Id);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Connection {connection.Id} is not in the project.");
            }

            return Equals(p.Connections[index], connection) ? p : p with { Connections = p.Connections.SetItem(index, connection) };
        });
    }

    public static IProjectCommand Disconnect(ConnectionId connection) =>
        new ProjectCommand("Disconnect", p => p.Connections.Any(c => c.Id == connection)
            ? p with { Connections = p.Connections.RemoveAll(c => c.Id == connection) }
            : p);

    public static IProjectCommand AddInstrument(ExternalInstrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        return new ProjectCommand("Add Instrument", p => p with { Instruments = p.Instruments.Add(instrument) });
    }

    /// <summary>Replaces the instrument with the same ID: its name, profile, operating mode, or ports.</summary>
    public static IProjectCommand UpdateInstrument(ExternalInstrument instrument, DeviceDefinitionLookup definitions)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        return RoutingGuard.Command("Change Instrument", definitions, p =>
        {
            var existing = p.FindInstrument(instrument.Id) ?? throw new KeyNotFoundException($"Instrument {instrument.Id} is not in the project.");
            return Equals(existing, instrument) ? p : p with { Instruments = p.Instruments.Replace(existing, instrument) };
        });
    }

    /// <summary>Removes an instrument that nothing is connected to. With connections, it is refused, so no track loses its output unnoticed.</summary>
    public static IProjectCommand RemoveInstrument(ExternalInstrumentId instrument) =>
        new ProjectCommand("Remove Instrument", p =>
        {
            if (p.FindInstrument(instrument) is not { } existing)
            {
                return p;
            }

            var users = p.Connections.Count(c => c.Destination.Kind == SignalNodeKind.ExternalInstrument && c.Destination.AsInstrument() == instrument);
            return users > 0
                ? throw new CommandRefusedException($"{users} connection{(users == 1 ? " sends" : "s send")} to {existing.Name}. Disconnect {(users == 1 ? "it" : "them")} first.")
                : p with { Instruments = p.Instruments.Remove(existing) };
        });

    public static IProjectCommand AddMixerChannel(MixerChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new ProjectCommand("Add Mixer Channel", p => p with { Mixer = p.Mixer.With(channel) });
    }

    /// <summary>
    /// Replaces the mixer channel with the same ID: name, gain, pan, mute, solo, or output (checked for loops).
    /// Edits with the same <paramref name="mergeKey"/> in quick succession (a fader drag) are one undo step.
    /// </summary>
    public static IProjectCommand UpdateMixerChannel(MixerChannel channel, DeviceDefinitionLookup definitions, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return RoutingGuard.Command("Change Mixer Channel", definitions, p =>
            Equals(p.Mixer.Find(channel.Id), channel) ? p : p with { Mixer = p.Mixer.With(channel) }, mergeKey);
    }

    /// <summary>Sets the master gain, in decibels.</summary>
    public static IProjectCommand SetMasterGain(double decibels) =>
        new ProjectCommand("Change Master Gain", p => p.Mixer.MasterGainDecibels == decibels ? p : p with { Mixer = p.Mixer with { MasterGainDecibels = decibels } })
        {
            MergeKey = "master-gain",
        };

    /// <summary>Adds a send from <paramref name="channel"/> (or replaces the one to the same target), refused when it would close a loop.</summary>
    public static IProjectCommand AddMixerSend(MixerChannelId channel, MixerSend send, DeviceDefinitionLookup definitions) =>
        EditSend("Add Send", channel, send, definitions, null);

    /// <summary>
    /// Changes a send's level or tap point (pre or post fader). Edits with the same <paramref name="mergeKey"/> in quick
    /// succession (a knob drag) are one undo step; the views use <c>send:{channel}:{target}</c>.
    /// </summary>
    public static IProjectCommand UpdateMixerSend(MixerChannelId channel, MixerSend send, DeviceDefinitionLookup definitions, string? mergeKey = null) =>
        EditSend("Change Send", channel, send, definitions, mergeKey);

    public static IProjectCommand RemoveMixerSend(MixerChannelId channel, MixerChannelId target) =>
        new ProjectCommand("Remove Send", p =>
            p.Mixer.Find(channel) is { } found && found.FindSend(target) is not null ? p with { Mixer = p.Mixer.With(found.WithoutSend(target)) } : p);

    private static IProjectCommand EditSend(string label, MixerChannelId channel, MixerSend send, DeviceDefinitionLookup definitions, string? mergeKey)
    {
        ArgumentNullException.ThrowIfNull(send);
        return RoutingGuard.Command(label, definitions, p =>
        {
            var found = p.Mixer.Find(channel) ?? throw new KeyNotFoundException($"Mixer channel {channel} is not in the project.");
            if (send.Target == channel)
            {
                throw new CommandRefusedException($"\"{found.Name}\" cannot send to itself.");
            }

            if (p.Mixer.Find(send.Target) is null)
            {
                throw new KeyNotFoundException($"Mixer channel {send.Target} is not in the project.");
            }

            return Equals(found.FindSend(send.Target), send) ? p : p with { Mixer = p.Mixer.With(found.WithSend(send)) };
        }, mergeKey);
    }

    /// <summary>Removes a mixer channel, the connections into it, and the sends to it; channels that fed it go to the master.</summary>
    public static IProjectCommand RemoveMixerChannel(MixerChannelId channel) =>
        new ProjectCommand("Remove Mixer Channel", p =>
        {
            if (p.Mixer.Find(channel) is null)
            {
                return p;
            }

            var node = SignalNode.Mixer(channel);
            return p with { Mixer = p.Mixer.Without(channel), Connections = p.Connections.RemoveAll(c => c.Destination == node) };
        });
}

/// <summary>Changing a track's role and adding content that may change it, never discarding anything (ADR 0021).</summary>
public static class TrackRoleCommands
{
    /// <summary>
    /// Sets a track's role. A change that needs consent (<see cref="RoleChangeOutcome.NeedsConfirmation"/>) is
    /// applied only when <paramref name="confirmed"/>; a refused one never is. Either way the refusal carries
    /// the plan's message. Use <see cref="TrackRoleConversion.ForRoleChange"/> first to ask the user.
    /// </summary>
    public static IProjectCommand SetRole(TrackId track, TrackRole role, DeviceDefinitionLookup definitions, bool confirmed = false) =>
        RoutingGuard.Command("Change Track Role", definitions, p => ApplyPlan(p, TrackRoleConversion.ForRoleChange(p, track, role, definitions), confirmed));

    /// <summary>
    /// Adds an audio clip, converting the track's role when its content requires it (an instrument track
    /// becomes hybrid). A conversion that needs consent is applied only when <paramref name="confirmed"/>.
    /// </summary>
    public static IProjectCommand AddAudioClip(TrackId track, AudioClip clip, DeviceDefinitionLookup definitions, bool confirmed = false)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return RoutingGuard.Command("Add Audio Clip", definitions, p =>
        {
            var converted = ApplyPlan(p, TrackRoleConversion.ForAddedClip(p, track, ClipContent.Audio, definitions), confirmed);
            var target = converted.Sequence.FindTrack(track)!;
            return converted with { Sequence = converted.Sequence.WithTrack(target.PlaceClip(clip)) };
        });
    }

    private static Project ApplyPlan(Project project, RoleChangePlan plan, bool confirmed) => plan.Outcome switch
    {
        RoleChangeOutcome.Refused => throw new CommandRefusedException(plan.Message),
        RoleChangeOutcome.NeedsConfirmation when !confirmed => throw new CommandRefusedException(plan.Message),
        _ => plan.Apply(project),
    };
}
