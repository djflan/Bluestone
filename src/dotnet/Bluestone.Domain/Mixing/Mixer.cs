using System.Collections.Immutable;

namespace Bluestone.Domain.Mixing;

/// <summary>Stable identity of a mixer channel. It is unrelated to any track's identity.</summary>
public readonly record struct MixerChannelId(Guid Value)
{
    public static MixerChannelId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// One audio path in the mixer: where audio from one or more sources is summed, leveled, and sent on.
/// A mixer channel is not a track. Twelve MIDI tracks playing one stereo instrument need one channel,
/// and a hardware-only MIDI track needs none (ADR 0023).
/// </summary>
public sealed record MixerChannel
{
    public const int MaxNameLength = 256;
    public const double MinGainDecibels = -120;
    public const double MaxGainDecibels = 24;
    public const int MaxSends = 64;

    private readonly string _name = string.Empty;
    private readonly ImmutableArray<MixerSend> _sends = [];
    private readonly double _gain;
    private readonly double _pan;

    public required MixerChannelId Id { get; init; }

    public string Name
    {
        get => _name;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _name = value.Length <= MaxNameLength ? value : throw new ArgumentException($"Channel names are limited to {MaxNameLength} characters.", nameof(Name));
        }
    }

    public double GainDecibels
    {
        get => _gain;
        init => _gain = value is >= MinGainDecibels and <= MaxGainDecibels ? value : throw new ArgumentOutOfRangeException(nameof(GainDecibels), value, "Gain must be between -120 and 24 dB.");
    }

    /// <summary>-1 is hard left, 0 is center, 1 is hard right.</summary>
    public double Pan
    {
        get => _pan;
        init => _pan = value is >= -1 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(Pan), value, "Pan must be between -1 and 1.");
    }

    public bool IsMuted { get; init; }

    public bool IsSoloed { get; init; }

    /// <summary>The channel this one feeds (a bus), or null for the master.</summary>
    public MixerChannelId? Output { get; init; }

    /// <summary>
    /// Copies of this channel's audio, each at its own level, to other channels used as returns or buses. At most one
    /// send per target, in display order.
    /// </summary>
    public ImmutableArray<MixerSend> Sends
    {
        get => _sends;
        init
        {
            var sends = value.IsDefault ? [] : value;
            if (sends.Length > MaxSends)
            {
                throw new ArgumentException($"A channel has at most {MaxSends} sends.", nameof(Sends));
            }

            var targets = new HashSet<MixerChannelId>();
            foreach (var send in sends)
            {
                ArgumentNullException.ThrowIfNull(send, nameof(Sends));
                if (!targets.Add(send.Target))
                {
                    throw new ArgumentException($"A channel sends to {send.Target} more than once.", nameof(Sends));
                }
            }

            _sends = sends;
        }
    }

    public static MixerChannel Create(string name) => new() { Id = MixerChannelId.New(), Name = name };

    public MixerSend? FindSend(MixerChannelId target)
    {
        foreach (var send in _sends)
        {
            if (send.Target == target)
            {
                return send;
            }
        }

        return null;
    }

    /// <summary>Replaces the send to the same target, or adds it at the end.</summary>
    public MixerChannel WithSend(MixerSend send)
    {
        ArgumentNullException.ThrowIfNull(send);
        for (var i = 0; i < _sends.Length; i++)
        {
            if (_sends[i].Target == send.Target)
            {
                return this with { Sends = _sends.SetItem(i, send) };
            }
        }

        return this with { Sends = _sends.Add(send) };
    }

    public MixerChannel WithoutSend(MixerChannelId target) =>
        FindSend(target) is null ? this : this with { Sends = _sends.RemoveAll(s => s.Target == target) };

    // Sends compare by content, so an unchanged channel read from a file or rebuilt by an edit equals the original.
    public bool Equals(MixerChannel? other) =>
        other is not null
        && Id == other.Id
        && _name == other._name
        && _gain.Equals(other._gain)
        && _pan.Equals(other._pan)
        && IsMuted == other.IsMuted
        && IsSoloed == other.IsSoloed
        && Output == other.Output
        && _sends.SequenceEqual(other._sends);

    public override int GetHashCode() => HashCode.Combine(Id, _name, _gain, _pan, IsMuted, IsSoloed, Output, _sends.Length);
}

/// <summary>
/// A send: a copy of a mixer channel's audio at <see cref="LevelDecibels"/> into another channel (a return or bus),
/// taken before the channel's fader and pan (<see cref="PreFader"/>) or after them. The target sums it with its other
/// inputs. A send never closes a loop (ADR 0032).
/// </summary>
public sealed record MixerSend
{
    private readonly double _level;

    public MixerSend(MixerChannelId target, double levelDecibels = 0, bool preFader = false)
    {
        Target = target;
        LevelDecibels = levelDecibels;
        PreFader = preFader;
    }

    public MixerChannelId Target { get; init; }

    /// <summary>The send's level, -120 (silence) to 24 dB.</summary>
    public double LevelDecibels
    {
        get => _level;
        init => _level = value is >= MixerChannel.MinGainDecibels and <= MixerChannel.MaxGainDecibels
            ? value
            : throw new ArgumentOutOfRangeException(nameof(LevelDecibels), value, "A send's level must be between -120 and 24 dB.");
    }

    /// <summary>Taken before the fader and pan; otherwise after both.</summary>
    public bool PreFader { get; init; }
}

/// <summary>The project's mixer: its channels, and the master bus they end in. Immutable.</summary>
public sealed record Mixer
{
    public const int MaxChannels = 1024;

    private readonly ImmutableArray<MixerChannel> _channels = [];
    private readonly double _master;

    public static Mixer Empty { get; } = new();

    public ImmutableArray<MixerChannel> Channels
    {
        get => _channels;
        init
        {
            var channels = value.IsDefault ? [] : value;
            if (channels.Length > MaxChannels)
            {
                throw new ArgumentException($"A mixer holds at most {MaxChannels} channels.", nameof(Channels));
            }

            var ids = new HashSet<MixerChannelId>();
            foreach (var channel in channels)
            {
                ArgumentNullException.ThrowIfNull(channel, nameof(Channels));
                if (!ids.Add(channel.Id))
                {
                    throw new ArgumentException($"Mixer channel {channel.Id} appears more than once.", nameof(Channels));
                }

                if (channel.FindSend(channel.Id) is not null)
                {
                    throw new ArgumentException($"Mixer channel {channel.Name} cannot send to itself.", nameof(Channels));
                }
            }

            _channels = channels;
        }
    }

    public double MasterGainDecibels
    {
        get => _master;
        init => _master = value is >= MixerChannel.MinGainDecibels and <= MixerChannel.MaxGainDecibels
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MasterGainDecibels), value, "Gain must be between -120 and 24 dB.");
    }

    public MixerChannel? Find(MixerChannelId id)
    {
        foreach (var channel in _channels)
        {
            if (channel.Id == id)
            {
                return channel;
            }
        }

        return null;
    }

    /// <summary>Replaces the channel with the same ID, or adds it.</summary>
    public Mixer With(MixerChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var index = -1;
        for (var i = 0; i < _channels.Length; i++)
        {
            if (_channels[i].Id == channel.Id)
            {
                index = i;
                break;
            }
        }

        return this with { Channels = index < 0 ? _channels.Add(channel) : _channels.SetItem(index, channel) };
    }

    /// <summary>
    /// Removes a channel, clears the output of any channel that fed it (those fall back to the master), and removes
    /// the sends to it.
    /// </summary>
    public Mixer Without(MixerChannelId id) =>
        this with { Channels = [.. _channels.Where(c => c.Id != id).Select(c => (c.Output == id ? c with { Output = null } : c).WithoutSend(id))] };
}
