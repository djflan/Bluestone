using System.Diagnostics;
using Bluestone.Plugins.Protocol;

namespace Bluestone.Plugins.Workers;

/// <summary>
/// Builds the start information for a worker process. The worker is the <c>Bluestone.PluginWorker</c> app host beside
/// this assembly, else <c>Bluestone.PluginWorker.dll</c> run by the <c>dotnet</c> host. It is always started directly,
/// never through a shell, with all standard streams redirected.
/// </summary>
internal static class WorkerLauncher
{
    public const string WorkerName = "Bluestone.PluginWorker";

    /// <summary>Start information for a worker; <paramref name="workerPath"/> is an explicit executable or <c>.dll</c>, or null for the default.</summary>
    public static ProcessStartInfo CreateStartInfo(string? workerPath, WorkerMode mode, string pipeName, IReadOnlyDictionary<string, string>? environment)
    {
        var path = workerPath ?? ResolveDefault();
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

        info.ArgumentList.Add("--mode");
        info.ArgumentList.Add(mode == WorkerMode.Scanner ? "scan" : "host");
        info.ArgumentList.Add("--pipe");
        info.ArgumentList.Add(pipeName);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null && Directory.Exists(directory))
        {
            info.WorkingDirectory = directory;
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                info.Environment[key] = value;
            }
        }

        return info;
    }

    /// <summary>The worker beside this assembly: the app host if present, else the managed <c>.dll</c>.</summary>
    public static string ResolveDefault()
    {
        var location = typeof(WorkerLauncher).Assembly.Location;
        var directory = string.IsNullOrEmpty(location) ? AppContext.BaseDirectory : Path.GetDirectoryName(location) ?? AppContext.BaseDirectory;
        var appHost = Path.Combine(directory, OperatingSystem.IsWindows() ? WorkerName + ".exe" : WorkerName);
        if (File.Exists(appHost))
        {
            return appHost;
        }

        var managed = Path.Combine(directory, WorkerName + ".dll");
        return File.Exists(managed)
            ? managed
            : throw new FileNotFoundException($"The plugin worker was not found beside {directory}.", appHost);
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
