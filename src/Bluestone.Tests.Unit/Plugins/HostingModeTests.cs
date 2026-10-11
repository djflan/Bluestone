using System.Collections.Immutable;
using Bluestone.Application.Plugins;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using CpuArchitecture = System.Runtime.InteropServices.Architecture;

namespace Bluestone.Tests.Unit.Plugins;

/// <summary>Which instances share a worker process under each hosting mode (ADR 0034), and the settings that choose the mode.</summary>
public sealed class HostingModeTests : IDisposable
{
    private static readonly PluginIdentity SurgeVst3 = new("vst3", "Surge XT", "ABCDEF0123456789ABCDEF0123456789", "Surge XT", "Surge Synth Team", PluginKind.Instrument, "1.3");
    private static readonly PluginIdentity SurgeEffects = new("vst3", "Surge XT", "0123456789ABCDEF0123456789ABCDEF", "Surge XT Effects", "Surge Synth Team", PluginKind.AudioEffect, "1.3");
    private static readonly PluginIdentity SurgeClap = new("clap", "Surge XT", "org.surge-synth-team.surge-xt", "Surge XT", "surge synth team ", PluginKind.Instrument, "1.3");
    private static readonly PluginIdentity Legend = new("vst3", "LegendHZ", "255BF0CB0F9A419992C1A715E3C58F48", "The Legend HZ", "Synapse Audio", PluginKind.Instrument, "1.0");
    private static readonly PluginIdentity Anonymous = new("clap", "Mystery", "com.example.mystery", "Mystery", "", PluginKind.AudioEffect, "1.0");

    private static readonly PluginInstanceId A = PluginInstanceId.New();
    private static readonly PluginInstanceId B = PluginInstanceId.New();

    private readonly TestDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private static string Key(HostingMode mode, PluginIdentity plugin, PluginInstanceId instance, CpuArchitecture architecture = CpuArchitecture.Arm64) =>
        WorkerKeys.For(mode, plugin, instance, architecture);

    [Fact]
    public void Individually_GivesEveryInstanceItsOwnWorker_EvenOfOnePlugin()
    {
        Assert.NotEqual(Key(HostingMode.Individually, SurgeVst3, A), Key(HostingMode.Individually, SurgeVst3, B));
        Assert.Equal(Key(HostingMode.Individually, SurgeVst3, A), Key(HostingMode.Individually, SurgeVst3, A));
    }

    [Fact]
    public void ByPlugin_SharesOneWorkerPerModule_AndKeepsFormatsApart()
    {
        Assert.Equal(Key(HostingMode.ByPlugin, SurgeVst3, A), Key(HostingMode.ByPlugin, SurgeVst3, B));
        Assert.Equal(Key(HostingMode.ByPlugin, SurgeVst3, A), Key(HostingMode.ByPlugin, SurgeEffects, B));
        Assert.NotEqual(Key(HostingMode.ByPlugin, SurgeVst3, A), Key(HostingMode.ByPlugin, SurgeClap, B));
        Assert.NotEqual(Key(HostingMode.ByPlugin, SurgeVst3, A), Key(HostingMode.ByPlugin, Legend, B));
    }

    [Fact]
    public void ByManufacturer_SharesOneWorkerPerVendor_IgnoringCaseAndSpaces_AcrossFormatsAndModules()
    {
        Assert.Equal(Key(HostingMode.ByManufacturer, SurgeVst3, A), Key(HostingMode.ByManufacturer, SurgeClap, B));
        Assert.NotEqual(Key(HostingMode.ByManufacturer, SurgeVst3, A), Key(HostingMode.ByManufacturer, Legend, B));
    }

    [Fact]
    public void ByManufacturer_WithoutAVendor_HostsThePluginByItsModule()
    {
        Assert.Equal(Key(HostingMode.ByPlugin, Anonymous, A), Key(HostingMode.ByManufacturer, Anonymous, B));
        Assert.NotEqual(Key(HostingMode.ByManufacturer, Anonymous, A), Key(HostingMode.ByManufacturer, Legend, B));
    }

    [Fact]
    public void Together_SharesOneWorkerForEveryPlugin_ButNotWithOtherModes()
    {
        Assert.Equal(Key(HostingMode.Together, SurgeVst3, A), Key(HostingMode.Together, Legend, B));
        Assert.NotEqual(Key(HostingMode.Together, SurgeVst3, A), Key(HostingMode.ByPlugin, SurgeVst3, A));
        Assert.NotEqual(Key(HostingMode.Together, SurgeVst3, A), Key(HostingMode.ByManufacturer, SurgeVst3, A));
    }

    [Theory]
    [InlineData(HostingMode.ByPlugin)]
    [InlineData(HostingMode.ByManufacturer)]
    [InlineData(HostingMode.Together)]
    public void SharedWorkers_NeverMixArchitectures(HostingMode mode) =>
        Assert.NotEqual(Key(mode, SurgeVst3, A, CpuArchitecture.Arm64), Key(mode, SurgeVst3, A, CpuArchitecture.X64));

    [Fact]
    public void WithinBluestone_HasNoWorkerKey() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Key(HostingMode.WithinBluestone, SurgeVst3, A));

    [Fact]
    public void Settings_DefaultToByPlugin_WithoutAutomaticRestart()
    {
        var settings = new PluginHostingSettings();

        Assert.Equal(HostingMode.ByPlugin, settings.Mode);
        Assert.Equal(HostingMode.ByPlugin, settings.ModeFor(Legend));
        Assert.False(settings.AutoRestart);
        Assert.False(settings.RestartPolicy.AutoRestart);
        Assert.True((settings with { AutoRestart = true }).RestartPolicy.AutoRestart);
    }

    [Fact]
    public void APluginOverride_WinsOverTheGlobalMode_AndClearingItFollowsTheGlobalModeAgain()
    {
        var settings = new PluginHostingSettings { Mode = HostingMode.Together }
            .WithOverride(PluginDeviceHost.DefinitionIdOf(Legend), HostingMode.Individually);

        Assert.Equal(HostingMode.Individually, settings.ModeFor(Legend));
        Assert.Equal(HostingMode.Together, settings.ModeFor(SurgeVst3));
        Assert.Equal(HostingMode.Together, settings.WithOverride(PluginDeviceHost.DefinitionIdOf(Legend), null).ModeFor(Legend));
    }

    [Fact]
    public void HostingWithinBluestone_CannotBeChosen()
    {
        Assert.DoesNotContain(HostingMode.WithinBluestone, PluginHostingSettings.Modes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginHostingSettings().WithOverride(PluginDeviceHost.DefinitionIdOf(Legend), HostingMode.WithinBluestone));
    }

    [Fact]
    public void Store_RoundTripsTheSettings_AndAMissingFileGivesTheDefaults()
    {
        var store = new PluginHostingSettingsStore(_directory.File("plugin-hosting.json"));
        Assert.Equal(new PluginHostingSettings(), store.Load());
        var settings = new PluginHostingSettings { Mode = HostingMode.ByManufacturer, AutoRestart = true }
            .WithOverride(PluginDeviceHost.DefinitionIdOf(Legend), HostingMode.Individually);

        store.Save(settings);

        Assert.Equal(settings, store.Load());
        Assert.Contains("\"byManufacturer\"", File.ReadAllText(store.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void Store_DropsInProcessAndUnknownModes_AndAnUnreadableFileGivesTheDefaults()
    {
        var store = new PluginHostingSettingsStore(_directory.File("plugin-hosting.json"));
        File.WriteAllText(store.Path, """{ "mode": "withinBluestone", "overrides": { "plugin:vst3:A/1": "withinBluestone", "plugin:vst3:B/2": "together", "plugin:vst3:C/3": 17 } }""");

        var loaded = store.Load();

        Assert.Equal(HostingMode.ByPlugin, loaded.Mode);
        Assert.Equal(ImmutableDictionary<string, HostingMode>.Empty.Add("plugin:vst3:B/2", HostingMode.Together), loaded.Overrides);

        File.WriteAllText(store.Path, "{ not json");
        Assert.Equal(new PluginHostingSettings(), store.Load());
    }
}
