using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bluestone.Application.Audio;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Desktop.Services;
using Bluestone.Desktop.Views;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Plugins;
using Bluestone.Plugins.Scanning;
using Bluestone.Presentation;
using Bluestone.Profiles;

namespace Bluestone.Desktop;

public partial class App : Avalonia.Application
{
    private static AudioViewModel? _audio;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The lifetime shows the splash; start-up runs once the UI loop is going, so the splash can say what it is doing.
            var splash = new SplashWindow();
            splash.QuitRequested += (_, _) => desktop.Shutdown(1);
            desktop.MainWindow = splash;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = StartAsync(desktop, splash));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The least time the splash stays up, so a fast start does not just flash it.</summary>
    private static readonly TimeSpan MinimumSplash = TimeSpan.FromSeconds(5);

    /// <summary>Composes the app behind the splash, reporting each step, then swaps the splash for the main window.</summary>
    private static async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        MainViewModel? viewModel = null;
        List<IMidiEndpointProvider> platformProviders = [];
        PluginHostManager? plugins = null;
        PluginScanner? scanner = null;
        PluginLibrary? library = null;
        desktop.Exit += (_, _) =>
        {
            if (viewModel is not null && plugins is not null)
            {
                Shutdown(viewModel, platformProviders, plugins);
            }
            else
            {
                plugins?.KillWorkers();
                platformProviders.ForEach(p => p.Dispose());
            }

            library?.Dispose();
            scanner?.Dispose();
        };

        try
        {
            await splash.RenderedAsync();
            var window = new MainWindow();
            await Step(splash, "Connecting to MIDI drivers…");
            platformProviders = [.. PlatformProviders.Create(SystemMonotonicClock.Instance)];
            plugins = new PluginHostManager(new PluginHostOptions { DataDirectory = Path.Combine(Path.GetTempPath(), $"bluestone-plugins-{Environment.ProcessId}") });
            scanner = new PluginScanner(new PluginScannerOptions(PluginCacheDirectory));
            library = new PluginLibrary(scanner, PluginFolders);
            viewModel = await ComposeAsync(window, platformProviders, plugins, library, splash);
            window.Attach(viewModel);

            if (_audio?.IsEnabledInSettings == true)
            {
                await Step(splash, "Starting the audio engine…");
            }

            _audio?.StartIfEnabled();

            // A project or MIDI file named on the command line (or by the OS file association) opens at start.
            if (desktop.Args is [var path, ..] && MainViewModel.CanOpenFile(path))
            {
                var model = viewModel;
                window.Opened += async (_, _) => await model.OpenFileAsync(path);
            }

            await Step(splash, "Ready", fraction: 1);
            if (MinimumSplash - started.Elapsed is { TotalMilliseconds: > 0 } remaining)
            {
                await Task.Delay(remaining);
            }

            desktop.MainWindow = window;
            window.Show();
            splash.Close();
        }
#pragma warning disable CA1031 // A failed start is shown on the splash rather than crashing silently.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceError($"Start-up failed: {ex}");
            splash.ShowFailure(ex.Message);
        }
    }

    /// <summary>Shows a start-up step on the splash and waits until it is drawn, so the step runs with it visible.</summary>
    private static async Task Step(SplashWindow? splash, string status, string? detail = null, double? fraction = null)
    {
        if (splash is not null)
        {
            splash.Report(status, detail, fraction);
            await splash.RenderedAsync();
        }
    }

    /// <summary>The composition root: the only place that chooses concrete providers and services. Reports its steps on <paramref name="splash"/>.</summary>
    internal static async Task<MainViewModel> ComposeAsync(MainWindow window, IEnumerable<IMidiEndpointProvider> platformProviders, PluginHostManager plugins, PluginLibrary library, SplashWindow? splash = null)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(window);
        var clock = SystemMonotonicClock.Instance;

        // A built-in virtual bus whose output is shown in the MIDI monitor, so Bluestone is useful
        // and verifiable with no hardware attached. Platform providers are added alongside it.
        var builtIn = new LoopbackMidiProvider(clock, EndpointCapabilities.ScheduledDelivery | EndpointCapabilities.SystemExclusive);
        var monitorPort = builtIn.CreatePort("Bluestone Monitor", "monitor");
        var keyboard = new ComputerKeyboardProvider(clock);
        var providers = new List<IMidiEndpointProvider> { builtIn, keyboard };
        providers.AddRange(platformProviders);
        var endpoints = new EndpointDirectory(providers);

        await Step(splash, "Loading device profiles…");
        var profiles = ProfileCatalog.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "profiles"))
            .AddDirectory(UserProfileDirectory);

        var session = new ProjectSession();
        var playback = new PlaybackController(session, endpoints, profiles, clock, playbackThreadSetup: PlatformProviders.ConfigurePlaybackThread);
        var monitor = new MidiMonitor(await builtIn.OpenInputAsync(monitorPort.InputId));
        await Step(splash, "Scanning plugins…");
        await ScanPluginsAsync(library, splash);
        await Step(splash, "Preparing the workspace…");
        var hosting = new PluginHostingSettingsStore(PluginHostingSettingsPath);
        var devices = new PluginDeviceHost(plugins, session, library, hosting: hosting.Load());
        var dispatcher = new AvaloniaDispatcher();
        var viewModel = new MainViewModel(session, playback, endpoints, new DialogService(window), dispatcher, monitor, new ComputerKeyboardViewModel(keyboard), devices)
        {
            Plugins = new PluginsViewModel(library, dispatcher, devices, hosting),
            LayoutStore = new WorkspaceStore(SettingsDirectory),
        };
        var initialized = viewModel.InitializeAsync();
        ComposeAudio(window, session, playback, devices, viewModel);
        await Step(splash, "Finding MIDI devices and resolving routes…");
        try
        {
            await initialized;
        }
#pragma warning disable CA1031 // As before the splash: a device that fails here is reported by the app, not fatal to start-up.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceError($"Initial device refresh failed: {ex}");
        }

        return viewModel;
    }

    /// <summary>The audio engine (ADR 0032): off until the user starts it or the saved settings say it was on.</summary>
    private static void ComposeAudio(MainWindow window, ProjectSession session, PlaybackController playback, PluginDeviceHost devices, MainViewModel viewModel)
    {
        // Plugins run at the device's rate: each time a device opens, they are restarted at its rate on the UI thread.
        var audio = new AudioPlayback(
            playback,
            session,
            devices.InstanceOf,
            prepareSampleRate: rate => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => devices.SetSampleRateAsync(rate)));
        _audio = new AudioViewModel(audio, new AudioSettingsStore(AudioSettingsPath), new AvaloniaDispatcher());
        viewModel.Mixer.MeterSource = channel => audio.IsRunning && audio.Graph is { } graph
            ? (channel is { } id ? graph.StripOf(id)?.Meter.Read() : graph.Master.Meter.Read())
            : null;
        window.AttachAudio(_audio);
        window.Closed += (_, _) => audio.Dispose();
    }

    /// <summary>Bluestone's local settings folder, e.g. ~/Library/Application Support/Bluestone on macOS. Workspace layouts live here, not in projects.</summary>
    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bluestone");

    /// <summary>Where the audio settings are kept, e.g. ~/Library/Application Support/Bluestone/audio-settings.json on macOS.</summary>
    public static string AudioSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bluestone", "audio-settings.json");

    /// <summary>How plugins share worker processes (ADR 0034), e.g. ~/Library/Application Support/Bluestone/plugin-hosting.json on macOS.</summary>
    public static string PluginHostingSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bluestone", "plugin-hosting.json");

    /// <summary>How long quitting waits for plugins to unload and playback to stop before giving up on an orderly end.</summary>
    private static readonly TimeSpan ShutdownLimit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Stops playback (releasing sounding notes), closes MIDI ports, and releases platform providers. Runs on the UI
    /// thread, which cannot run anything posted to it until this returns, so it waits a bounded time and then kills any
    /// plugin worker still running rather than hang. Errors are logged rather than thrown: nothing useful can be done
    /// with them while quitting.
    /// </summary>
    private static void Shutdown(MainViewModel viewModel, IEnumerable<IMidiEndpointProvider> platformProviders, PluginHostManager plugins)
    {
        try
        {
            if (!StopAsync(viewModel, platformProviders, plugins).Wait(ShutdownLimit))
            {
                System.Diagnostics.Trace.TraceError($"Shutting down took longer than {ShutdownLimit.TotalSeconds:0} s; stopping plugin workers.");
                plugins.KillWorkers();
            }
        }
#pragma warning disable CA1031 // Quitting must not crash; the failure is recorded instead.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceError($"Error while shutting down: {ex}");
            plugins.KillWorkers();
        }
    }

    private static async Task StopAsync(MainViewModel viewModel, IEnumerable<IMidiEndpointProvider> platformProviders, PluginHostManager plugins)
    {
        await viewModel.DisposeAsync().ConfigureAwait(false);
        await plugins.DisposeAsync().ConfigureAwait(false);
        foreach (var provider in platformProviders)
        {
            provider.Dispose();
        }
    }

    /// <summary>
    /// Scans the plugin folders at start. Each module is scanned in its own scanner process, so a module that crashes the
    /// scanner cannot take Bluestone with it; it is reported and skipped on later scans until retried from the plugins
    /// view (ADR 0025).
    /// </summary>
    private static async Task ScanPluginsAsync(PluginLibrary library, SplashWindow? splash)
    {
        // The scan runs off the UI thread so the splash keeps drawing; progress is posted back to it.
        await Task.Run(() => library.ScanAsync(progress => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            splash?.Report(
                progress.Total == 0 ? "Scanning plugins…" : $"Scanning plugins ({Math.Min(progress.Done + 1, progress.Total)} of {progress.Total})…",
                progress.Module is { } module ? Path.GetFileName(module) : null,
                progress.Total == 0 ? null : (double)progress.Done / progress.Total))));
        foreach (var module in library.Modules.Where(m => m.State != PluginModuleState.Loaded))
        {
            System.Diagnostics.Trace.TraceWarning($"Plugin module {module.ModulePath} was not loaded: {module.Problem}");
        }
    }

    /// <summary>The folders scanned for plugin modules, in order. Formats with OS-standard folders add theirs here.</summary>
    public static IReadOnlyList<string> PluginFolders => [UserPluginDirectory, .. PluginLibrary.StandardFolders()];

    /// <summary>The scanner's cache and quarantine list.</summary>
    public static string PluginCacheDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bluestone", "plugin-cache");

    /// <summary>Where users put plugin modules, e.g. ~/Library/Application Support/Bluestone/plugins on macOS.</summary>
    public static string UserPluginDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bluestone", "plugins");

    /// <summary>Where users put their own profiles, e.g. ~/Library/Application Support/Bluestone/profiles on macOS.</summary>
    public static string UserProfileDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bluestone", "profiles");
}
