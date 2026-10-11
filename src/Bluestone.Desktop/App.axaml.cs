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
using Bluestone.Plugins.Protocol;
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
            var viewModel = Compose(window, platformProviders, plugins);
            window.Attach(viewModel);
            _audio?.StartIfEnabled();
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => Shutdown(viewModel, platformProviders, plugins);

            // A project or MIDI file named on the command line (or by the OS file association) opens at start.
            if (desktop.Args is [var path, ..] && MainViewModel.CanOpenFile(path))
            {
                window.Opened += async (_, _) => await viewModel.OpenFileAsync(path);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The composition root: the only place that chooses concrete providers and services.</summary>
    public static MainViewModel Compose(MainWindow window, IEnumerable<IMidiEndpointProvider> platformProviders, PluginHostManager plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);
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
        var devices = new PluginDeviceHost(plugins, session, ScanPlugins());
        var viewModel = new MainViewModel(session, playback, endpoints, new DialogService(window), new AvaloniaDispatcher(), monitor, new ComputerKeyboardViewModel(keyboard), devices);
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
    /// The plugins in <see cref="UserPluginDirectory"/>. Each module is scanned in its own scanner process, so a module
    /// that crashes the scanner cannot take Bluestone with it; it is reported and skipped on later scans (ADR 0025).
    /// </summary>
    private static IReadOnlyList<PluginIdentity> ScanPlugins()
    {
        if (!Directory.Exists(UserPluginDirectory))
        {
            return [];
        }

        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bluestone", "plugin-cache");
        using var scanner = new PluginScanner(new PluginScannerOptions(cache));
        var results = Task.Run(() => scanner.ScanAsync(Directory.EnumerateFiles(UserPluginDirectory))).GetAwaiter().GetResult();
        foreach (var failure in results.Where(r => r.Failure is not null))
        {
            System.Diagnostics.Trace.TraceWarning($"Plugin module {failure.ModulePath} was not loaded: {failure.Failure}");
        }

        return [.. results.SelectMany(r => r.Plugins)];
    }

    /// <summary>Where users put plugin modules, e.g. ~/Library/Application Support/Bluestone/plugins on macOS.</summary>
    public static string UserPluginDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bluestone", "plugins");

    /// <summary>Where users put their own profiles, e.g. ~/Library/Application Support/Bluestone/profiles on macOS.</summary>
    public static string UserProfileDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bluestone", "profiles");
}
