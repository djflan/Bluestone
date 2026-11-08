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
        Assert.Equal([44_100, 96_000], vm.SampleRates);
        Assert.Equal(44_100, vm.SampleRate);
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

        Assert.Equal(new AudioSettings(false, "a", 48_000, 512, AudioSettings.DefaultQueuedFrames) { Host = "CoreAudio" }, Store.Load());
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
    public void FirstRun_PrefersASIOWhenAnASIODeviceIsAvailable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The first-run ASIO preference applies only on Windows.");
        var devices = new[]
        {
            new AudioDeviceInfo("asio-device", "Interface", "ASIO", false, 48_000, 2, [48_000], [2], 1024, 1024),
            new AudioDeviceInfo("wasapi-device", "Speakers", "WASAPI", true, 48_000, 2, [48_000], [2], 64, 4096),
        };

        var vm = new AudioViewModel(_audio, Store, new Immediate(), () => devices, listHosts: () => ["ASIO", "WASAPI"]);

        Assert.Equal("ASIO", vm.Host!.Name);
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
        Assert.Contains("256-frame render blocks", vm.Status, StringComparison.Ordinal);
        Assert.Empty(activities.Running);
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
