using System.Collections.Immutable;
using Bluestone.Domain.Midi;

namespace Bluestone.Domain.Routing;

/// <summary>One channel remapped to another.</summary>
public readonly record struct ChannelRemap(MidiChannel From, MidiChannel To);

/// <summary>
/// What a connection does to the channel of the events it carries. The clips never change: only the
/// connection decides which channel an instrument hears (ADR 0023).
/// </summary>
/// <remarks>
/// The steps run in this order: <see cref="Only"/> removes events on other channels, then either
/// <see cref="Force"/> or <see cref="Remap"/> readdresses what is left. Events with no channel (SysEx)
/// are never touched. The channel is a MIDI 1.0 channel on a logical port; MIDI 2.0 groups are the
/// port's business, not the music's, so this type does not widen when they arrive.
/// </remarks>
public sealed record ChannelMapping
{
    private readonly ImmutableArray<MidiChannel> _only = [];
    private readonly ImmutableArray<ChannelRemap> _remap = [];

    /// <summary>Leaves every event on the channel it was written on.</summary>
    public static ChannelMapping Preserve { get; } = new();

    /// <summary>Sends every channel event on <paramref name="channel"/>.</summary>
    public static ChannelMapping ForceTo(MidiChannel channel) => new() { Force = channel };

    /// <summary>When not empty, only events on these channels pass.</summary>
    public ImmutableArray<MidiChannel> Only
    {
        get => _only;
        init => _only = value.IsDefault ? [] : [.. value.Distinct().Order()];
    }

    /// <summary>When set, every channel event that passes is sent on this channel.</summary>
    public MidiChannel? Force { get; init; }

    /// <summary>Channels to readdress; channels not listed keep their number. Not allowed together with <see cref="Force"/>.</summary>
    /// <exception cref="ArgumentException">A channel is remapped twice.</exception>
    public ImmutableArray<ChannelRemap> Remap
    {
        get => _remap;
        init
        {
            var remap = value.IsDefault ? [] : value;
            if (remap.Select(r => r.From).Distinct().Count() != remap.Length)
            {
                throw new ArgumentException("A channel can be remapped only once.", nameof(Remap));
            }

            _remap = [.. remap.OrderBy(r => r.From)];
        }
    }

    /// <summary>True when events pass exactly as written.</summary>
    public bool IsPreserving => _only.IsEmpty && Force is null && _remap.IsEmpty;

    /// <summary>The channel an event written on <paramref name="source"/> is sent on, or false when the mapping drops it.</summary>
    public bool TryMap(MidiChannel source, out MidiChannel result)
    {
        result = source;
        if (!_only.IsEmpty && !_only.Contains(source))
        {
            return false;
        }

        if (Force is { } forced)
        {
            result = forced;
            return true;
        }

        foreach (var remap in _remap)
        {
            if (remap.From == source)
            {
                result = remap.To;
                break;
            }
        }

        return true;
    }

    /// <summary>Whether this mapping is valid: a forced channel and a remap table exclude each other.</summary>
    public bool IsConsistent => Force is null || _remap.IsEmpty;

    public bool Equals(ChannelMapping? other) =>
        other is not null && Force == other.Force && _only.SequenceEqual(other._only) && _remap.SequenceEqual(other._remap);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Force);
        foreach (var channel in _only)
        {
            hash.Add(channel);
        }

        foreach (var remap in _remap)
        {
            hash.Add(remap);
        }

        return hash.ToHashCode();
    }
}
