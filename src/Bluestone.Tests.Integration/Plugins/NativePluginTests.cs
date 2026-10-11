using System.Collections.Immutable;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;
using Bluestone.Plugins.Scanning;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Plugins;

/// <summary>
/// VST3 and CLAP through the native plugin host, with Bluestone's Rust test plugins (built and packaged with these tests,
/// so they run on every CI runner): scanning, loading, processing with sample-accurate parameters and events, state,
/// rendering a MIDI effect offline, and a killed worker that is restarted from its state.
/// </summary>
[Collection(PluginProcessTests.Name)]
public sealed class NativePluginTests
{
    private const int Frames = 64;

    public static TheoryData<string> Formats() => new() { "vst3", "clap" };

    public static string ModulePath(string format) =>
        Path.Combine(AppContext.BaseDirectory, "test-plugins", $"BluestoneTestPlugins.{format}");

    /// <summary>Scans the format's test module in a scanner process and returns its plugins by name.</summary>
    internal static async Task<Dictionary<string, PluginIdentity>> ScanAsync(PluginTestHost host, string format)
    {
        using var scanner = new PluginScanner(new PluginScannerOptions(Path.Combine(host.Directory, "scan")));
        var result = await scanner.RescanAsync(ModulePath(format), ignoreQuarantine: true, Ct);
        Assert.True(result.Failure is null, $"Scanning {ModulePath(format)} failed: {result.Failure}");
        return result.Plugins.ToDictionary(p => p.DisplayName);
    }

    public static PluginInstanceRequest Request(PluginIdentity plugin, string format, int inputs, int outputs, int frames = Frames) =>
        new(plugin, 48_000, frames, inputs, outputs) { ModulePath = ModulePath(format) };

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Scanning_TheTestModule_FindsItsThreePlugins_WithTheirKinds(string format)
    {
        await using var host = new PluginTestHost();

        var plugins = await ScanAsync(host, format);

        Assert.Equal(PluginKind.AudioEffect, plugins["Test Gain"].Kind);
        Assert.Equal(PluginKind.Instrument, plugins["Test Sine"].Kind);
        Assert.Equal(PluginKind.MidiEffect, plugins["Test Transpose"].Kind);
        Assert.All(plugins.Values, p => Assert.Equal(format, p.Format));
        Assert.All(plugins.Values, p => Assert.Equal("BluestoneTestPlugins", p.ModuleId));
        Assert.All(plugins.Values, p => Assert.Equal("Bluestone", p.Vendor));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Gain_ReportsItsParameters_ScalesAudio_AndAMidBlockChangeLandsOnTheExactSample(string format)
    {
        await using var host = new PluginTestHost();
        var plugins = await ScanAsync(host, format);
        var gain = await host.CreateRunningAsync(Request(plugins["Test Gain"], format, 2, 2));

        Assert.Equal(["Gain", "Invert"], gain.Parameters.Select(p => p.Name));
        Assert.Equal(0.5, gain.Parameters[0].DefaultValue, 6);
        Assert.Equal(1, gain.Parameters[1].StepCount);
        Assert.All(gain.Parameters, p => Assert.True(p.IsUserFacing));

        var driver = new BlockDriver(gain, Frames);
        Array.Fill(driver.Input, 1.0f);
        Assert.Equal(ProcessOutcome.Priming, driver.Process().Outcome);
        Assert.True(gain.QueueParameterChange(0, 0.25, sampleOffset: 37));
        Assert.Equal(ProcessOutcome.Processed, driver.Process().Outcome);
        Assert.All(driver.Output, s => Assert.Equal(1.0f, s));
        Assert.Equal(ProcessOutcome.Processed, driver.Process().Outcome);
        foreach (var channel in new[] { 0, 1 })
        {
            var block = driver.Channel(channel);
            Assert.All(block[..37], s => Assert.Equal(1.0f, s));
            Assert.All(block[37..], s => Assert.Equal(0.5f, s));
        }

        var values = await gain.GetParametersAsync(Ct);
        Assert.Equal(0.25, values.Single(v => v.Id == 0).Value, 6);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Sine_PlaysANoteAtItsPitch_OnBothChannels(string format)
    {
        await using var host = new PluginTestHost();
        var plugins = await ScanAsync(host, format);
        var sine = await host.CreateRunningAsync(Request(plugins["Test Sine"], format, 0, 2, frames: 256));
        var driver = new BlockDriver(sine, 256);
        var left = new List<float>();

        driver.Process([PluginEvent.NoteOn(0, 0, 69, 127)]);
        for (var b = 0; b < 40; b++)
        {
            Assert.Equal(ProcessOutcome.Processed, driver.Process().Outcome);
            Assert.Equal(driver.Channel(0), driver.Channel(1));
            left.AddRange(driver.Channel(0));
        }

        var crossings = 0;
        for (var i = 4800; i < 9600; i++)
        {
            if (left[i - 1] < 0 && left[i] >= 0)
            {
                crossings++;
            }
        }

        Assert.InRange(crossings, 43, 45);
        Assert.InRange(left.Max(), 0.249f, 0.251f);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Transpose_TransposesNotes_AndTheNoteOffFollowsItsNoteOn(string format)
    {
        await using var host = new PluginTestHost();
        var plugins = await ScanAsync(host, format);
        var transpose = await host.CreateRunningAsync(Request(plugins["Test Transpose"], format, 0, 0));
        await transpose.SetParameterAsync(0, (7 + 24) / 48.0, Ct);
        var driver = new BlockDriver(transpose, Frames);

        driver.Process([PluginEvent.NoteOn(0, 2, 60, 100), PluginEvent.NoteOff(10, 2, 60)]);
        driver.Process();

        Assert.Equal(ProcessOutcome.Processed, driver.LastOutcome.Outcome);
        Assert.Equal([PluginEvent.NoteOn(0, 2, 67, 100), PluginEvent.NoteOff(10, 2, 67)], driver.OutputEvents.ToArray());
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Transpose_RenderedOffline_MovesTheNotesOnTheTimeline(string format)
    {
        await using var host = new PluginTestHost();
        var plugins = await ScanAsync(host, format);
        var transpose = await host.CreateRunningAsync(Request(plugins["Test Transpose"], format, 0, 0));
        ImmutableArray<TimelineEvent> events = [new(100, PluginEvent.NoteOn(0, 0, 60, 90)), new(5000, PluginEvent.NoteOff(0, 0, 60))];

        var rendered = await transpose.RenderEventsAsync(null, [new ParameterValue(0, (-5 + 24) / 48.0)], 6000, [new TimelineTempo(0, 120)], events, [], Ct);

        Assert.Equal([new TimelineEvent(100, PluginEvent.NoteOn(0, 0, 55, 90)), new TimelineEvent(5000, PluginEvent.NoteOff(0, 0, 55))], rendered.Events.ToArray());
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task State_CapturedFromOneInstance_RestoresIntoANewOne(string format)
    {
        await using var host = new PluginTestHost();
        var plugins = await ScanAsync(host, format);
        var first = await host.CreateRunningAsync(Request(plugins["Test Gain"], format, 2, 2));
        await first.SetParameterAsync(0, 0.125, Ct);
        await first.SetParameterAsync(1, 1.0, Ct);

        var snapshot = await first.CaptureStateAsync(Ct);
        Assert.Equal($"bluestone.{format}-state", snapshot.State.Format);
        var second = await host.CreateRunningAsync(Request(plugins["Test Gain"], format, 2, 2) with { RestoreFrom = snapshot });

        Assert.Null(second.LastStateRestoreError);
        var values = await second.GetParametersAsync(Ct);
        Assert.Equal(0.125, values.Single(v => v.Id == 0).Value, 6);
        Assert.Equal(1.0, values.Single(v => v.Id == 1).Value, 6);
        var driver = new BlockDriver(second, Frames);
        Array.Fill(driver.Input, 1.0f);
        driver.Process();
        driver.Process();
        Assert.All(driver.Output, s => Assert.Equal(-0.25f, s));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task CorruptState_IsReported_AndTheInstanceStillRuns(string format)
    {
        await using var host = new PluginTestHost();
        var plugins = await ScanAsync(host, format);
        var bad = new PluginStateSnapshot(plugins["Test Gain"], new PluginStateData($"bluestone.{format}-state", [1, 2, 3]), [], DateTimeOffset.UnixEpoch);

        var gain = await host.CreateRunningAsync(Request(plugins["Test Gain"], format, 2, 2) with { RestoreFrom = bad });

        Assert.NotNull(gain.LastStateRestoreError);
        Assert.Equal(PluginInstanceState.Running, gain.Status.State);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task KillingTheWorker_LeavesSilence_AndRestartRestoresStateAndParameters(string format)
    {
        await using var host = new PluginTestHost();
        var plugins = await ScanAsync(host, format);
        var gain = await host.CreateRunningAsync(Request(plugins["Test Gain"], format, 2, 2));
        await gain.SetParameterAsync(0, 0.25, Ct);
        await gain.CaptureStateAsync(Ct);
        await gain.SetParameterAsync(1, 1.0, Ct);
        var driver = new BlockDriver(gain, Frames);
        Array.Fill(driver.Input, 1.0f);
        driver.Process();
        driver.Process();
        Assert.All(driver.Output, s => Assert.Equal(-0.5f, s));

        KillProcess(gain.WorkerProcessId!.Value);
        await WaitForStateAsync(gain, PluginInstanceState.Unavailable);
        Assert.Equal(UnavailableReason.Crashed, gain.Status.Reason);
        Assert.Equal(ProcessOutcome.Unavailable, driver.Process().Outcome);
        Assert.All(driver.Output, s => Assert.Equal(0.0f, s));

        Assert.Equal(PluginInstanceState.Running, (await gain.RestartAsync(Ct)).State);
        host.Track(gain);
        driver.Process();
        driver.Process();
        Assert.All(driver.Output, s => Assert.Equal(-0.5f, s));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task OpeningAnEditor_OnAPluginWithoutOne_IsRefusedWithAReason(string format)
    {
        await using var host = new PluginTestHost();
        var plugins = await ScanAsync(host, format);
        var gain = await host.CreateRunningAsync(Request(plugins["Test Gain"], format, 2, 2));

        var error = await Assert.ThrowsAsync<PluginHostException>(() => gain.OpenEditorAsync("Test Gain", Ct));

        Assert.Equal(ErrorCode.NotSupported, error.Code);
        Assert.False(gain.IsEditorOpen);
    }

    [Fact]
    public async Task AModuleThatIsNotAPlugin_IsReportedAsAFailure_NotQuarantined()
    {
        await using var host = new PluginTestHost();
        var bundle = Path.Combine(host.Directory, "Broken.vst3");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        await File.WriteAllTextAsync(Path.Combine(bundle, "Contents", "MacOS", "Broken"), "not a binary", Ct);
        using var scanner = new PluginScanner(new PluginScannerOptions(Path.Combine(host.Directory, "scan")));

        var result = await scanner.RescanAsync(bundle, ignoreQuarantine: false, Ct);

        Assert.Equal(ModuleScanStatus.Failed, result.Status);
        Assert.Contains(result.Failure!.Kind, new[] { ScanFailureKind.Malformed, ScanFailureKind.Unsupported });
        Assert.Empty(scanner.Quarantined);
    }
}
