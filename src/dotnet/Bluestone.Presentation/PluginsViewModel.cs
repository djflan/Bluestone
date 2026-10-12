using System.Collections.ObjectModel;
using System.Globalization;
using Bluestone.Application.Plugins;
using Bluestone.Domain.Devices;
using Bluestone.Plugins;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>A hosting mode as a choice in a list. <see cref="Mode"/> null stands for "use the global mode".</summary>
public sealed record HostingChoice(HostingMode? Mode, string Name, string Description)
{
    public override string ToString() => Name;
}

/// <summary>An available plugin, as the plugins view lists it, with how it is hosted and how often it crashed.</summary>
public sealed partial class PluginRow : ObservableObject
{
    private readonly PluginsViewModel? _owner;
    private bool _syncing;

    internal PluginRow(PluginsViewModel? owner, PluginSummary plugin, string detail, string module)
    {
        _owner = owner;
        Name = plugin.Name;
        Detail = detail;
        Module = module;
        Definition = plugin.Definition;
    }

    public string Name { get; }

    public string Detail { get; }

    public string Module { get; }

    public DeviceDefinitionId Definition { get; }

    /// <summary>"Use global (By plugin)", or a mode chosen for this plugin.</summary>
    [ObservableProperty]
    public partial HostingChoice? Hosting { get; set; }

    /// <summary>"Crashed 2 times (1 while sharing a process)", or empty.</summary>
    [ObservableProperty]
    public partial string CrashText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasCrashed { get; private set; }

    /// <summary>It crashed and is not hosted individually yet: offer to.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(HostIndividuallyCommand))]
    public partial bool CanHostIndividually { get; private set; }

    internal void Sync(HostingChoice hosting, PluginCrashCount? crashes, HostingMode effective)
    {
        _syncing = true;
        try
        {
            Hosting = hosting;
            HasCrashed = crashes is { Crashes: > 0 };
            CrashText = crashes is { Crashes: > 0 } c ? PluginsViewModel.CrashTextOf(c) : string.Empty;
            CanHostIndividually = HasCrashed && effective != HostingMode.Individually;
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnHostingChanged(HostingChoice? value)
    {
        if (!_syncing && value is not null && _owner is not null)
        {
            _ = _owner.SetOverrideAsync(this, value.Mode);
        }
    }

    [RelayCommand(CanExecute = nameof(CanHostIndividually))]
    private Task HostIndividuallyAsync() => _owner?.SetOverrideAsync(this, HostingMode.Individually) ?? Task.CompletedTask;
}

/// <summary>A module that could not be read, or that is quarantined, and why.</summary>
public sealed record ModuleProblemRow(string ModulePath, string Module, string Problem);

/// <summary>
/// The plugins view (Window ▸ Plugins…): what the last scan found in the plugin folders, which modules failed and why,
/// and which are quarantined because they crashed or hung their scanner. "Rescan all" reads every module again;
/// "Retry" reads one quarantined module again. Newly found plugins join the device catalog without a restart, because
/// <see cref="PluginDeviceHost"/> follows the same <see cref="PluginLibrary"/>.
/// With the device host it also sets how plugins share worker processes (ADR 0034): the global hosting mode, automatic
/// restart, and a mode per plugin; it shows how often each plugin crashed and offers to host a crashed one individually.
/// Changes are saved to the settings store and applied to running plugins at once.
/// </summary>
public sealed partial class PluginsViewModel : ObservableObject
{
    private readonly PluginLibrary _library;
    private readonly IUiDispatcher _dispatcher;
    private readonly PluginDeviceHost? _devices;
    private readonly PluginHostingSettingsStore? _store;
    private bool _syncing;

    public PluginsViewModel(PluginLibrary library, IUiDispatcher dispatcher, PluginDeviceHost? devices = null, PluginHostingSettingsStore? store = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _devices = devices;
        _store = store;
        Folders = library.Folders;
        _library.Changed += (_, _) => _dispatcher.Run(Sync);
        if (devices is not null)
        {
            devices.Crashed += (_, _) => _dispatcher.Post(Sync);
        }

        Sync();
        StatusText = Summary();
    }

    /// <summary>The global modes, from most sharing to least.</summary>
    public IReadOnlyList<HostingChoice> HostingModes { get; } =
        [.. PluginHostingSettings.Modes.Select(m => new HostingChoice(m, PluginHostingSettings.NameOf(m), PluginHostingSettings.DescriptionOf(m)))];

    /// <summary>The choices for one plugin: the global mode, then each mode.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<HostingChoice> PluginHostingChoices { get; private set; } = [];

    /// <summary>Whether hosting can be set here (the view was given the device host).</summary>
    public bool HasHosting => _devices is not null;

    [ObservableProperty]
    public partial HostingChoice? GlobalHosting { get; set; }

    /// <summary>What the chosen global mode means.</summary>
    [ObservableProperty]
    public partial string GlobalHostingText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool AutoRestart { get; set; }

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

    internal static string CrashTextOf(PluginCrashCount crashes)
    {
        var text = crashes.Crashes == 1 ? "Crashed once" : string.Create(CultureInfo.InvariantCulture, $"Crashed {crashes.Crashes} times");
        return crashes.Shared switch
        {
            0 => text + ", hosted alone.",
            _ when crashes.Shared == crashes.Crashes => text + ", each time sharing a process with other plugins (any of them may have caused it).",
            _ => text + string.Create(CultureInfo.InvariantCulture, $", {crashes.Shared} of them sharing a process with other plugins."),
        };
    }

    /// <summary>Hosts one plugin as <paramref name="mode"/>, or as the global mode says when null; saves and applies it.</summary>
    internal async Task SetOverrideAsync(PluginRow row, HostingMode? mode)
    {
        if (_devices is null)
        {
            return;
        }

        var settings = _devices.Hosting.WithOverride(row.Definition, mode);
        var name = mode is { } m ? PluginHostingSettings.NameOf(m).ToLowerInvariant() : "as the global mode says";
        await ApplyAsync(settings, $"{row.Name} is now hosted {name}.");
    }

    partial void OnGlobalHostingChanged(HostingChoice? value)
    {
        GlobalHostingText = value?.Description ?? string.Empty;
        if (!_syncing && value?.Mode is { } mode && _devices is not null)
        {
            _ = ApplyAsync(_devices.Hosting with { Mode = mode }, $"Plugins are now hosted {value.Name.ToLowerInvariant()}.");
        }
    }

    /// <summary>Whether floating plugin editors follow the selected track (unpinned ones hide while their track is not selected).</summary>
    [ObservableProperty]
    public partial bool EditorsFollowSelection { get; set; } = true;

    partial void OnEditorsFollowSelectionChanged(bool value)
    {
        if (!_syncing && _devices is not null)
        {
            _ = ApplyAsync(_devices.Hosting with { EditorsFollowSelection = value }, value
                ? "Plugin editors now follow the selected track; pin one to keep it open."
                : "Plugin editors now stay open until closed.");
        }
    }

    partial void OnAutoRestartChanged(bool value)
    {
        if (!_syncing && _devices is not null)
        {
            _ = ApplyAsync(_devices.Hosting with { AutoRestart = value }, value
                ? "Crashed plugins now restart by themselves, up to 3 times in 5 minutes."
                : "Crashed plugins now wait for Restart.");
        }
    }

    private async Task ApplyAsync(PluginHostingSettings settings, string done)
    {
        if (_devices is null || settings.Equals(_devices.Hosting))
        {
            return;
        }

        try
        {
            _store?.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"The hosting settings could not be saved: {ex.Message}";
        }

        await RunAsync(async () =>
        {
            ProgressText = "Moving plugins to their processes…";
            var moved = await _devices.ApplyHostingAsync(settings);
            Sync();
            StatusText = moved.IsEmpty ? done : $"{done} {Count(moved.Length, "device")} restarted in a new process, with {(moved.Length == 1 ? "its" : "their")} state.";
        });
    }

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
        var rows = modules.SelectMany(m => m.Plugins.Select(p => (Plugin: p, Module: m.Name))).ToList();
        if (!rows.Select(r => (r.Plugin.Name, Detail(r.Plugin), r.Module, r.Plugin.Definition)).SequenceEqual(Plugins.Select(p => (p.Name, p.Detail, p.Module, p.Definition))))
        {
            Plugins.Clear();
            foreach (var (plugin, module) in rows)
            {
                Plugins.Add(new PluginRow(this, plugin, Detail(plugin), module));
            }
        }

        Replace(Failures, modules.Where(m => m.State == PluginModuleState.Failed).Select(Row));
        Replace(Quarantined, modules.Where(m => m.State == PluginModuleState.Quarantined).Select(Row));
        HasFailures = Failures.Count > 0;
        HasQuarantined = Quarantined.Count > 0;
        SyncHosting();
    }

    private void SyncHosting()
    {
        if (_devices is null)
        {
            return;
        }

        var settings = _devices.Hosting;
        _syncing = true;
        try
        {
            GlobalHosting = HostingModes.First(c => c.Mode == settings.Mode);
            GlobalHostingText = GlobalHosting.Description;
            AutoRestart = settings.AutoRestart;
            EditorsFollowSelection = settings.EditorsFollowSelection;
            var global = new HostingChoice(null, $"Global ({GlobalHosting.Name})", "Follow the global hosting mode.");
            if (PluginHostingChoices.Count == 0 || PluginHostingChoices[0] != global)
            {
                PluginHostingChoices = [global, .. HostingModes];
            }
        }
        finally
        {
            _syncing = false;
        }

        var crashes = _devices.CrashCounts;
        foreach (var row in Plugins)
        {
            var chosen = settings.Overrides.TryGetValue(row.Definition.Value, out var mode) ? mode : (HostingMode?)null;
            row.Sync(PluginHostingChoices.First(c => c.Mode == chosen), crashes.GetValueOrDefault(row.Definition), settings.ModeFor(row.Definition));
        }
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
