namespace Bluestone.Tests.Ui;

/// <summary>The repository the tests were built from, for tests that read source files (lints, design specimens).</summary>
internal static class Repo
{
    /// <summary>The repository's root: the folder above src/dotnet/Bluestone.slnx.</summary>
    public static string Root { get; } = Find();

    /// <summary><paramref name="relative"/> (with forward slashes) under the root.</summary>
    public static string File(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (System.IO.File.Exists(Path.Combine(directory.FullName, "src", "dotnet", "Bluestone.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No repository above {AppContext.BaseDirectory}.");
    }
}
