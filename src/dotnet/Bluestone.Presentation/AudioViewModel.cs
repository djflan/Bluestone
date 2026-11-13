using System.Collections.ObjectModel;
using System.Globalization;
using Bluestone.Application.Audio;
using Bluestone.Application.Status;
using Bluestone.Audio;
using Bluestone.Audio.Devices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>
/// An audio device choice; <see cref="Id"/> is null for the system's default output. <see cref="DefaultSampleRate"/> is
/// the device's own rate (for WASAPI, the Windows mixer's), or 0 when unknown.
/// </summary>
public sealed record AudioDeviceOption(string? Id, string Name, IReadOnlyList<int> SampleRates, int MinBufferFrames = 0, int MaxBufferFrames = 0, int DefaultSampleRate = 0)
{
    public override string ToString() => Name;
}

public sealed record AudioHostOption(string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Audio settings and status (ADR 0032): the output device, sample rate, buffer and queue sizes, the engine on or
/// off, a test tone, and the master meter. Settings are saved when applied. Device and graph problems are shown as
/// text. Device lists are read when the view model is created and on <see cref="RefreshDevicesCommand"/>.
/// </summary>
public sealed partial class AudioViewModel : ObservableObject
{
    private static readonly int[] StandardRates = [44_100, 48_000, 88_200, 96_000];
    private static readonly int[] CommonBufferSizes = [32, 64, 128, 256, 512, 1024, 2048, 4096];
    private readonly IAudioPlayback _audio;
    private readonly int _otherQueuedFrames;
    private readonly int _otherLiveQueuedFrames;
    private readonly AudioSettingsStore _store;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<IReadOnlyList<AudioDeviceInfo>> _listDevices;
    private readonly Func<IReadOnlyList<string>> _listHosts;
    private readonly ActivityLog? _activities;
    private IReadOnlyList<AudioDeviceInfo> _lastListedDevices = [];
    private bool _hasListedDevices;
    private bool _loadingDevices;
    private RunningActivity? _waiting;
    private RunningActivity? _opening;

    [ObservableProperty]
    private AudioDeviceOption? _device;

    [ObservableProperty]
    private AudioHostOption? _host;

    [ObservableProperty]
    private int _sampleRate;

    [ObservableProperty]
    private int _bufferFrames;

    [ObservableProperty]
    private int _queuedFrames;

    [ObservableProperty]
    private int _liveQueuedFrames;

    [ObservableProperty]
    private bool _lowLatencyMonitoring;

    [ObservableProperty]
    private string _latency = string.Empty;

    [ObservableProperty]
    private string _status = "Audio is off.";

    [ObservableProperty]
    private string _problems = string.Empty;

    [ObservableProperty]
    private string _masterLevel = string.Empty;

    /// <summary>What the open device actually runs at, such as "48 kHz · 480 samples / 10.00 ms"; empty while audio is off.</summary>
    [ObservableProperty]
    private string _deviceFormat = string.Empty;

    /// <summary>Dropouts since the device opened, and how its callback thread is scheduled; empty while audio is off.</summary>
    [ObservableProperty]
    private string _health = string.Empty;

    /// <summary>The device dropped audio, or its callback thread runs at normal priority: <see cref="Health"/> is a warning.</summary>
    [ObservableProperty]
    private bool _hasDropouts;

    /// <param name="audio">The audio, in this process or in the engine process.</param>
    /// <param name="store">Where settings are saved.</param>
    /// <param name="dispatcher">For status changes reported on other threads.</param>
    /// <param name="listDevices">The output devices (the operating system's, or a test's).</param>
    /// <param name="activities">Where opening the device and waiting for a lost one are reported, for the main window's status line.</param>
    /// <param name="listHosts">The audio hosts available in this build, or a test's.</param>
    public AudioViewModel(IAudioPlayback audio, AudioSettingsStore store, IUiDispatcher dispatcher, Func<IReadOnlyList<AudioDeviceInfo>>? listDevices = null, ActivityLog? activities = null, Func<IReadOnlyList<string>>? listHosts = null)
    {
        _activities = activities;
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _listDevices = listDevices ?? (() => AudioDevices.List());
        _listHosts = listHosts ?? (listDevices is null
            ? () => AudioDevices.ListHosts()
            : () => _listDevices().Select(device => device.Host).Distinct(StringComparer.Ordinal).ToArray());
        var savedSettingsExist = File.Exists(store.Path);
        var settings = store.Load();
        _sampleRate = settings.SampleRate;
        _bufferFrames = settings.BufferFrames;
        // The queue shown is the one in use: the engine's own (0 by default, ADR 0044) when the engine renders.
        _queuedFrames = audio.RendersInEngine ? settings.EngineQueuedFrames : settings.QueuedFrames;
        _otherQueuedFrames = audio.RendersInEngine ? settings.QueuedFrames : settings.EngineQueuedFrames;
        _liveQueuedFrames = audio.RendersInEngine ? settings.EngineLiveQueuedFrames : settings.LiveQueuedFrames;
        _otherLiveQueuedFrames = audio.RendersInEngine ? settings.LiveQueuedFrames : settings.EngineLiveQueuedFrames;
        _lowLatencyMonitoring = settings.LowLatencyMonitoring;
        var initialDevices = _listDevices();
        var selectedHost = settings.Host;
        if (selectedHost is null && !savedSettingsExist && OperatingSystem.IsWindows() && initialDevices.Any(device => device.Host == "ASIO"))
        {
            selectedHost = "ASIO";
        }

        LoadDevices(selectedHost, settings.DeviceId, initialDevices);
        _audio.Changed += (_, _) => _dispatcher.Post(UpdateStatus);
        UpdateStatus();
    }

    public ObservableCollection<AudioHostOption> Hosts { get; } = [];

    public ObservableCollection<AudioDeviceOption> Devices { get; } = [];

    public ObservableCollection<int> SampleRates { get; } = [];

    public ObservableCollection<int> BufferSizes { get; } = [];

    /// <summary>
    /// The sample rate as the dropdown sees it. The choices are rebuilt when the device changes, which briefly leaves the
    /// dropdown with no selection; that empty selection is ignored rather than written to <see cref="SampleRate"/>.
    /// </summary>
    public int? SelectedSampleRate
    {
        get => SampleRates.Contains(SampleRate) ? SampleRate : null;
        set
        {
            if (value is { } rate)
            {
                SampleRate = rate;
            }
        }
    }

    /// <summary>The buffer size as the dropdown sees it; an empty selection while the choices are rebuilt is ignored.</summary>
    public int? SelectedBufferFrames
    {
        get => BufferSizes.Contains(BufferFrames) ? BufferFrames : null;
        set
        {
            if (value is { } frames)
            {
                BufferFrames = frames;
            }
        }
    }

    public IReadOnlyList<int> QueueSizes { get; } = AudioSettings.QueueSizes;

    public IReadOnlyList<int> LiveQueueSizes { get; } = AudioSettings.LiveQueueSizes;

    public bool IsRunning => _audio.IsRunning;

    public bool IsAsioHost => Host?.Name == "ASIO";

    /// <summary>
    /// Set when WASAPI shared mode will resample: a fixed rate that is not the rate Windows mixes the device at. Empty
    /// otherwise. (ASIO and Core Audio switch the device to the chosen rate instead.)
    /// </summary>
    public string ResampleWarning => Host?.Name == "WASAPI" && SampleRate != AudioSettings.Automatic && Device is { DefaultSampleRate: > 0 } device && device.DefaultSampleRate != SampleRate
        ? string.Create(CultureInfo.InvariantCulture, $"Windows mixes this device at {device.DefaultSampleRate:N0} Hz, so {SampleRate:N0} Hz is resampled. Automatic follows the device.")
        : string.Empty;

    /// <summary>A sample rate choice as the list shows it: "Automatic" for <see cref="AudioSettings.Automatic"/>.</summary>
    public static string RateLabel(int rate) => rate == AudioSettings.Automatic ? "Automatic" : string.Create(CultureInfo.InvariantCulture, $"{rate:N0} Hz");

    /// <summary>A block size choice as the list shows it: "Automatic" for <see cref="AudioSettings.Automatic"/>.</summary>
    public static string BlockLabel(int frames) => frames == AudioSettings.Automatic ? "Automatic" : string.Create(CultureInfo.InvariantCulture, $"{frames} samples");

    /// <summary>A device buffer as samples and duration, as Bitwig and most DAWs show it: "480 samples / 10.00 ms".</summary>
    public static string BufferDuration(int frames, int rate) =>
        string.Create(CultureInfo.InvariantCulture, $"{frames} samples / {frames * 1000.0 / rate:0.00} ms");

    public bool CanRefreshDevices => !_audio.RendersInEngine || !_audio.IsRunning;

    /// <summary>Plays a 440 Hz tone at −18 dBFS through the open device.</summary>
    public bool TestTone
    {
        get => _audio.TestTone;
        set
        {
            if (_audio.TestTone != value)
            {
                _audio.TestTone = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The settings as chosen in the view.</summary>
    public AudioSettings Settings => _audio.RendersInEngine
        ? new(IsRunning, Device?.Id, SampleRate, BufferFrames, _otherQueuedFrames, _otherLiveQueuedFrames, LowLatencyMonitoring) { Host = Host?.Name, EngineQueuedFrames = QueuedFrames, EngineLiveQueuedFrames = LiveQueuedFrames }
        : new(IsRunning, Device?.Id, SampleRate, BufferFrames, QueuedFrames, LiveQueuedFrames, LowLatencyMonitoring) { Host = Host?.Name, EngineQueuedFrames = _otherQueuedFrames, EngineLiveQueuedFrames = _otherLiveQueuedFrames };

    /// <summary>Whether the saved settings say audio was on, so <see cref="StartIfEnabled"/> will open a device.</summary>
    public bool IsEnabledInSettings => _store.Load().Enabled;

    /// <summary>Opens the device the saved settings name, when they say audio was on. For start-up.</summary>
    public void StartIfEnabled()
    {
        if (_store.Load().Enabled)
        {
            Open();
        }
    }

    /// <summary>Reads the master meter; call from a UI timer.</summary>
    public void RefreshMeters()
    {
        if (!_audio.IsRunning || _audio.MeterOf(null) is not { } reading)
        {
            MasterLevel = string.Empty;
            UpdateHealth();
            return;
        }

        MasterLevel = string.Create(CultureInfo.InvariantCulture, $"Master peak {Decibels(reading.PeakLeft)} / {Decibels(reading.PeakRight)} dBFS");
        Latency = LatencyText();
        UpdateHealth();
    }

    // Dropouts in words, and the callback thread's priority. A pop is one of: a callback the device delivered late (its
    // thread was not run in time), a render longer than its block, an underrun the driver reported, or (with
    // render-ahead) frames the queue did not have.
    private void UpdateHealth()
    {
        if (!_audio.IsRunning || _audio.Telemetry is not { } telemetry)
        {
            Health = string.Empty;
            HasDropouts = false;
            return;
        }

        var parts = new List<string>();
        if (telemetry.Discontinuities > 0)
        {
            parts.Add(Count(telemetry.Discontinuities, "late device callback"));
        }

        if (telemetry.LateRenders > 0)
        {
            parts.Add(Count(telemetry.LateRenders, "render longer than its block", "renders longer than their block"));
        }

        if (telemetry.Xruns > 0)
        {
            parts.Add(Count(telemetry.Xruns, "underrun reported by the driver", "underruns reported by the driver"));
        }

        if (telemetry.QueueUnderrunFrames > 0)
        {
            parts.Add(Count(telemetry.QueueUnderrunFrames, "frame missing from render-ahead", "frames missing from render-ahead"));
        }

        if (telemetry.PluginFailedBlocks > 0)
        {
            parts.Add(Count(telemetry.PluginFailedBlocks, "plugin block played as silence", "plugin blocks played as silence"));
        }

        if (telemetry.RenderFaults > 0)
        {
            parts.Add(Count(telemetry.RenderFaults, "failed render", "failed renders"));
        }

        var thread = telemetry.CallbackThreadPolicy switch
        {
            AudioCallbackThreadPolicy.MmcssProAudio => "audio thread at MMCSS \u201cPro Audio\u201d priority",
            AudioCallbackThreadPolicy.TimeCritical => "audio thread at time-critical priority (MMCSS refused)",
            AudioCallbackThreadPolicy.Normal => "audio thread at normal priority: Windows refused audio priority",
            AudioCallbackThreadPolicy.Backend => "audio thread run by the driver",
            _ => null,
        };
        var dropouts = parts.Count == 0 ? "No dropouts" : $"Dropouts: {string.Join(", ", parts)}";
        Health = thread is null ? $"{dropouts}." : $"{dropouts} \u00b7 {thread}.";
        HasDropouts = parts.Count > 0 || telemetry.CallbackThreadPolicy == AudioCallbackThreadPolicy.Normal;
    }

    private static string Count(long count, string one, string? many = null) =>
        string.Create(CultureInfo.InvariantCulture, $"{count:N0} {(count == 1 ? one : many ?? one + "s")}");

    // Delay compensation in the graph, the queue in use, and what live notes measured.
    private string LatencyText()
    {
        if (_audio.Stream is not { } stream)
        {
            return string.Empty;
        }

        var rate = (double)stream.SampleRate;
        var text = string.Create(CultureInfo.InvariantCulture, $"Delay compensation {stream.LatencyFrames / rate * 1000:0.#} ms; render-ahead {stream.QueuedFrames / rate * 1000:0.#} ms{(_audio.IsLiveQueue ? " (live)" : string.Empty)}.");
        var live = _audio.LiveLatency;
        if (live.Count > 0)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" Live notes heard after {live.Average.TotalMilliseconds:0.#} ms (min {live.Min.TotalMilliseconds:0.#}, max {live.Max.TotalMilliseconds:0.#}, {live.Count} notes).");
        }

        return text;
    }

    /// <summary>Opens (or reopens) the device with the chosen settings and saves them.</summary>
    [RelayCommand]
    private void Start()
    {
        var started = Open();

        // Waiting for an unplugged device, or for the engine to open it, still counts as on: it opens when it can, also
        // after a restart.
        _store.Save(Settings with { Enabled = started || _audio.IsWaitingForDevice || _audio.IsStarting });
        UpdateStatus();
    }

    // Opens the device as an activity: Messages records "Audio is on: …" or why it did not open. When the engine opens it,
    // nothing waits: the activity stays on the status line until the engine says what it opened (UpdateStatus ends it).
    private bool Open()
    {
        _opening?.Complete();
        _opening = _activities?.Begin("Audio", $"Opening {Device?.Name ?? "the audio device"}…");
        var started = _audio.Start(Settings with { Enabled = true });
        UpdateStatus();
        return started;
    }

    // The open in progress ends once the device is open, waited for, or refused.
    private void EndOpening()
    {
        if (_opening is not { } opening || _audio.IsStarting)
        {
            return;
        }

        _opening = null;
        if (_audio.IsRunning || _audio.IsWaitingForDevice)
        {
            opening.Complete(Status);
        }
        else
        {
            opening.Fail(_audio.Problem ?? "the device did not open.");
        }
    }

    /// <summary>Closes the device and saves that audio is off.</summary>
    [RelayCommand]
    private void Stop()
    {
        _opening?.Complete("Audio is off.");
        _opening = null;
        _audio.Stop();
        _store.Save(Settings with { Enabled = false });
        UpdateStatus();
    }

    [RelayCommand]
    private void RefreshDevices() => LoadDevices(Host?.Name, Device?.Id);

    partial void OnHostChanged(AudioHostOption? value)
    {
        if (!_loadingDevices && value is not null)
        {
            LoadDevices(value.Name, selectedDevice: null);
        }

        OnPropertyChanged(nameof(ResampleWarning));
    }

    partial void OnDeviceChanged(AudioDeviceOption? value)
    {
        SampleRates.Clear();
        SampleRates.Add(AudioSettings.Automatic);
        foreach (var rate in value?.SampleRates is { Count: > 0 } rates ? rates : StandardRates)
        {
            SampleRates.Add(rate);
        }

        if (!SampleRates.Contains(SampleRate))
        {
            SampleRate = AudioSettings.Automatic;
        }

        var minimum = value?.MinBufferFrames ?? 0;
        var maximum = value?.MaxBufferFrames ?? 0;
        var choices = CommonBufferSizes.Where(size => (minimum == 0 || size >= minimum) && (maximum == 0 || size <= maximum)).ToArray();
        if (choices.Length == 0)
        {
            choices = minimum > 0 ? [minimum] : [.. AudioSettings.BufferSizes];
        }

        BufferSizes.Clear();
        BufferSizes.Add(AudioSettings.Automatic);
        foreach (var size in choices)
        {
            BufferSizes.Add(size);
        }

        if (!BufferSizes.Contains(BufferFrames))
        {
            BufferFrames = choices.MinBy(size => Math.Abs((long)size - BufferFrames));
        }

        // Rebuilding the choices cleared the dropdowns' selections even where the value itself did not change.
        OnPropertyChanged(nameof(SelectedSampleRate));
        OnPropertyChanged(nameof(SelectedBufferFrames));
        OnPropertyChanged(nameof(IsAsioHost));
        OnPropertyChanged(nameof(ResampleWarning));
    }

    partial void OnSampleRateChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedSampleRate));
        OnPropertyChanged(nameof(ResampleWarning));
    }

    partial void OnBufferFramesChanged(int value) => OnPropertyChanged(nameof(SelectedBufferFrames));

    private void LoadDevices(string? selectedHost, string? selectedDevice, IReadOnlyList<AudioDeviceInfo>? initialDevices = null)
    {
        var rate = SampleRate;
        _loadingDevices = true;
        var reuseEngineSnapshot = _audio.RendersInEngine && _audio.IsRunning && _hasListedDevices;
        var listed = reuseEngineSnapshot ? _lastListedDevices : initialDevices ?? _listDevices();
        if (!reuseEngineSnapshot)
        {
            _lastListedDevices = listed;
            _hasListedDevices = true;
        }
        var hostNames = _listHosts().Concat(listed.Select(d => d.Host))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var firstListed = listed.Count > 0 ? listed[0] : null;
        var firstHost = hostNames.Count > 0 ? hostNames[0] : null;
        selectedHost ??= listed.FirstOrDefault(d => d.Id == selectedDevice)?.Host
            ?? (selectedDevice?.StartsWith("wasapi:", StringComparison.OrdinalIgnoreCase) == true ? "WASAPI" : null)
            ?? (selectedDevice?.StartsWith("asio:", StringComparison.OrdinalIgnoreCase) == true ? "ASIO" : null)
            ?? listed.FirstOrDefault(d => d.IsDefault)?.Host
            ?? firstListed?.Host
            ?? firstHost;
        if (selectedHost is not null && !hostNames.Contains(selectedHost, StringComparer.Ordinal))
        {
            hostNames.Add(selectedHost);
        }

        Hosts.Clear();
        foreach (var name in hostNames)
        {
            Hosts.Add(new AudioHostOption(name));
        }

        Host = Hosts.FirstOrDefault(h => h.Name == selectedHost) ?? Hosts.FirstOrDefault();
        var hostDevices = listed.Where(d => d.Host == Host?.Name).ToArray();
        var fallback = hostDevices.FirstOrDefault(d => d.IsDefault)?.SampleRates.ToList()
            ?? hostDevices.FirstOrDefault()?.SampleRates.ToList()
            ?? [.. StandardRates];
        var defaultDevice = hostDevices.FirstOrDefault(d => d.IsDefault);
        Devices.Clear();
        Devices.Add(new AudioDeviceOption(null, "System default output", fallback, defaultDevice?.MinBufferFrames ?? 0, defaultDevice?.MaxBufferFrames ?? 0, defaultDevice?.DefaultSampleRate ?? 0));
        foreach (var device in hostDevices)
        {
            Devices.Add(new AudioDeviceOption(device.Id, $"{device.Name} ({device.Host})", [.. device.SampleRates], device.MinBufferFrames, device.MaxBufferFrames, device.DefaultSampleRate));
        }

        if (selectedDevice is not null && Devices.All(d => d.Id != selectedDevice))
        {
            Devices.Add(new AudioDeviceOption(selectedDevice, $"{selectedDevice} (not connected)", [.. StandardRates]));
        }

        Device = Devices.First(d => d.Id == selectedDevice);
        _loadingDevices = false;
        SampleRate = rate;
        OnDeviceChanged(Device);
    }

    private void UpdateStatus()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanRefreshDevices));
        if (_audio.Stream is null)
        {
            DeviceFormat = string.Empty;
            UpdateHealth();
        }

        if (_audio.Stream is { } stream)
        {
            var queue = stream.QueuedFrames > 0 ? string.Create(CultureInfo.InvariantCulture, $", {stream.QueuedFrames * 1000.0 / stream.SampleRate:0.#} ms render-ahead") : string.Empty;
            if (stream.RenderThreadPolicy is { } policy)
            {
                queue += $" ({policy})";
            }

            var where = _audio.RendersInEngine ? " in the engine" : string.Empty;
            var deviceBuffer = stream.DeviceBufferFrames > 0 ? $", {stream.DeviceBufferFrames}-frame device buffer" : string.Empty;
            Status = string.Create(CultureInfo.InvariantCulture, $"Audio is on{where}: {stream.SampleRate} Hz, {stream.Channels} channels{deviceBuffer}, {stream.BlockFrames}-frame render blocks{queue}.");
            DeviceFormat = stream.DeviceBufferFrames > 0
                ? string.Create(CultureInfo.InvariantCulture, $"Running at {stream.SampleRate / 1000.0:0.###} kHz \u00b7 {BufferDuration(stream.DeviceBufferFrames, stream.SampleRate)}")
                : string.Create(CultureInfo.InvariantCulture, $"Running at {stream.SampleRate / 1000.0:0.###} kHz");
            if (_audio.Problem is { } fallback)
            {
                Status += $" {fallback}";
            }
        }
        else if (_audio.IsStarting)
        {
            Status = "Audio is starting in the engine…";
        }
        else if (_audio.IsWaitingForDevice)
        {
            Status = $"Audio is waiting for its device: {_audio.Problem}";
        }
        else
        {
            Status = _audio.Problem is { } problem ? $"Audio is off: {problem}" : "Audio is off.";
        }

        Problems = string.Join(Environment.NewLine, _audio.GraphProblems);
        EndOpening();

        // A lost device is something Bluestone is doing: waiting for it to come back. The status line shows it until it
        // returns or audio is turned off.
        if (_audio.IsWaitingForDevice && _waiting is null)
        {
            _waiting = _activities?.Begin("Audio", "Waiting for the audio device to return…");
        }
        else if (!_audio.IsWaitingForDevice && _waiting is { } waiting)
        {
            _waiting = null;
            waiting.Complete(Status);
        }
    }

    private static string Decibels(float linear)
    {
        var db = MeterReading.ToDecibels(linear);
        return double.IsNegativeInfinity(db) || db < -99 ? "-inf" : db.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
