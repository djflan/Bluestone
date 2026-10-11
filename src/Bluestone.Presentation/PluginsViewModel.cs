using System.Collections.ObjectModel;
using System.Globalization;
using Bluestone.Application.Plugins;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>An available plugin, as the plugins view lists it.</summary>
public sealed record PluginRow(string Name, string Detail, string Module);

/// <summary>A module that could not be read, or that is quarantined, and why.</summary>
public sealed record ModuleProblemRow(string ModulePath, string Module, string Problem);

/// <summary>
/// The plugins view (Window ▸ Plugins…): what the last scan found in the plugin folders, which modules failed and why,
/// and which are quarantined because they crashed or hung their scanner. "Rescan all" reads every module again;
/// "Retry" reads one quarantined module again. Newly found plugins join the device catalog without a restart, because
/// <see cref="PluginDeviceHost"/> follows the same <see cref="PluginLibrary"/>.
/// </summary>
public sealed partial class PluginsViewModel : ObservableObject
{
    private readonly PluginLibrary _library;
    private readonly IUiDispatcher _dispatcher;

    public PluginsViewModel(PluginLibrary library, IUiDispatcher dispatcher)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Folders = library.Folders;
        _library.Changed += (_, _) => _dispatcher.Run(Sync);
        Sync();
        StatusText = Summary();
    }

    /// <summary>Where module files are looked for.</summary>
    public IReadOnlyList<string> Folders { get; }

    public ObservableCollection<PluginRow> Plugins { get; } = [];

    /// <summary>Modules that could not be read; they are tried again on the next scan.</summary>
    public ObservableCollection<ModuleProblemRow> Failures { get; } = [];

    /// <summary>Modules skipped because they crashed or hung their scanner, until retried.</summary>
    public ObservableCollection<ModuleProblemRow> Quarantined { get; } = [];

    [ObservableProperty]
    public partial bool HasFailures { get; private set; }

    [ObservableProperty]
    public partial bool HasQuarantined { get; private set; }

    /// <summary>What the scan found when the view was made, then what the last action did.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RescanAllCommand), nameof(RetryCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Fraction of the modules read so far by the running scan, 0 to 1.</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; }

    /// <summary>The module being read by the running scan.</summary>
    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RescanAllAsync()
    {
        await RunAsync(async () =>
        {
            await _library.RescanAllAsync(OnProgress);
            Sync();
            StatusText = $"Rescanned. {Summary()}";
        });
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RetryAsync(ModuleProblemRow? row)
    {
        if (row is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            ProgressText = $"Reading {row.Module}…";
            var result = await _library.RetryAsync(row.ModulePath);
            Sync();
            StatusText = result.State switch
            {
                PluginModuleState.Loaded => string.Create(CultureInfo.InvariantCulture, $"{result.Name} was read: {Count(result.Plugins.Length, "plugin")} available."),
                PluginModuleState.Quarantined => $"{result.Name} is still quarantined. {result.Problem}",
                _ => $"{result.Name} left quarantine but could not be read. {result.Problem}",
            };
        });
    }

    private bool IsIdle() => !IsBusy;

    private async Task RunAsync(Func<Task> work)
    {
        IsBusy = true;
        Progress = 0;
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Sync();
            StatusText = $"The scan stopped: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            ProgressText = string.Empty;
        }
    }

    // Called by the library, possibly on a scanning thread.
    private void OnProgress(PluginScanProgress progress) => _dispatcher.Run(() =>
    {
        Progress = progress.Total == 0 ? 1 : (double)progress.Done / progress.Total;
        ProgressText = progress.Module is { } module
            ? string.Create(CultureInfo.InvariantCulture, $"Scanning {progress.Done + 1} of {progress.Total}: {Path.GetFileName(module)}")
            : string.Empty;
    });

    private void Sync()
    {
        var modules = _library.Modules;
        Replace(Plugins, modules.SelectMany(m => m.Plugins.Select(p => new PluginRow(p.Name, Detail(p), m.Name))));
        Replace(Failures, modules.Where(m => m.State == PluginModuleState.Failed).Select(Row));
        Replace(Quarantined, modules.Where(m => m.State == PluginModuleState.Quarantined).Select(Row));
        HasFailures = Failures.Count > 0;
        HasQuarantined = Quarantined.Count > 0;
    }

    private string Summary()
    {
        var modules = _library.Modules.Length;
        if (modules == 0)
        {
            return "No plugin modules found in the plugin folders.";
        }

        var text = $"{Count(Plugins.Count, "plugin")} available from {Count(modules, "module")}.";
        if (Failures.Count > 0)
        {
            text += $" {Count(Failures.Count, "module")} could not be read.";
        }

        if (Quarantined.Count > 0)
        {
            text += $" {Count(Quarantined.Count, "module")} quarantined.";
        }

        return text;
    }

    private static string Detail(PluginSummary plugin) =>
        string.Join(" · ", new[] { plugin.Kind, plugin.Vendor, plugin.Version.Length > 0 ? $"v{plugin.Version}" : string.Empty }.Where(s => s.Length > 0));

    private static ModuleProblemRow Row(PluginModuleSummary module) => new(module.ModulePath, module.Name, module.Problem ?? string.Empty);

    private static string Count(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var wanted = items.ToList();
        if (wanted.SequenceEqual(target))
        {
            return;
        }

        target.Clear();
        foreach (var item in wanted)
        {
            target.Add(item);
        }
    }
}
