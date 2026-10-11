using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;
using Bluestone.Plugins.Scanning;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Plugins;

/// <summary>
/// Third-party plugins installed on the machine, when they are there (they are not on CI runners; each test then
/// skips with the reason): the owner's VST3 plugins and the open-source CLAP plugins downloaded for testing. Each is
/// scanned, loaded, played or fed audio, has a parameter set and its state captured, then its worker is killed while
/// processing; Bluestone reports it crashed, outputs silence, and a restart brings back the state and the parameter.
/// </summary>
[Collection(PluginProcessTests.Name)]
public sealed class InstalledPluginTests
{
    private const int Frames = 256;

    /// <summary>Format, module, plugin name, and the parameter to change ("" for the first one shown).</summary>
    public static TheoryData<string, string, string, string> Installed()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new()
        {
            // The Legend HZ's first parameter is its mod wheel, which the processor resets.
            { "vst3", "/Library/Audio/Plug-Ins/VST3/LegendHZ.vst3", "The Legend HZ", "Output Volume" },
            { "vst3", "/Library/Audio/Plug-Ins/VST3/DPA_Reverside.vst3", "REVERSIDE", "" },
            { "clap", Path.Combine(home, "Library/Audio/Plug-Ins/CLAP/Airwindows Consolidated.clap"), "Airwindows Consolidated", "" },
            { "clap", Path.Combine(home, "Library/Audio/Plug-Ins/CLAP/Surge XT.clap"), "Surge XT", "" },
        };
    }

    [Theory]
    [MemberData(nameof(Installed))]
    public async Task AnInstalledPlugin_Plays_KeepsItsState_AndSurvivesItsWorkerBeingKilled(string format, string modulePath, string name, string parameterName)
    {
        Assert.SkipUnless(Directory.Exists(modulePath) || File.Exists(modulePath), $"{name} ({format}) is not installed at {modulePath}.");
        await using var host = new PluginTestHost();
        using var scanner = new PluginScanner(new PluginScannerOptions(Path.Combine(host.Directory, "scan")) { ScanTimeout = TimeSpan.FromSeconds(60) });
        var scanned = await scanner.RescanAsync(modulePath, ignoreQuarantine: true, Ct);
        Assert.True(scanned.Failure is null, $"Scanning {name} failed: {scanned.Failure}");
        var plugin = scanned.Plugins.Single(p => p.DisplayName == name);
        Assert.Equal(format, plugin.Format);

        var isInstrument = plugin.Kind == PluginKind.Instrument;
        var request = new PluginInstanceRequest(plugin, 48_000, Frames, isInstrument ? 0 : 2, 2) { ModulePath = modulePath };
        var instance = await host.CreateRunningAsync(request);
        var parameter = instance.Parameters.First(p => p.IsUserFacing && p.StepCount == 0 && (p.Traits & ParameterTraits.Automatable) != 0
            && (parameterName.Length == 0 || p.Name == parameterName));
        var driver = new BlockDriver(instance, Frames);
        FillWithASine(driver.Input);

        Assert.True(Peak(driver, isInstrument, blocks: 40) > 1e-4f, $"{name} produced no audio.");

        var value = parameter.DefaultValue > 0.5 ? 0.2 : 0.8;
        await instance.SetParameterAsync(parameter.Id, value, Ct);
        var snapshot = await instance.CaptureStateAsync(Ct);
        Assert.Equal($"bluestone.{format}-state", snapshot.State.Format);
        Assert.Equal(value, (await instance.GetParametersAsync(Ct)).Single(v => v.Id == parameter.Id).Value, 2);

        // The worker is killed while blocks are being processed.
        var pid = instance.WorkerProcessId!.Value;
        driver.Process();
        KillProcess(pid);
        await WaitForStateAsync(instance, PluginInstanceState.Unavailable);
        Assert.Equal(UnavailableReason.Crashed, instance.Status.Reason);
        Assert.Equal(ProcessOutcome.Unavailable, driver.Process().Outcome);
        Assert.All(driver.Output, s => Assert.Equal(0.0f, s));

        Assert.Equal(PluginInstanceState.Running, (await instance.RestartAsync(Ct)).State);
        host.Track(instance);
        Assert.Equal(value, (await instance.GetParametersAsync(Ct)).Single(v => v.Id == parameter.Id).Value, 2);
        Assert.True(Peak(driver, isInstrument, blocks: 40) > 1e-4f, $"{name} produced no audio after the restart.");
    }

    private static float Peak(BlockDriver driver, bool playNote, int blocks)
    {
        var peak = 0f;
        for (var b = 0; b < blocks; b++)
        {
            ReadOnlySpan<PluginEvent> events = playNote && b == 0 ? [PluginEvent.NoteOn(0, 0, 60, 110)] : [];
            if (driver.Process(events).Outcome == ProcessOutcome.Processed)
            {
                peak = Math.Max(peak, driver.Output.Max(Math.Abs));
            }
        }

        return peak;
    }

    private static void FillWithASine(float[] buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = 0.25f * MathF.Sin(i * 0.05f);
        }
    }
}
