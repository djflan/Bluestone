using System.Collections.Immutable;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Bluestone.Application.Audio;
using Bluestone.Application.Editing;
using Bluestone.Application.Live;
using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Desktop.Views;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using TrackModel = Bluestone.Domain.Sequencing.Track;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The production mixer console with a realistic session: eleven strips (a long name, a Group with members, FX returns, a
/// MIDI-only track, varied gain, pan, inserts, and sends), meters playing, and one strip selected. Renders the console at
/// normal and compact sizes and in every meter mode. Frames always go to the test output; set BLUESTONE_MIXER_FINAL_DIR
/// to also copy them to a review folder.
/// </summary>
public sealed class MixerConsoleShowcaseTests
{
    private sealed record Seed(string Name, TrackRole Role, double Gain, double Pan, double PeakDb, double RmsDb, string[] Devices,
        int[]? Bypassed = null, bool Muted = false, bool InDrums = false, string[]? Sends = null);

    private static readonly Seed[] Session =
    [
        new("Kick", TrackRole.Audio, -6.0, 0, -4.3, -16, ["Tone EQ", "Soft clip"], InDrums: true, Sends: ["Room"]),
        new("Snare", TrackRole.Audio, -8.5, -0.41, -6.8, -19, ["Tone EQ", "Opto"], InDrums: true, Sends: ["Room", "Echo"]),
        new("Hi-hats", TrackRole.Audio, -16.8, 0.24, -12.6, -26, [], InDrums: true),
        new("Drums", TrackRole.Group, 8.0, 0, -1.7, -11, ["Bus comp", "Tape", "Soft clip", "Bus EQ", "Limiter"]),
        new("Granular atmosphere after midnight", TrackRole.Hybrid, -13.2, 0.35, -8.2, -22, ["Stone EQ", "Tape", "Stereo tool"], [1], Sends: ["Room", "Echo"]),
        new("Bass", TrackRole.Hybrid, -7.2, 0, -3.6, -14, ["Stone EQ", "Opto", "Saturator"], Sends: ["Room"]),
        new("Keys", TrackRole.Instrument, 0, 0, double.NaN, double.NaN, []),
        new("Lead vocal", TrackRole.Audio, -3.8, 0, -0.7, -12, ["Vocal EQ", "De-esser", "Opto", "Tape", "Stereo tool", "Limiter"], [3], Sends: ["Room", "Echo"]),
        new("Backing vocals", TrackRole.Audio, -11.5, -0.32, -60, -60, ["High pass"], Muted: true, Sends: ["Room"]),
        new("Room", TrackRole.Effect, -12.0, 0.12, -10.4, -24, ["Room", "Tone EQ"]),
        new("Echo", TrackRole.Effect, -18.5, -0.27, -15.8, -29, ["Echo", "High pass", "Tape"]),
    ];

    private static readonly (double PeakDb, double RmsDb) MasterLevel = (-0.4, -9.5);

    public static TheoryData<string, int, int, MixerMeterMode, bool> Cases => new()
    {
        { "final-normal", 1600, 500, MixerMeterMode.PeakRms, false },
        { "final-compact", 1240, 480, MixerMeterMode.PeakRms, false },
        { "mode-vu", 1600, 500, MixerMeterMode.Vu, false },
        { "mode-k14", 1600, 500, MixerMeterMode.K14, false },
        { "mode-lufs-warming", 1600, 500, MixerMeterMode.LufsS, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public Task RenderTheConsole(string name, int width, int height, MixerMeterMode mode, bool warming) => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var ids = await SeedAsync(app);
        var console = app.ViewModel.MixerConsole;
        var audio = new ShowcaseAudio(ids, mode, warming);
        console.UseAudio(audio);
        console.SetMeterModeCommand.Execute(mode);

        var view = new MixerConsoleView { DataContext = console };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        try
        {
            console.Tracks.Single(row => row.Name == "Bass").SelectCommand.Execute(null);
            var lead = console.Tracks.Single(row => row.Name == "Lead vocal");
            lead.IsArmed = true;

            // Two refreshes: the first sets a higher held peak, the second is the level now.
            var timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            audio.Boost = 2;
            console.RefreshMeters(timestamp);
            audio.Boost = 0;
            console.RefreshMeters(timestamp + 1);
            app.Settle();
            window.UpdateLayout();
            app.Settle();

            var rails = view.GetLogicalDescendants().OfType<MixerMeterRail>().Where(rail => rail.IsEffectivelyVisible).ToArray();
            var gains = view.GetLogicalDescendants().OfType<MixerGainBar>().Where(bar => bar.IsEffectivelyVisible).ToArray();
            var pans = view.GetLogicalDescendants().OfType<MixerPanRamp>().Where(pan => pan.IsEffectivelyVisible).ToArray();
            Assert.Equal(Session.Count(seed => seed.Role != TrackRole.Instrument) + 1, rails.Length);
            Assert.Equal(rails.Length, gains.Length);
            Assert.Equal(rails.Length, pans.Length);
            Assert.All(rails, rail => Assert.Equal(mode, rail.Mode));
            Assert.All(rails, rail => Assert.Equal(rails[0].Bounds.Height, rail.Bounds.Height));
            Assert.Single(view.GetLogicalDescendants().OfType<Border>(), border =>
                AutomationProperties.GetName(border)?.StartsWith("Keys,", StringComparison.Ordinal) == true);

            // Send knobs take their strip's colour, not the default accent.
            var knobs = view.GetLogicalDescendants().OfType<Knob>().Where(knob => knob.IsEffectivelyVisible).ToArray();
            Assert.NotEmpty(knobs);
            Assert.All(knobs, knob => Assert.NotEqual(Bluestone.Desktop.Theme.Palette.Accent, knob.Accent));

            // The clipped strips (the first refresh pushed them past 0 dBFS) light their clip and offer the reset.
            Assert.True(lead.IsClipped);
            var clips = view.GetLogicalDescendants().OfType<Button>().Where(button => button.Classes.Contains("mixerClip") && button.IsEffectivelyVisible).ToArray();
            Assert.Contains(clips, button => AutomationProperties.GetName(button) == "Clipped – click to reset");

            var path = app.SaveFrame($"mixer-{name}", window);
            if (Environment.GetEnvironmentVariable("BLUESTONE_MIXER_FINAL_DIR") is { Length: > 0 } destination)
            {
                Directory.CreateDirectory(destination);
                File.Copy(path, Path.Combine(destination, $"{name}.png"), overwrite: true);
            }
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// The docked mixer at the workspace's default detail height in a laptop-sized window: the approved strip, insert stack
    /// and sends included, must fit without the processing band folding away.
    /// </summary>
    [Fact]
    public Task RenderTheDockedConsoleAtTheDefaultDetailHeight() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var ids = await SeedAsync(app);
        app.Window.Width = 1440;
        app.Window.Height = 900;
        Assert.Equal(Bluestone.Presentation.WorkspaceViewModel.DefaultDetailExtent, app.ViewModel.Workspace.DetailExtent);
        app.ViewModel.Workspace.Mixer.ShowCommand.Execute(null);
        app.Settle();
        var view = AppUnderTest.Within<MixerConsoleView>(app.Window, _ => true);
        var console = app.ViewModel.MixerConsole;
        var audio = new ShowcaseAudio(ids, MixerMeterMode.PeakRms, warming: false);
        console.UseAudio(audio);
        console.Tracks.Single(row => row.Name == "Bass").SelectCommand.Execute(null);
        var timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        audio.Boost = 2;
        console.RefreshMeters(timestamp);
        audio.Boost = 0;
        console.RefreshMeters(timestamp + 1);
        app.Settle();
        app.Window.UpdateLayout();
        app.Settle();

        var strips = view.FindControl<Grid>("StripsArea")!;
        Assert.False(view.Classes.Contains("short"), $"The default docked mixer folded its processing band at {strips.Bounds.Height}px.");
        var bands = view.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("processing") && border.IsEffectivelyVisible).ToArray();
        Assert.NotEmpty(bands);
        var rails = view.GetLogicalDescendants().OfType<MixerMeterRail>().Where(rail => rail.IsEffectivelyVisible).ToArray();
        Assert.NotEmpty(rails);
        var minimum = (double)view.FindResource("Mixer.TightRailsHeight")!;
        Assert.All(rails, rail => Assert.True(rail.Bounds.Height >= minimum,
            $"The meter is {rail.Bounds.Height}px tall in a {strips.Bounds.Height}px strips area."));

        var path = app.SaveFrame("mixer-final-docked-default");
        if (Environment.GetEnvironmentVariable("BLUESTONE_MIXER_FINAL_DIR") is { Length: > 0 } destination)
        {
            Directory.CreateDirectory(destination);
            File.Copy(path, Path.Combine(destination, "final-docked-default.png"), overwrite: true);
        }
    });

    private static async Task<Dictionary<TrackId, Seed>> SeedAsync(AppUnderTest app)
    {
        var drumsId = TrackId.New();
        var tracks = new List<TrackModel>();
        var byName = new Dictionary<string, TrackId>(StringComparer.Ordinal);
        foreach (var seed in Session)
        {
            var id = seed.Name == "Drums" ? drumsId : TrackId.New();
            byName[seed.Name] = id;
            var strip = seed.Role == TrackRole.Instrument
                ? null
                : new AudioStrip { GainDecibels = seed.Gain, Pan = seed.Pan, IsMuted = seed.Muted };
            tracks.Add(new TrackModel(id, seed.Name, [], role: seed.Role, group: seed.InDrums ? drumsId : null, audioStrip: strip));
        }

        var connections = new List<SignalConnection>();
        var chains = new List<DeviceChain>();
        foreach (var seed in Session)
        {
            var id = byName[seed.Name];
            if (seed.Role != TrackRole.Instrument)
            {
                connections.Add(SignalConnection.Create(SignalKind.Audio, SignalNode.Track(id), seed.InDrums ? SignalNode.Track(drumsId) : SignalNode.Master));
            }

            foreach (var (send, index) in (seed.Sends ?? []).Select((send, index) => (send, index)))
            {
                connections.Add(SignalConnection.Create(SignalKind.Audio, SignalNode.Track(id), SignalNode.Track(byName[send])) with
                {
                    AudioPurpose = AudioRoutePurpose.Send,
                    SendLevelDecibels = index == 0 ? -18 + seed.Gain / 2 : -28,
                });
            }

            if (seed.Devices.Length > 0)
            {
                chains.Add(Chain(ChainOwner.ForTrack(id), seed.Devices, seed.Bypassed));
            }
        }

        chains.Add(Chain(ChainOwner.Master, ["Bus EQ", "Glue comp", "Limiter"], null));
        app.Session.Execute(new ProjectCommand("Set up a mixing session", project => project with
        {
            Sequence = new Sequence(project.Sequence.TempoMap, project.Sequence.MeterMap, [.. tracks], project.Sequence.Markers),
            Connections = [.. connections],
            Chains = [.. project.Chains.Where(chain => chain.Owner == ChainOwner.Rack), .. chains],
        }));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        app.Settle();
        return Session.ToDictionary(seed => byName[seed.Name]);
    }

    private static DeviceChain Chain(ChainOwner owner, string[] devices, int[]? bypassed) => DeviceChain.Create(owner) with
    {
        Devices =
        [
            .. devices.Select((name, index) => DeviceInstance.Create(new DeviceReference(
                new DeviceDefinitionId($"vst3:{index:D2}{name.Length:D2}000000000000000000000000000"), name)) with
            {
                IsBypassed = bypassed?.Contains(index) == true,
            }),
        ],
    };

    /// <summary>Stands in for the audio engine's mixer meters: fixed levels per strip, reported in the chosen mode.</summary>
    private sealed class ShowcaseAudio(Dictionary<TrackId, Seed> seeds, MixerMeterMode mode, bool warming) : IAudioPlayback
    {
        public double Boost { get; set; }

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool IsRunning => true;

        public string? Problem => null;

        public bool IsOnFallback => false;

        public bool IsStarting => false;

        public bool IsWaitingForDevice => false;

        public ImmutableArray<string> GraphProblems => [];

        public bool TestTone { get; set; }

        public bool RendersInEngine => false;

        public AudioStreamInfo? Stream => null;

        public AudioTelemetry? Telemetry => null;

        public LiveLatency LiveLatency => default;

        public bool IsLiveQueue => false;

        public bool Start(AudioSettings settings) => true;

        public void Stop()
        {
        }

        public void Rebuild()
        {
        }

        public MeterReading? MeterOf(MixerChannelId? channel) => null;

        public MixerMeterReading? MixerMeterOf(TrackId? track)
        {
            var (peak, rms, index) = track is { } id
                ? seeds.TryGetValue(id, out var seed) ? (seed.PeakDb, seed.RmsDb, Array.IndexOf(Session, seed)) : (double.NaN, double.NaN, 0)
                : (MasterLevel.PeakDb, MasterLevel.RmsDb, -1);
            if (double.IsNaN(peak))
            {
                return null;
            }

            peak += Boost;
            var reading = new MeterReading(Linear(peak), Linear(peak - 0.6), Linear(rms), Linear(rms - 0.4));
            var ready = !(warming && index is >= 0 and < 5);
            var value = mode switch
            {
                MixerMeterMode.PeakRms => rms,
                MixerMeterMode.Vu => rms + 18,
                MixerMeterMode.K20 => rms + 20,
                MixerMeterMode.K14 => rms + 14,
                MixerMeterMode.K12 => rms + 12,
                _ => ready ? rms - 2 : double.NaN,
            };
            return new MixerMeterReading(reading, mode, value, value - 0.5, ready);
        }

        public void SetMixerMeterSettings(MixerMeterSettings settings)
        {
        }

        public MeterReading? JunctionMeterOf(Guid junction) => null;

        public void WatchJunctions(IReadOnlyCollection<Guid> junctions)
        {
        }

        public void Dispose()
        {
        }

        private static float Linear(double decibels) => (float)Math.Pow(10, decibels / 20);
    }
}
