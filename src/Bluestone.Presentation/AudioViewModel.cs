using System.Collections.ObjectModel;
using System.Globalization;
using Bluestone.Application.Audio;
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
    private readonly AudioPlayback _audio;
    private readonly AudioSettingsStore _store;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<IReadOnlyList<AudioDeviceInfo>> _listDevices;

    [ObservableProperty]
    private AudioDeviceOption? _device;

    [ObservableProperty]
    private int _sampleRate;

    [ObservableProperty]
    private int _bufferFrames;

    [ObservableProperty]
    private int _queuedFrames;

    [ObservableProperty]
    private string _status = "Audio is off.";

    [ObservableProperty]
    private string _problems = string.Empty;

    [ObservableProperty]
    private string _masterLevel = string.Empty;

    /// <param name="audio">The engine.</param>
    /// <param name="store">Where settings are saved.</param>
    /// <param name="dispatcher">For status changes reported on other threads.</param>
    /// <param name="listDevices">The output devices (the operating system's, or a test's).</param>
    public AudioViewModel(AudioPlayback audio, AudioSettingsStore store, IUiDispatcher dispatcher, Func<IReadOnlyList<AudioDeviceInfo>>? listDevices = null)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _listDevices = listDevices ?? (() => AudioDevices.List());
        var settings = store.Load();
        _sampleRate = settings.SampleRate;
        _bufferFrames = settings.BufferFrames;
        _queuedFrames = settings.QueuedFrames;
        LoadDevices(settings.DeviceId);
        _audio.Changed += (_, _) => _dispatcher.Post(UpdateStatus);
        UpdateStatus();
    }

    public ObservableCollection<AudioDeviceOption> Devices { get; } = [];

    public ObservableCollection<int> SampleRates { get; } = [];

    public IReadOnlyList<int> BufferSizes { get; } = AudioSettings.BufferSizes;

    public IReadOnlyList<int> QueueSizes { get; } = AudioSettings.QueueSizes;

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
    public AudioSettings Settings => new(IsRunning, Device?.Id, SampleRate, BufferFrames, QueuedFrames);

    /// <summary>Opens the device the saved settings name, when they say audio was on. For start-up.</summary>
    public void StartIfEnabled()
    {
        if (_store.Load().Enabled)
        {
            _audio.Start(Settings with { Enabled = true });
            UpdateStatus();
        }
    }

    /// <summary>Reads the master meter; call from a UI timer.</summary>
    public void RefreshMeters()
    {
        if (_audio.Graph is not { } graph || !_audio.IsRunning)
        {
            MasterLevel = string.Empty;
            return;
        }

        var reading = graph.Master.Meter.Read();
        MasterLevel = string.Create(CultureInfo.InvariantCulture, $"Master peak {Decibels(reading.PeakLeft)} / {Decibels(reading.PeakRight)} dBFS");
    }

    /// <summary>Opens (or reopens) the device with the chosen settings and saves them.</summary>
    [RelayCommand]
    private void Start()
    {
        var started = _audio.Start(Settings with { Enabled = true });

        // Waiting for an unplugged device still counts as on: it opens when it returns, also after a restart.
        _store.Save(Settings with { Enabled = started || _audio.IsWaitingForDevice });
        UpdateStatus();
    }

    /// <summary>Closes the device and saves that audio is off.</summary>
    [RelayCommand]
    private void Stop()
    {
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
        if (_audio.Output is { } output && _audio.Engine is { } engine)
        {
            var queue = output.QueuedFrames > 0 ? string.Create(CultureInfo.InvariantCulture, $", {output.QueuedFrames * 1000.0 / output.SampleRate:0.#} ms render-ahead") : string.Empty;
            if (output.RenderThreadPolicy is { } policy)
            {
                queue += $" ({policy})";
            }

            Status = string.Create(CultureInfo.InvariantCulture, $"Audio is on: {output.SampleRate} Hz, {output.Channels} channels, {engine.BlockFrames}-frame blocks{queue}.");
            if (_audio.Problem is { } fallback)
            {
                Status += $" {fallback}";
            }
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
    }

    private static string Decibels(float linear)
    {
        var db = MeterReading.ToDecibels(linear);
        return double.IsNegativeInfinity(db) || db < -99 ? "-inf" : db.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
