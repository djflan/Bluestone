using Bluestone.Domain.Midi;

namespace Bluestone.Domain.Routing;

/// <summary>
/// A reference to a device profile by its stable ID. The display name is remembered so a missing
/// profile can still be named to the user.
/// </summary>
public sealed record ProfileReference
{
    public ProfileReference(string profileId, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ProfileId = profileId.Length <= 100 ? profileId : throw new ArgumentException("Profile IDs are at most 100 characters.", nameof(profileId));
        DisplayName = displayName;
    }

    public string ProfileId { get; }

    public string? DisplayName { get; }
}

/// <summary>
/// Stable identity hints for a MIDI endpoint: the provider and its stable key (for example a CoreMIDI
/// unique ID), plus the name, manufacturer, and model last seen. Never a port index, and never used
/// to decide which device profile applies.
/// </summary>
public sealed record EndpointReference
{
    public EndpointReference(string providerId, string endpointKey, string? displayName = null, string? manufacturer = null, string? model = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointKey);
        ProviderId = providerId;
        EndpointKey = endpointKey;
        DisplayName = displayName;
        Manufacturer = manufacturer;
        Model = model;
    }

    public string ProviderId { get; }

    public string EndpointKey { get; }

    public string? DisplayName { get; }

    public string? Manufacturer { get; }

    public string? Model { get; }
}

/// <summary>A voice chosen from a profile: a bank by its profile-local ID and a program in it.</summary>
public sealed record VoiceAssignment(string BankId, ProgramNumber Program);
