using System.Collections.Immutable;
using Bluestone.Application.Audio;
using Bluestone.Application.Sessions;
using Bluestone.Application.Status;
using Bluestone.Audio.Devices;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Presentation;
using Bluestone.Profiles;
using Bluestone.Tests.Unit.Application;

namespace Bluestone.Tests.Unit.Presentation;

/// <summary>The audio settings view model with a fixed device list; no device is opened.</summary>
public sealed class AudioViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bluestone-audio-settings", Guid.NewGuid().ToString("N"));
    private readonly EndpointDirectory _endpoints = new([]);
    private readonly PlaybackController _playback;
    private readonly AudioPlayback _audio;

    public AudioViewModelTests()
    {
        var session = new ProjectSession();
        _playback = new PlaybackController(session, _endpoints, ProfileCatalog.Empty, new VirtualClock(), startThread: false);
        _audio = new AudioPlayback(_playback, session, _ => null);
    }

    private AudioSettingsStore Store => new(Path.Combine(_directory, "audio-settings.json"));

    public void Dispose()
    {
        _audio.Dispose();
        _playback.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _endpoints.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Devices_StartWithTheSystemDefault_AndTheRatesFollowTheChosenDevice()
    {
        var vm = new AudioViewModel(_audio, Store, new Immediate(), () => [Device("a", "Interface", 44_100, 96_000), Device("b", "Headset", 48_000)]);

        Assert.Equal(["System default output", "Interface (CoreAudio)", "Headset (CoreAudio)"], vm.Devices.Select(d => d.Name));
        Assert.Null(vm.Device!.Id);

        vm.Device = vm.Devices[1];
        Assert.Equal([AudioSettings.Automatic, 44_100, 96_000], vm.SampleRates);
        Assert.Equal(AudioSettings.Automatic, vm.SampleRate);
        Assert.Equal(AudioSettings.Automatic, vm.BufferSizes[0]);
        Assert.False(vm.IsRunning);
        Assert.Equal("Audio is off.", vm.Status);
    }

    [Fact]
    public void ARememberedDevice_ThatIsNotConnected_StaysSelectable()
    {
        Store.Save(new AudioSettings(DeviceId: "gone", SampleRate: 96_000, BufferFrames: 128, QueuedFrames: 512));

        var vm = new AudioViewModel(_audio, Store, new Immediate(), () => [Device("a", "Interface", 48_000)]);

        Assert.Equal("gone (not connected)", vm.Device!.Name);
        Assert.Equal((96_000, 128, 512), (vm.SampleRate, vm.BufferFrames, vm.QueuedFrames));
    }

    [Fact]
    public void Stop_SavesTheChoices_WithAudioOff()
    {
        var vm = new AudioViewModel(_audio, Store, new Immediate(), () => [Device("a", "Interface", 48_000)]);
        vm.Device = vm.Devices[1];
        vm.BufferFrames = 512;

        vm.StopCommand.Execute(null);

        Assert.Equal(new AudioSettings(false, "a", AudioSettings.Automatic, 512, AudioSettings.DefaultQueuedFrames) { Host = "CoreAudio" }, Store.Load());
    }

    [Fact]
    public void HostSelection_FiltersDevicesAndIsSaved()
    {
        Store.Save(new AudioSettings { Host = "ASIO" });
        var devices = new[]
        {
            new AudioDeviceInfo("asio-device", "Interface", "ASIO", true, 48_000, 2, [44_100, 48_000], [2], 32, 1024),
            new AudioDeviceInfo("wasapi-device", "Speakers", "WASAPI", true, 48_000, 2, [48_000], [2], 64, 4096),
        };
        var vm = new AudioViewModel(_audio, Store, new Immediate(), () => devices, listHosts: () => ["ASIO", "WASAPI"]);

        Assert.Equal("ASIO", vm.Host!.Name);
        Assert.Contains(vm.Devices, device => device.Id == "asio-device");
        Assert.DoesNotContain(vm.Devices, device => device.Id == "wasapi-device");

        vm.Host = vm.Hosts.Single(host => host.Name == "WASAPI");
        Assert.Contains(64, vm.BufferSizes);
        Assert.Contains(4096, vm.BufferSizes);
        vm.StopCommand.Execute(null);

        Assert.Equal("WASAPI", Store.Load().Host);
        Assert.Contains(vm.Devices, device => device.Id == "wasapi-device");
        Assert.DoesNotContain(vm.Devices, device => device.Id == "asio-device");
    }

    [Fact]
    public void FirstRun_EnablesTheSystemDefaultEvenWhenASIOIsAvailable()
    {
        var devices = new[]
        {
            new AudioDeviceInfo("asio-device", "Interface", "ASIO", false, 48_000, 2, [48_000], [2], 1024, 1024),
            new AudioDeviceInfo("wasapi-device", "Speakers", "WASAPI", true, 48_000, 2, [48_000], [2], 64, 4096),
        };

        var vm = new AudioViewModel(_audio, Store, new Immediate(), () => devices, listHosts: () => ["ASIO", "WASAPI"]);

        Assert.Equal("WASAPI", vm.Host!.Name);
        Assert.Null(vm.Device!.Id);
        Assert.True(Store.Load().Enabled);
    }

    [Fact]
    public void SavedAudioOff_RemainsOff()
    {
        Store.Save(new AudioSettings(Enabled: false));

        var vm = new AudioViewModel(_audio, Store, new Immediate(), () => [Device("a", "Interface", 48_000)]);
        vm.StartIfEnabled();

        Assert.False(Store.Load().Enabled);
        Assert.False(_audio.IsWaitingForDevice);
    }

    [Fact]
    public void FirstRun_WhenSettingsCannotBeSaved_StillStartsAndReportsTheProblem()
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(Store.Path + ".tmp");
        var backend = new AudioPlaybackRecoveryTests.FakeBackend();
        using var audio = new AudioPlayback(_playback, new ProjectSession(), _ => null, backend: backend, checkInterval: Timeout.InfiniteTimeSpan);
        var activities = new ActivityLog();
        var ended = new List<ActivityEndedEventArgs>();
        activities.Ended += (_, e) => ended.Add(e);

        var vm = new AudioViewModel(audio, Store, new Immediate(), backend.ListDevices, activities);

        Assert.True(vm.IsEnabledInSettings);
        Assert.Contains("Audio settings could not be saved", vm.Problems, StringComparison.Ordinal);
        Assert.Equal(ActivityOutcome.Warning, Assert.Single(ended).Outcome);
        vm.StartIfEnabled();
        Assert.True(audio.IsWaitingForDevice);

        vm.StopCommand.Execute(null);
        Assert.False(audio.IsWaitingForDevice);
        Assert.False(vm.IsEnabledInSettings);

        Directory.Delete(Store.Path + ".tmp");
        vm.StartCommand.Execute(null);
        Assert.True(Store.Load().Enabled);
        Assert.Empty(vm.Problems);
        vm.StopCommand.Execute(null);
        Assert.False(Store.Load().Enabled);
    }

    [Fact]
    public void FirstRun_WhenTheSettingsDirectoryCannotBeCreated_KeepsTheEnabledIntent()
    {
        Directory.CreateDirectory(_directory);
        var parent = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(parent, "occupied");
        var store = new AudioSettingsStore(Path.Combine(parent, "audio-settings.json"));

        var vm = new AudioViewModel(_audio, store, new Immediate(), () => []);

        Assert.True(vm.IsEnabledInSettings);
        Assert.Contains("Audio settings could not be saved", vm.Problems, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedSave_KeepsTheNewAudioIntentUntilItCanBePersisted(bool previouslyEnabled)
    {
        Store.Save(new AudioSettings(Enabled: previouslyEnabled));
        Directory.CreateDirectory(Store.Path + ".tmp");
        var backend = new AudioPlaybackRecoveryTests.FakeBackend();
        using var audio = new AudioPlayback(_playback, new ProjectSession(), _ => null, backend: backend, checkInterval: Timeout.InfiniteTimeSpan);
        var vm = new AudioViewModel(audio, Store, new Immediate(), backend.ListDevices);

        if (previouslyEnabled)
        {
            vm.StopCommand.Execute(null);
        }
        else
        {
            vm.StartCommand.Execute(null);
        }

        Assert.Equal(!previouslyEnabled, vm.IsEnabledInSettings);
        Assert.Equal(previouslyEnabled, Store.Load().Enabled);
        Assert.Contains("Audio settings could not be saved", vm.Problems, StringComparison.Ordinal);

        Directory.Delete(Store.Path + ".tmp");
        if (previouslyEnabled)
        {
            vm.StopCommand.Execute(null);
        }
        else
        {
            vm.StartCommand.Execute(null);
        }

        Assert.Equal(!previouslyEnabled, Store.Load().Enabled);
        Assert.Empty(vm.Problems);
    }

    [Fact]
    public void UnreadableSettings_AreNotTreatedAsAFirstRun()
    {
        Directory.CreateDirectory(Store.Path);

        var vm = new AudioViewModel(_audio, Store, new Immediate(), () => []);
        vm.StartIfEnabled();

        Assert.False(vm.IsEnabledInSettings);
        Assert.False(_audio.IsWaitingForDevice);
        Assert.Contains("Audio settings could not be read", vm.Problems, StringComparison.Ordinal);
        Assert.False(File.Exists(Store.Path + ".tmp"));
    }

    [Fact]
    public void ExistingSystemDefaultSettings_StayOnWASAPIWhenASIOIsInstalled()
    {
        Store.Save(new AudioSettings());
        var devices = new[]
        {
            new AudioDeviceInfo("asio-device", "Interface", "ASIO", false, 48_000, 2, [48_000], [2], 1024, 1024),
            new AudioDeviceInfo("wasapi-device", "Speakers", "WASAPI", true, 48_000, 2, [48_000], [2], 64, 4096),
        };

        var vm = new AudioViewModel(_audio, Store, new Immediate(), () => devices, listHosts: () => ["ASIO", "WASAPI"]);

        Assert.Equal("WASAPI", vm.Host!.Name);
    }

    [Fact]
    public void ARememberedDevice_WithoutASavedHost_KeepsItsHost()
    {
        Store.Save(new AudioSettings(DeviceId: "wasapi:speakers"));
        var devices = new[]
        {
            new AudioDeviceInfo("asio:interface", "Interface", "ASIO", true, 48_000, 2, [48_000], [2], 1024, 1024),
            new AudioDeviceInfo("wasapi:speakers", "Speakers", "WASAPI", true, 48_000, 2, [48_000], [2], 64, 4096),
        };

        var vm = new AudioViewModel(_audio, Store, new Immediate(), () => devices, listHosts: () => ["ASIO", "WASAPI"]);

        Assert.Equal("WASAPI", vm.Host!.Name);
        Assert.Equal("wasapi:speakers", vm.Device!.Id);
    }

    [Fact]
    public void AnUnreadableSettingsFile_GivesTheDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Store.Path, "{ not json");

        Assert.Equal(new AudioSettings(), Store.Load());
    }

    [Fact]
    public async Task OpeningTheDevice_AndWaitingForALostOne_AreReportedAsActivities()
    {
        var backend = new AudioPlaybackRecoveryTests.FakeBackend();
        var session = new ProjectSession();
        await using var playback = new PlaybackController(session, _endpoints, ProfileCatalog.Empty, new VirtualClock(), startThread: false);
        using var audio = new AudioPlayback(playback, session, _ => null, backend: backend, checkInterval: Timeout.InfiniteTimeSpan);
        var activities = new ActivityLog();
        var ended = new List<ActivityEndedEventArgs>();
        activities.Ended += (_, e) => ended.Add(e);
        var vm = new AudioViewModel(audio, Store, new Immediate(), backend.ListDevices, activities);

        // Nothing to open: the start is recorded, and Bluestone shows that it waits for a device.
        vm.StartCommand.Execute(null);

        Assert.Equal("Opening System default output…", ended[0].Activity.Text);
        Assert.StartsWith("Audio is waiting for its device", ended[0].Text, StringComparison.Ordinal);
        Assert.Equal("Waiting for the audio device to return…", Assert.Single(activities.Running).Text);

        backend.Devices.Add(new AudioDeviceInfo("built-in", "Speakers", "Test", true, 48_000, 2, [48_000], [2], 64, 4096));
        audio.CheckDevice();

        Assert.Empty(activities.Running);
        Assert.StartsWith("Audio is on: 48000 Hz", ended[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OpeningADevice_IsRecordedWithWhatOpened()
    {
        var backend = new AudioPlaybackRecoveryTests.FakeBackend();
        backend.Devices.Add(new AudioDeviceInfo("built-in", "Speakers", "Test", true, 48_000, 2, [48_000], [2], 64, 4096));
        var session = new ProjectSession();
        using var audio = new AudioPlayback(_playback, session, _ => null, backend: backend, checkInterval: Timeout.InfiniteTimeSpan);
        var activities = new ActivityLog();
        ActivityEndedEventArgs? ended = null;
        activities.Ended += (_, e) => ended = e;
        var vm = new AudioViewModel(audio, Store, new Immediate(), backend.ListDevices, activities);

        vm.StartCommand.Execute(null);

        Assert.Equal(ActivityOutcome.Completed, ended?.Outcome);
        Assert.StartsWith("Audio is on:", ended?.Text, StringComparison.Ordinal);
        Assert.Contains("960-frame device buffer", vm.Status, StringComparison.Ordinal);
        Assert.Contains("960-frame render blocks", vm.Status, StringComparison.Ordinal);
        Assert.Equal("Running at 48 kHz · 960 samples / 20.00 ms", vm.DeviceFormat);
        Assert.Empty(activities.Running);

        vm.RefreshMeters();
        Assert.Equal("No dropouts.", vm.Health);
        Assert.False(vm.HasDropouts);

        vm.StopCommand.Execute(null);
        Assert.Equal((string.Empty, string.Empty), (vm.DeviceFormat, vm.Health));
    }

    [Fact]
    public void Dropouts_AndTheAudioThreadsPriority_AreShown()
    {
        var backend = new AudioPlaybackRecoveryTests.FakeBackend();
        backend.Devices.Add(new AudioDeviceInfo("speakers", "Speakers", "WASAPI", true, 48_000, 2, [48_000], [2], 64, 4096));
        using var audio = new AudioPlayback(_playback, new ProjectSession(), _ => null, backend: backend, checkInterval: Timeout.InfiniteTimeSpan);
        var vm = new AudioViewModel(audio, Store, new Immediate(), backend.ListDevices);
        vm.StartCommand.Execute(null);
        var output = backend.Opened[0];

        output.CallbackThreadPolicy = AudioCallbackThreadPolicy.MmcssProAudio;
        vm.RefreshMeters();
        Assert.Equal("No dropouts · audio thread at MMCSS “Pro Audio” priority.", vm.Health);
        Assert.False(vm.HasDropouts);

        output.Discontinuities = 3;
        vm.RefreshMeters();
        Assert.Equal("Dropouts: 3 late device callbacks · audio thread at MMCSS “Pro Audio” priority.", vm.Health);
        Assert.True(vm.HasDropouts);

        output.Discontinuities = 0;
        output.CallbackThreadPolicy = AudioCallbackThreadPolicy.Normal;
        vm.RefreshMeters();
        Assert.True(vm.HasDropouts);
    }

    [Fact]
    public void AFixedRateOtherThanTheWindowsMixRate_WarnsThatItIsResampled()
    {
        var devices = new[] { new AudioDeviceInfo("wasapi-device", "Speakers", "WASAPI", true, 48_000, 2, [44_100, 48_000], [2], 64, 4096) };
        var vm = new AudioViewModel(_audio, Store, new Immediate(), () => devices, listHosts: () => ["WASAPI"]);
        Assert.Empty(vm.ResampleWarning);

        vm.SampleRate = 44_100;
        Assert.Equal("Windows mixes this device at 48,000 Hz, so 44,100 Hz is resampled. Automatic follows the device.", vm.ResampleWarning);

        vm.SampleRate = 48_000;
        Assert.Empty(vm.ResampleWarning);
    }

    [Fact]
    public void TheRateAndBlockLabels_ReadAsTheyDoInOtherDaws()
    {
        Assert.Equal(("Automatic", "48,000 Hz"), (AudioViewModel.RateLabel(0), AudioViewModel.RateLabel(48_000)));
        Assert.Equal(("Automatic", "256 samples"), (AudioViewModel.BlockLabel(0), AudioViewModel.BlockLabel(256)));
        Assert.Equal("480 samples / 10.00 ms", AudioViewModel.BufferDuration(480, 48_000));
    }

    [Fact]
    public void RebuildingTheChoices_IgnoresTheDropdownsEmptySelection_AndAnnouncesTheSelectionAgain()
    {
        var devices = new[]
        {
            new AudioDeviceInfo("asio-device", "Interface", "ASIO", true, 48_000, 2, [44_100, 48_000], [2], 32, 1024),
            new AudioDeviceInfo("wasapi-device", "Speakers", "WASAPI", true, 48_000, 2, [44_100, 48_000], [2], 64, 4096),
        };
        var vm = new AudioViewModel(_audio, Store, new Immediate(), () => devices, listHosts: () => ["ASIO", "WASAPI"]);
        vm.SelectedSampleRate = 44_100;
        vm.SelectedBufferFrames = 128;
        var announced = new List<string?>();
        vm.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        // A dropdown whose items are cleared writes back an empty selection.
        vm.SelectedSampleRate = null;
        vm.SelectedBufferFrames = null;
        Assert.Equal((44_100, 128), (vm.SampleRate, vm.BufferFrames));

        vm.Host = vm.Hosts.Single(host => host.Name == "WASAPI");
        vm.Device = vm.Devices.Single(device => device.Id == "wasapi-device");

        Assert.Equal((44_100, 128), (vm.SelectedSampleRate, vm.SelectedBufferFrames));
        Assert.Contains(nameof(AudioViewModel.SelectedSampleRate), announced);
        Assert.Contains(nameof(AudioViewModel.SelectedBufferFrames), announced);
        vm.StopCommand.Execute(null);
        Assert.Equal((44_100, 128), (Store.Load().SampleRate, Store.Load().BufferFrames));
    }

    private static AudioDeviceInfo Device(string id, string name, params int[] rates) =>
        new(id, name, "CoreAudio", false, rates[0], 2, [.. rates], ImmutableArray.Create(2), 32, 4096);

    private sealed class Immediate : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();
    }
}
