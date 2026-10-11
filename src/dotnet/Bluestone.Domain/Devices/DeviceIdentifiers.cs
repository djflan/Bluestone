using System.Globalization;

namespace Bluestone.Domain.Devices;

/// <summary>
/// Stable identity of one device instance in a project. Automation, routing taps, and plugin state all
/// refer to this, never to a position in a chain, so reordering or moving a device changes none of them.
/// </summary>
public readonly record struct DeviceId(Guid Value)
{
    public static DeviceId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>Stable identity of a device chain, which keeps it when the chain moves to another owner.</summary>
public readonly record struct DeviceChainId(Guid Value)
{
    public static DeviceChainId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// A parameter of one device definition. Plugin parameters use the plugin's own numeric ID (a VST3
/// <c>ParamID</c>, for example), so a number means the same thing in every project and after every reorder.
/// </summary>
public readonly record struct ParameterId(uint Value)
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Names the implementation behind a device: a built-in processor (<c>bluestone.midi.transpose</c>) or a
/// plugin (<c>vst3:&lt;class id&gt;</c>). It identifies what to load, never one occurrence of it.
/// </summary>
public readonly record struct DeviceDefinitionId
{
    public const int MaxLength = 256;

    public DeviceDefinitionId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Length <= MaxLength ? value : throw new ArgumentException($"Device definition IDs are at most {MaxLength} characters.", nameof(value));
    }

    /// <summary>Empty for <c>default</c>.</summary>
    public string Value { get; }

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// A stored reference to a device definition. The last-seen name and version are remembered so a plugin
/// that is no longer installed can still be named to the user, and its device keeps its place, its
/// parameter values, and its saved state until the plugin comes back.
/// </summary>
public sealed record DeviceReference
{
    public DeviceReference(DeviceDefinitionId id, string? displayName = null, string? version = null)
    {
        if (id.Value is null)
        {
            throw new ArgumentException("A device reference needs a definition ID.", nameof(id));
        }

        Id = id;
        DisplayName = displayName;
        Version = version;
    }

    public DeviceDefinitionId Id { get; }

    public string? DisplayName { get; }

    public string? Version { get; }
}
