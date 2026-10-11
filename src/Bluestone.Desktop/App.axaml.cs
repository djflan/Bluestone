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
            var window = new MainWindow();
            var platformProviders = PlatformProviders.Create(SystemMonotonicClock.Instance).ToList();
            var plugins = new PluginHostManager(new PluginHostOptions { DataDirectory = Path.Combine(Path.GetTempPath(), $"bluestone-plugins-{Environment.ProcessId}") });
            var scanner = new PluginScanner(new PluginScannerOptions(PluginCacheDirectory));
            var library = new PluginLibrary(scanner, PluginFolders);
            var viewModel = Compose(window, platformProviders, plugins, library);
            window.Attach(viewModel);
            _audio?.StartIfEnabled();
            desktop.MainWindow = window;
            desktop.Exit += (_, _) =>
            {
                Shutdown(viewModel, platformProviders, plugins);
                library.Dispose();
                scanner.Dispose();
            };

            // A project or MIDI file named on the command line (or by the OS file association) opens at start.
            if (desktop.Args is [var path, ..] && MainViewModel.CanOpenFile(path))
            {
                window.Opened += async (_, _) => await viewModel.OpenFileAsync(path);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The composition root: the only place that chooses concrete providers and services.</summary>
    public static MainViewModel Compose(MainWindow window, IEnumerable<IMidiEndpointProvider> platformProviders, PluginHostManager plugins, PluginLibrary library)
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

        var profiles = ProfileCatalog.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "profiles"))
            .AddDirectory(UserProfileDirectory);

        var session = new ProjectSession();
        var playback = new PlaybackController(session, endpoints, profiles, clock, playbackThreadSetup: PlatformProviders.ConfigurePlaybackThread);
        var monitor = new MidiMonitor(builtIn.OpenInputAsync(monitorPort.InputId).AsTask().GetAwaiter().GetResult());
        ScanPlugins(library);
        var devices = new PluginDeviceHost(plugins, session, library);
        var dispatcher = new AvaloniaDispatcher();
        var viewModel = new MainViewModel(session, playback, endpoints, new DialogService(window), dispatcher, monitor, new ComputerKeyboardViewModel(keyboard), devices)
        {
            Plugins = new PluginsViewModel(library, dispatcher),
        };
        _ = viewModel.InitializeAsync();
        ComposeAudio(window, session, playback, devices);
        return viewModel;
    }

    /// <summary>The audio engine (ADR 0032): off until the user starts it or the saved settings say it was on.</summary>
    private static void ComposeAudio(MainWindow window, ProjectSession session, PlaybackController playback, PluginDeviceHost devices)
    {
        var audio = new AudioPlayback(playback, session, devices.InstanceOf);
        _audio = new AudioViewModel(audio, new AudioSettingsStore(AudioSettingsPath), new AvaloniaDispatcher());
        window.AttachAudio(_audio);
        window.Closed += (_, _) => audio.Dispose();
    }

    /// <summary>Where the audio settings are kept, e.g. ~/Library/Application Support/Bluestone/audio-settings.json on macOS.</summary>
    public static string AudioSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bluestone", "audio-settings.json");

    /// <summary>
    /// Stops playback (releasing sounding notes), closes MIDI ports, and releases platform providers.
    /// Errors are logged rather than thrown: nothing useful can be done with them while quitting.
    /// </summary>
    private static void Shutdown(MainViewModel viewModel, IEnumerable<IMidiEndpointProvider> platformProviders, PluginHostManager plugins)
    {
        try
        {
            viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
            plugins.DisposeAsync().AsTask().GetAwaiter().GetResult();
            foreach (var provider in platformProviders)
            {
                provider.Dispose();
            }
        }
#pragma warning disable CA1031 // Quitting must not crash; the failure is recorded instead.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceError($"Error while shutting down: {ex}");
        }
    }

    /// <summary>
    /// Scans the plugin folders at start. Each module is scanned in its own scanner process, so a module that crashes the
    /// scanner cannot take Bluestone with it; it is reported and skipped on later scans until retried from the plugins
    /// view (ADR 0025).
    /// </summary>
    private static void ScanPlugins(PluginLibrary library)
    {
        Task.Run(() => library.ScanAsync()).GetAwaiter().GetResult();
        foreach (var module in library.Modules.Where(m => m.State != PluginModuleState.Loaded))
        {
            System.Diagnostics.Trace.TraceWarning($"Plugin module {module.ModulePath} was not loaded: {module.Problem}");
        }
    }

    /// <summary>The folders scanned for plugin modules, in order. Formats with OS-standard folders add theirs here.</summary>
    public static IReadOnlyList<string> PluginFolders => [UserPluginDirectory];

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
