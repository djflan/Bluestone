namespace Bluestone.Tests.Integration;

internal static class RepositoryPaths
{
    /// <summary>The repository root, found by walking up from the test binaries to <c>global.json</c>.</summary>
    public static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
        }
    }

    public static string Profiles => Path.Combine(Root, "profiles");
}
