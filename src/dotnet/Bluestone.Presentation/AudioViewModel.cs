using System.Collections.ObjectModel;
using System.Globalization;
using Bluestone.Application.Audio;
using Bluestone.Application.Status;
using Bluestone.Audio;
using Bluestone.Audio.Devices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>An audio device choice; <see cref="Id"/> is null for the system's default output.</summary>
public sealed record AudioDeviceOption(string? Id, string Name, IReadOnlyList<int> SampleRates)
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
    private readonly IAudioPlayback _audio;
    private readonly int _otherQueuedFrames;
    private readonly int _otherLiveQueuedFrames;
    private readonly AudioSettingsStore _store;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<IReadOnlyList<AudioDeviceInfo>> _listDevices;
    private readonly ActivityLog? _activities;
    private RunningActivity? _waiting;
    private RunningActivity? _opening;

    [ObservableProperty]
    private AudioDeviceOption? _device;

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

    /// <param name="audio">The audio, in this process or in the engine process.</param>
    /// <param name="store">Where settings are saved.</param>
    /// <param name="dispatcher">For status changes reported on other threads.</param>
    /// <param name="listDevices">The output devices (the operating system's, or a test's).</param>
    /// <param name="activities">Where opening the device and waiting for a lost one are reported, for the main window's status line.</param>
    public AudioViewModel(IAudioPlayback audio, AudioSettingsStore store, IUiDispatcher dispatcher, Func<IReadOnlyList<AudioDeviceInfo>>? listDevices = null, ActivityLog? activities = null)
    {
        _activities = activities;
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _listDevices = listDevices ?? (() => AudioDevices.List());
        var settings = store.Load();
        _sampleRate = settings.SampleRate;
        _bufferFrames = settings.BufferFrames;
        // The queue shown is the one in use: the engine's own (0 by default, ADR 0044) when the engine renders.
        _queuedFrames = audio.RendersInEngine ? settings.EngineQueuedFrames : settings.QueuedFrames;
        _otherQueuedFrames = audio.RendersInEngine ? settings.QueuedFrames : settings.EngineQueuedFrames;
        _liveQueuedFrames = audio.RendersInEngine ? settings.EngineLiveQueuedFrames : settings.LiveQueuedFrames;
        _otherLiveQueuedFrames = audio.RendersInEngine ? settings.LiveQueuedFrames : settings.EngineLiveQueuedFrames;
        _lowLatencyMonitoring = settings.LowLatencyMonitoring;
        LoadDevices(settings.DeviceId);
        _audio.Changed += (_, _) => _dispatcher.Post(UpdateStatus);
        UpdateStatus();
    }

    public ObservableCollection<AudioDeviceOption> Devices { get; } = [];

    public ObservableCollection<int> SampleRates { get; } = [];

    public IReadOnlyList<int> BufferSizes { get; } = AudioSettings.BufferSizes;

    public IReadOnlyList<int> QueueSizes { get; } = AudioSettings.QueueSizes;

    public IReadOnlyList<int> LiveQueueSizes { get; } = AudioSettings.LiveQueueSizes;

    public bool IsRunning => _audio.IsRunning;

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
        ? new(IsRunning, Device?.Id, SampleRate, BufferFrames, _otherQueuedFrames, _otherLiveQueuedFrames, LowLatencyMonitoring) { EngineQueuedFrames = QueuedFrames, EngineLiveQueuedFrames = LiveQueuedFrames }
        : new(IsRunning, Device?.Id, SampleRate, BufferFrames, QueuedFrames, LiveQueuedFrames, LowLatencyMonitoring) { EngineQueuedFrames = _otherQueuedFrames, EngineLiveQueuedFrames = _otherLiveQueuedFrames };

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
            return;
        }

        MasterLevel = string.Create(CultureInfo.InvariantCulture, $"Master peak {Decibels(reading.PeakLeft)} / {Decibels(reading.PeakRight)} dBFS");
        Latency = LatencyText();
    }

    // Delay compensation in the graph, the queue in use, and what live notes measured.
    private string LatencyText()
    {
        if (_audio.Stream is not { } stream)
        {
            return string.Empty;
        }

        var rate = (double)stream.SampleRate;
        var text = string.Create(CultureInfo.InvariantCulture, $"Delay compensation {stream.LatencyFrames / rate * 1000:0.#} ms; render-ahead {stream.QueuedFrames / rate * 1000:0.#} ms{(_audio.IsLiveQueue ? " (live)" : string.Empty)}.");
        if (_audio.Telemetry is { } telemetry && (telemetry.Xruns > 0 || telemetry.QueueUnderrunFrames > 0))
        {
            text += string.Create(CultureInfo.InvariantCulture, $" {telemetry.Xruns} dropouts reported by the device, {telemetry.QueueUnderrunFrames} frames the render-ahead queue missed.");
        }

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
    private void RefreshDevices() => LoadDevices(Device?.Id);

    partial void OnDeviceChanged(AudioDeviceOption? value)
    {
        SampleRates.Clear();
        foreach (var rate in value?.SampleRates is { Count: > 0 } rates ? rates : StandardRates)
        {
            SampleRates.Add(rate);
        }

        if (!SampleRates.Contains(SampleRate) && SampleRates.Count > 0)
        {
            SampleRate = SampleRates.Contains(48_000) ? 48_000 : SampleRates[0];
        }
    }

    private void LoadDevices(string? selected)
    {
        var rate = SampleRate;
        Devices.Clear();
        var listed = _listDevices();
        var fallback = listed.FirstOrDefault(d => d.IsDefault)?.SampleRates.ToList() ?? [.. StandardRates];
        Devices.Add(new AudioDeviceOption(null, "System default output", fallback));
        foreach (var device in listed)
        {
            Devices.Add(new AudioDeviceOption(device.Id, $"{device.Name} ({device.Host})", [.. device.SampleRates]));
        }

        if (selected is not null && Devices.All(d => d.Id != selected))
        {
            // A remembered device that is not connected stays visible, so the choice is not lost.
            Devices.Add(new AudioDeviceOption(selected, $"{selected} (not connected)", [.. StandardRates]));
        }

        Device = Devices.First(d => d.Id == selected);
        SampleRate = rate;
        OnDeviceChanged(Device);
    }

    private void UpdateStatus()
    {
        OnPropertyChanged(nameof(IsRunning));
        if (_audio.Stream is { } stream)
        {
            var queue = stream.QueuedFrames > 0 ? string.Create(CultureInfo.InvariantCulture, $", {stream.QueuedFrames * 1000.0 / stream.SampleRate:0.#} ms render-ahead") : string.Empty;
            if (stream.RenderThreadPolicy is { } policy)
            {
                queue += $" ({policy})";
            }

            var where = _audio.RendersInEngine ? " in the engine" : string.Empty;
            Status = string.Create(CultureInfo.InvariantCulture, $"Audio is on{where}: {stream.SampleRate} Hz, {stream.Channels} channels, {stream.BlockFrames}-frame blocks{queue}.");
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
