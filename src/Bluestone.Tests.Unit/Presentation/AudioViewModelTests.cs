using System.Collections.Immutable;
using Bluestone.Application.Audio;
using Bluestone.Application.Sessions;
using Bluestone.Audio.Devices;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Presentation;
using Bluestone.Profiles;

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

        Assert.Equal(new AudioSettings(false, "a", 48_000, 512, AudioSettings.DefaultQueuedFrames), Store.Load());
    }

    [Fact]
    public void AnUnreadableSettingsFile_GivesTheDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Store.Path, "{ not json");

        Assert.Equal(new AudioSettings(), Store.Load());
    }

    private static AudioDeviceInfo Device(string id, string name, params int[] rates) =>
        new(id, name, "CoreAudio", false, rates[0], 2, [.. rates], ImmutableArray.Create(2), 32, 4096);

    private sealed class Immediate : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();
    }
}
