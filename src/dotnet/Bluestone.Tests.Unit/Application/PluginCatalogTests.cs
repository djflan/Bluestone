using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using Bluestone.Signal;

namespace Bluestone.Tests.Unit.Application;

/// <summary>The device catalog the plugin host extends with the available plugins, which is rebuilt after every edit.</summary>
public sealed class PluginCatalogTests : IDisposable
{
    private static readonly PluginIdentity Synth = new("vst3", "m", "synth", "Synth", "V", PluginKind.Instrument, "1");
    private static readonly PluginIdentity Echo = new("vst3", "m", "echo", "Echo", "V", PluginKind.AudioEffect, "1");
    private readonly string _data = Directory.CreateTempSubdirectory("bluestone-catalog-").FullName;

    public void Dispose() => Directory.Delete(_data, recursive: true);

    [Fact]
    public async Task Extend_GivesTheSameCatalog_UntilThePluginsOrTheBaseChange()
    {
        await using var manager = new PluginHostManager(new PluginHostOptions { DataDirectory = _data });
        var host = new PluginDeviceHost(manager, new ProjectSession(), [Synth]);

        var first = host.Extend(DeviceCatalog.BuiltIn);
        Assert.Same(first, host.Extend(DeviceCatalog.BuiltIn));
        Assert.NotNull(first.Find(PluginDeviceHost.DefinitionIdOf(Synth)));

        host.SetAvailable([Synth, Echo]);
        var rescanned = host.Extend(DeviceCatalog.BuiltIn);
        Assert.NotSame(first, rescanned);
        Assert.NotNull(rescanned.Find(PluginDeviceHost.DefinitionIdOf(Echo)));

        var other = DeviceCatalog.BuiltIn.With(PluginDeviceHost.DefinitionOf(new PluginIdentity("vst3", "m", "x", "X", "V", PluginKind.MidiEffect, "1")));
        Assert.NotSame(rescanned, host.Extend(other));
    }
}
