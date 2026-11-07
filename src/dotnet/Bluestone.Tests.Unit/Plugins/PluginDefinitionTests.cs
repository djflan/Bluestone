using Bluestone.Application.Plugins;
using Bluestone.Domain.Devices;
using Bluestone.Plugins.Protocol;

namespace Bluestone.Tests.Unit.Plugins;

/// <summary>What a plugin's device definition offers to routing (ADR 0037).</summary>
public sealed class PluginDefinitionTests
{
    [Theory]
    [InlineData(PluginKind.AudioEffect, true)]
    [InlineData(PluginKind.Instrument, false)]
    [InlineData(PluginKind.MidiEffect, false)]
    public void OnlyAudioEffects_HaveASidechainInput(PluginKind kind, bool sidechain)
    {
        var definition = PluginDeviceHost.DefinitionOf(new PluginIdentity("vst3", "m", "p", "P", "V", kind, "1"));

        var input = definition.FindInput(DeviceInputPort.Sidechain);

        Assert.Equal(sidechain, input is { Kind: SignalKind.Audio });
        Assert.Null(definition.FindInput(DeviceInputPort.Receive));
    }
}
