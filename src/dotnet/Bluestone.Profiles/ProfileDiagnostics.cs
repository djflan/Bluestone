namespace Bluestone.Profiles;

public enum ProfileDiagnosticSeverity
{
    /// <summary>The profile loads, but something is probably a mistake (an unknown property, an unused parameter).</summary>
    Warning,

    /// <summary>The profile is rejected.</summary>
    Error,
}

/// <summary>A validation finding with the JSON path it applies to, e.g. <c>$.banks[2].programs[5].number</c>.</summary>
public sealed record ProfileDiagnostic(ProfileDiagnosticSeverity Severity, string Path, string Message)
{
    public override string ToString() => $"{Severity} {Path}: {Message}";
}

public sealed record ProfileLoadResult(DeviceProfile? Profile, IReadOnlyList<ProfileDiagnostic> Diagnostics)
{
    public bool Succeeded => Profile is not null;
}

/// <summary>Limits applied to untrusted profile files.</summary>
public sealed record ProfileLoadOptions
{
    public static readonly ProfileLoadOptions Default = new();

    public int MaxBytes { get; init; } = 4 * 1024 * 1024;
}
