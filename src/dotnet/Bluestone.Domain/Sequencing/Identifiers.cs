namespace Bluestone.Domain.Sequencing;

/// <summary>Stable identity of an event within a project, preserved across edits and saves.</summary>
public readonly record struct EventId(Guid Value)
{
    public static EventId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>Stable identity of a track within a project.</summary>
public readonly record struct TrackId(Guid Value)
{
    public static TrackId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>Stable identity of a clip within a project.</summary>
public readonly record struct ClipId(Guid Value)
{
    public static ClipId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
