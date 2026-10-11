using System.Collections.Immutable;

namespace Bluestone.Profiles;

/// <summary>A profile file that could not be used, and why.</summary>
public sealed record ProfileLoadFailure(string Source, IReadOnlyList<ProfileDiagnostic> Diagnostics);

/// <summary>
/// The set of profiles available to a session, keyed by profile ID. Loading a directory never
/// throws for bad content: rejected files are listed in <see cref="Failures"/> so the UI can explain them.
/// </summary>
public sealed class ProfileCatalog
{
    private readonly ImmutableDictionary<string, DeviceProfile> _byId;

    private ProfileCatalog(ImmutableDictionary<string, DeviceProfile> byId, ImmutableArray<ProfileLoadFailure> failures)
    {
        _byId = byId;
        Failures = failures;
    }

    public static ProfileCatalog Empty { get; } = new(ImmutableDictionary<string, DeviceProfile>.Empty.WithComparers(StringComparer.Ordinal), []);

    /// <summary>Profiles ordered by name.</summary>
    public ImmutableArray<DeviceProfile> Profiles => [.. _byId.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];

    public ImmutableArray<ProfileLoadFailure> Failures { get; }

    /// <summary>Loads every <c>*.bluestone-profile.json</c> file directly inside <paramref name="directory"/>.</summary>
    public static ProfileCatalog LoadDirectory(string directory, ProfileLoadOptions? options = null) => Empty.AddDirectory(directory, options);

    /// <summary>Adds every profile file in <paramref name="directory"/>; a missing directory adds nothing.</summary>
    public ProfileCatalog AddDirectory(string directory, ProfileLoadOptions? options = null)
    {
        var catalog = this;
        if (!Directory.Exists(directory))
        {
            return catalog;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*" + ProfileLoader.FileExtension).Order(StringComparer.Ordinal))
        {
            catalog = catalog.Add(file, ProfileLoader.LoadFile(file, options));
        }

        return catalog;
    }

    public DeviceProfile? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Adds a load result. A profile whose ID is already present is recorded as a failure.</summary>
    public ProfileCatalog Add(string source, ProfileLoadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Profile is not { } profile)
        {
            return new ProfileCatalog(_byId, Failures.Add(new ProfileLoadFailure(source, result.Diagnostics)));
        }

        if (_byId.ContainsKey(profile.Id))
        {
            var duplicate = new ProfileDiagnostic(ProfileDiagnosticSeverity.Error, "$.id", $"profile \"{profile.Id}\" is already loaded from another file.");
            return new ProfileCatalog(_byId, Failures.Add(new ProfileLoadFailure(source, [.. result.Diagnostics, duplicate])));
        }

        return new ProfileCatalog(_byId.Add(profile.Id, profile), Failures);
    }
}
