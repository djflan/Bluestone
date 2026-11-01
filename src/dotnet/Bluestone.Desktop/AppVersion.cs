using System.Reflection;

namespace Bluestone.Desktop;

/// <summary>The version this build shows: the semver from Directory.Build.props and the commit it was built from.</summary>
internal static class AppVersion
{
    /// <summary>How many characters of the commit hash are shown, as <c>git log --oneline</c> shows them.</summary>
    private const int ShortCommitLength = 7;

    private static readonly string? Informational =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>E.g. "v0.0.1 a32f6d1"; just "v0.0.1" when the build did not record a commit.</summary>
    public static string Display { get; } = Format(Informational);

    /// <summary>E.g. "v0.0.1".</summary>
    public static string Release { get; } = Display.Split(' ')[0];

    /// <summary>The short commit the build was made from, e.g. "a32f6d1", or empty when it was not recorded.</summary>
    public static string Commit { get; } = Display.Contains(' ', StringComparison.Ordinal) && Display.StartsWith('v') ? Display.Split(' ')[1] : string.Empty;

    /// <summary>Formats an informational version such as "0.0.1+a32f6d1…".</summary>
    internal static string Format(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "development build";
        }

        var parts = informationalVersion.Split('+', 2);
        var version = $"v{parts[0]}";
        return parts.Length == 2 && parts[1].Length > 0
            ? $"{version} {parts[1][..Math.Min(ShortCommitLength, parts[1].Length)]}"
            : version;
    }
}
