using System.Collections.Immutable;
using Bluestone.Domain.Midi;

namespace Bluestone.Domain.Devices;

/// <summary>A parameter and the value stored for it, normalized from 0 to 1 (see <see cref="ControlValue.FromFraction"/>).</summary>
public readonly record struct ParameterValue(ParameterId Id, ControlValue Value);

/// <summary>
/// A plugin's saved state as the worker last reported it: opaque bytes and the format they are in.
/// Bluestone stores it, never interprets it, and hands it back when it re-creates the instance (ADR 0025).
/// </summary>
public sealed record PluginState
{
    public const int MaxLength = 128 * 1024 * 1024;
    public const int MaxFormatLength = 64;

    public PluginState(ByteBlock data, string format)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        Data = data.Length <= MaxLength ? data : throw new ArgumentOutOfRangeException(nameof(data), data.Length, "Plugin state is too large.");
        Format = format.Length <= MaxFormatLength ? format : throw new ArgumentException($"State formats are at most {MaxFormatLength} characters.", nameof(format));
    }

    public ByteBlock Data { get; }

    /// <summary>Who wrote the bytes and in which version, so a newer plugin can refuse an incompatible older state.</summary>
    public string Format { get; }
}

/// <summary>
/// One configured occurrence of a device in a chain. Its identity is stable for the life of the
/// project, whatever happens to its position, its chain, or the plugin behind it. This is project
/// state only: the running processor or plugin instance is a runtime object that is rebuilt from this.
/// </summary>
public sealed record DeviceInstance
{
    public const int MaxNameLength = 256;

    private readonly string _name = string.Empty;
    private readonly ImmutableArray<ParameterValue> _parameters = [];

    public required DeviceId Id { get; init; }

    public required DeviceReference Definition { get; init; }

    /// <summary>The user's name for this occurrence, or empty to show the definition's name.</summary>
    public string Name
    {
        get => _name;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _name = value.Length <= MaxNameLength ? value : throw new ArgumentException($"Device names are limited to {MaxNameLength} characters.", nameof(Name));
        }
    }

    /// <summary>A bypassed device passes its input through unchanged and keeps everything else about itself.</summary>
    public bool IsBypassed { get; init; }

    /// <summary>Stored parameter values in parameter ID order. A parameter with no entry is at its default.</summary>
    public ImmutableArray<ParameterValue> Parameters
    {
        get => _parameters;
        init => _parameters = Canonical(value);
    }

    public PluginState? State { get; init; }

    public static DeviceInstance Create(DeviceReference definition) => new() { Id = DeviceId.New(), Definition = definition };

    /// <summary>The name to show: the user's, else the definition's last-seen name, else its ID.</summary>
    public string DisplayName => Name.Length > 0 ? Name : Definition.DisplayName ?? Definition.Id.Value;

    public ControlValue? ValueOf(ParameterId id)
    {
        foreach (var parameter in _parameters)
        {
            if (parameter.Id == id)
            {
                return parameter.Value;
            }
        }

        return null;
    }

    public DeviceInstance WithParameter(ParameterId id, ControlValue value) =>
        this with { Parameters = [.. _parameters.Where(p => p.Id != id), new ParameterValue(id, value)] };

    /// <summary>An independent copy with a new identity, for presets and duplicated tracks.</summary>
    public DeviceInstance Duplicate() => this with { Id = DeviceId.New() };

    private static ImmutableArray<ParameterValue> Canonical(ImmutableArray<ParameterValue> values)
    {
        if (values.IsDefaultOrEmpty)
        {
            return [];
        }

        var sorted = values.OrderBy(v => v.Id.Value).ToImmutableArray();
        for (var i = 1; i < sorted.Length; i++)
        {
            if (sorted[i].Id == sorted[i - 1].Id)
            {
                throw new ArgumentException($"Parameter {sorted[i].Id} is stored more than once.", nameof(values));
            }
        }

        return sorted;
    }
}
