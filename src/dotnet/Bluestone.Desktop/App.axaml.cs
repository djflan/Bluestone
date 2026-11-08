using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Bluestone.Application.Audio;
using Bluestone.Application.Engine;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Application.Status;
using Bluestone.Desktop.Services;
using Bluestone.Desktop.Views;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Platform.Windows;
using Bluestone.Plugins;
using Bluestone.Plugins.Scanning;
using Bluestone.Presentation;
using Bluestone.Profiles;

namespace Bluestone.Desktop;

public partial class App : Avalonia.Application
{
    private static AudioViewModel? _audio;
    private static EngineSupervisor? _engine;
    private static EnginePlayback? _enginePlayback;

    /// <summary>Cancels start-up work that runs off the UI thread (the plugin scan) when Bluestone quits during start-up.</summary>
    private static readonly CancellationTokenSource StartupCancellation = new();

    /// <summary>The start-up plugin scan while it runs, so quitting can wait for it to stop its scanner process.</summary>
    private static Task? _startupScan;

    private static WindowIcon? _icon;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // macOS reads the application menu once, straight after this; a menu set any later is ignored.
        if (OperatingSystem.IsMacOS())
        {
            AppMenu.InstallApplicationMenu(this, () => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow);
            DockIcon.Set(Controls.StoneMark.CreateDockImage());
        }

        // Earlier Windows ignores title bar colours and keeps its own dark grey.
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            window.Icon ??= _icon ??= Controls.StoneMark.CreateIcon();
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                MatchTitleBar(window);
            }
        });
    }

    /// <summary>Draws a window's title bar in the menu bar's colour, so the two read as one.</summary>
    [SupportedOSPlatform("windows10.0.22000")]
    private void MatchTitleBar(Window window)
    {
        if (window.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } handle
            && this.TryGetResource("Surface.ChromeColor", ActualThemeVariant, out var background) && background is Color chrome
            && this.TryGetResource("Text.PrimaryColor", ActualThemeVariant, out var foreground) && foreground is Color text)
        {
            WindowFrame.SetTitleBarColors(handle.Handle, chrome.ToUInt32() & 0xFFFFFF, text.ToUInt32() & 0xFFFFFF);
        }
    }

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

    /// <summary>Composes the app behind the splash, reporting each step, then swaps the splash for the main window.</summary>
    private static async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash)
    {
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
                // Quitting during start-up: stop the plugin scan, which ends its scanner process, before going.
                StopStartupScan();
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

            // The engine process (ADR 0044) starts alongside the window; editing does not wait for it.
            if (_engine is { } engine)
            {
                _ = engine.StartAsync();
            }

            // A project or MIDI file named on the command line (or by the OS file association) opens at start.
            if (desktop.Args is [var path, ..] && MainViewModel.CanOpenFile(path))
            {
                var model = viewModel;
                window.Opened += async (_, _) => await model.OpenFileAsync(path);
            }

            await Step(splash, "Ready", fraction: 1);
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
        var activities = new ActivityLog();
        PlaybackController playback;

        // MIDI playback, platform endpoints, live input and thru, and audio run in the engine process (ADR 0044), by
        // default where it has been measured against ADR 0043's targets (macOS). BLUESTONE_ENGINE=0 runs them here instead,
        // and BLUESTONE_ENGINE=1 uses the engine on any platform.
        if (UseEngineProcess(Environment.GetEnvironmentVariable("BLUESTONE_ENGINE")))
        {
            _engine = new EngineSupervisor(new EngineSupervisorOptions(), activities);
            _enginePlayback = new EnginePlayback(_engine, new EnginePlaybackOptions { IsEngineEndpoint = PlatformProviders.IsEngineEndpoint });
            playback = new PlaybackController(session, endpoints, profiles, _enginePlayback);
        }
        else
        {
            playback = new PlaybackController(session, endpoints, profiles, clock, playbackThreadSetup: PlatformProviders.ConfigurePlaybackThread, playbackPreciseSleep: PlatformProviders.PlaybackPreciseSleep);
        }

        var monitor = new MidiMonitor(await builtIn.OpenInputAsync(monitorPort.InputId));
        await Step(splash, "Scanning plugins…");
        await ScanPluginsAsync(library, splash);
        await Step(splash, "Preparing the workspace…");
        var hosting = new PluginHostingSettingsStore(PluginHostingSettingsPath);
        var devices = new PluginDeviceHost(plugins, session, library, hosting: hosting.Load());
        var dispatcher = new AvaloniaDispatcher();
        var viewModel = new MainViewModel(session, playback, endpoints, new DialogService(window), dispatcher, monitor, new ComputerKeyboardViewModel(keyboard), devices, activities)
        {
            Plugins = new PluginsViewModel(library, dispatcher, devices, hosting, activities),
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

    /// <summary>Whether the real-time work runs in the engine process: <paramref name="setting"/> "0" or "1", else by platform.</summary>
    internal static bool UseEngineProcess(string? setting) => setting switch
    {
        "0" => false,
        "1" => true,
        _ => OperatingSystem.IsMacOS(),
    };

    /// <summary>
    /// The audio engine (ADR 0032): off until the user starts it or the saved settings say it was on. With the engine
    /// process (BLUESTONE_ENGINE=1) the engine opens the device and renders; Bluestone describes the graph (ADR 0044).
    /// </summary>
    private static void ComposeAudio(MainWindow window, ProjectSession session, PlaybackController playback, PluginDeviceHost devices, MainViewModel viewModel)
    {
        // Plugins run at the device's rate: each time a device opens, they are restarted at its rate on the UI thread.
        Func<int, Task> prepare = rate => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => devices.SetSampleRateAsync(rate));
        IAudioPlayback audio = _engine is { } engine
            ? new EngineAudio(engine, playback, session, devices.InstanceOf, prepare)
            : new AudioPlayback(playback, session, devices.InstanceOf, prepareSampleRate: prepare);
        if (audio is EngineAudio engineAudio)
        {
            playback.AudioReady = engineAudio.WhenReadyAsync;
        }

        _audio = new AudioViewModel(audio, new AudioSettingsStore(AudioSettingsPath), new AvaloniaDispatcher(), activities: viewModel.Activities);
        viewModel.Mixer.MeterSource = audio.MeterOf;
        viewModel.JunctionLevelSource = audio.JunctionMeterOf;
        // In-process audio takes its graph lock to watch junctions, which a rebuild can hold: never wait for it on the UI thread.
        var watch = new LatestOnBackground<IReadOnlyCollection<Guid>>(audio.WatchJunctions);
        viewModel.WatchJunctions = watch.Post;
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
        if (_engine is { } engine)
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }

        await viewModel.DisposeAsync().ConfigureAwait(false);
        _enginePlayback?.Dispose();
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
        var cancellation = StartupCancellation.Token;
        _startupScan = Task.Run(
            () => library.ScanAsync(
                progress => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    splash?.Report(
                        progress.Total == 0 ? "Scanning plugins…" : $"Scanning plugins ({Math.Min(progress.Done + 1, progress.Total)} of {progress.Total})…",
                        progress.Module is { } module ? Path.GetFileName(module) : null,
                        progress.Total == 0 ? null : (double)progress.Done / progress.Total)),
                cancellation),
            cancellation);
        await _startupScan;
        foreach (var module in library.Modules.Where(m => m.State != PluginModuleState.Loaded))
        {
            System.Diagnostics.Trace.TraceWarning($"Plugin module {module.ModulePath} was not loaded: {module.Problem}");
        }
    }

    /// <summary>
    /// Cancels the start-up plugin scan and waits, at most <see cref="ShutdownLimit"/>, for it to end; ending disposes its
    /// scanner process. Runs on the UI thread while quitting; the scan runs on the thread pool, so waiting cannot deadlock.
    /// </summary>
    private static void StopStartupScan()
    {
        StartupCancellation.Cancel();
        try
        {
            if (_startupScan is { IsCompleted: false } scan && !scan.Wait(ShutdownLimit))
            {
                System.Diagnostics.Trace.TraceError($"The plugin scan did not stop within {ShutdownLimit.TotalSeconds:0} s.");
            }
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // Cancelled, as asked.
        }
#pragma warning disable CA1031 // Quitting must not crash; the failure is recorded instead.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceError($"Error while stopping the plugin scan: {ex}");
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
