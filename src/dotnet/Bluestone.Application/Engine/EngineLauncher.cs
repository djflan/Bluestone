using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Bluestone.Application.Engine;

/// <summary>
/// Builds the start information for the engine process (ADR 0044): the <c>Bluestone.Engine</c> app host beside this
/// assembly, else <c>Bluestone.Engine.dll</c> run by the <c>dotnet</c> host. It is always started directly, never
/// through a shell, with its standard streams redirected.
/// </summary>
internal static class EngineLauncher
{
    public const string EngineName = "Bluestone.Engine";

    public static ProcessStartInfo CreateStartInfo(string path, string pipeName, string token, bool restarted, bool allowTestCommands, bool testAudio = false)
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            info.FileName = DotnetHost();
            info.ArgumentList.Add(path);
        }
        else
        {
            info.FileName = path;
        }

        info.ArgumentList.Add("--pipe");
        info.ArgumentList.Add(pipeName);
        info.ArgumentList.Add("--token");
        info.ArgumentList.Add(token);
        if (restarted)
        {
            info.ArgumentList.Add("--restarted");
        }

        if (allowTestCommands)
        {
            info.ArgumentList.Add("--allow-test-commands");
            if (testAudio)
            {
                info.ArgumentList.Add("--test-audio");
            }
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null && Directory.Exists(directory))
        {
            info.WorkingDirectory = directory;
        }

        return info;
    }

    /// <summary>The engine beside this assembly: the app host if present, else the managed <c>.dll</c>.</summary>
    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty location (single-file and NativeAOT) falls back to AppContext.BaseDirectory.")]
    public static string ResolveDefault()
    {
        var location = typeof(EngineLauncher).Assembly.Location;
        var directory = string.IsNullOrEmpty(location) ? AppContext.BaseDirectory : Path.GetDirectoryName(location) ?? AppContext.BaseDirectory;
        var appHost = Path.Combine(directory, OperatingSystem.IsWindows() ? EngineName + ".exe" : EngineName);
        if (File.Exists(appHost))
        {
            return appHost;
        }

        var managed = Path.Combine(directory, EngineName + ".dll");
        return File.Exists(managed)
            ? managed
            : throw new FileNotFoundException($"The engine was not found beside {directory}.", appHost);
    }

    private static string DotnetHost()
    {
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(hostPath) && File.Exists(hostPath))
        {
            return hostPath;
        }

        var processPath = Environment.ProcessPath;
        if (processPath is not null && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }
}
