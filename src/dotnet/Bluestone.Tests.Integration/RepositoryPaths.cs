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

    /// <summary>The device profiles that ship with the application.</summary>
    public static string Profiles => Path.Combine(Root, "src", "dotnet", "Bluestone.Desktop", "Profiles");

    /// <summary>Standard MIDI files shared by the test projects.</summary>
    public static string MidiFixtures => Path.Combine(Root, "src", "dotnet", "TestFixtures", "Midi");
}
