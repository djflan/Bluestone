using Bluestone.Domain.Midi;
using Bluestone.Midi.Endpoints;

namespace Bluestone.Presentation;

/// <summary>An output choice. A remembered but disconnected endpoint is listed so the route stays visible.</summary>
public sealed record OutputOption(EndpointId? Id, string Name, string Detail, bool IsAvailable)
{
    public static readonly OutputOption None = new(null, "No output", "The track is silent", true);

    /// <summary>Shown when selected tracks use different outputs; choosing it changes nothing.</summary>
    public bool IsMixed { get; init; }

    public static OutputOption Mixed(int count) => new(null, "Mixed", $"{count} different outputs", false) { IsMixed = true };

    public override string ToString() => Name;
}

/// <summary>A profile choice; <see cref="Id"/> is null for "no profile".</summary>
public sealed record ProfileOption(string? Id, string Name, bool IsInstalled)
{
    public static readonly ProfileOption None = new(null, "No profile", true);

    public static readonly ProfileOption Mixed = new(null, "Mixed", false) { IsMixed = true };

    public bool IsMixed { get; init; }

    public override string ToString() => Name;
}

/// <summary>A channel choice; <see cref="Channel"/> is null to keep each event's own channel.</summary>
public sealed record ChannelOption(MidiChannel? Channel)
{
    public static readonly IReadOnlyList<ChannelOption> All =
        [new((MidiChannel?)null), .. MidiChannel.All.Select(c => new ChannelOption(c))];

    public static readonly ChannelOption Mixed = new((MidiChannel?)null) { IsMixed = true };

    public bool IsMixed { get; init; }

    public string Name => IsMixed ? "Mixed" : Channel is { } c ? $"Channel {c.Number}" : "Track channels";

    public override string ToString() => Name;
}

public sealed record BankOption(string? Id, string Name)
{
    public static readonly BankOption None = new(null, "No voice change");

    public static readonly BankOption Mixed = new(null, "Mixed") { IsMixed = true };

    public bool IsMixed { get; init; }

    public override string ToString() => Name;
}

public sealed record ProgramOption(ProgramNumber Program, string Name)
{
    public static readonly ProgramOption Mixed = new(new ProgramNumber(0), "Mixed") { IsMixed = true };

    public bool IsMixed { get; init; }

    public override string ToString() => Name;
}

public enum RouteHealth
{
    /// <summary>Playing to a connected output.</summary>
    Ready,

    /// <summary>Playable, but something needs attention (name match, missing profile).</summary>
    Attention,

    /// <summary>Not playable: no output, or the output is missing.</summary>
    Offline,
}
