using Bluestone.Plugins;

namespace Bluestone.Tests.Unit.Plugins;

public sealed class PipelineLatencyTests
{
    [Theory]
    [InlineData(0, 1, 256, 0)]
    [InlineData(0, 2, 256, 256)]
    [InlineData(10, 2, 256, 266)]
    [InlineData(32, 3, 128, 288)]
    public void ReportedLatency_IsPluginLatencyPlusDepthMinusOneBlocks(int plugin, int depth, int frames, int expected) =>
        Assert.Equal(expected, PipelineLatency.ReportedLatencyFrames(plugin, depth, frames));

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(0, 2, -1)]
    [InlineData(5, 2, 4)]
    [InlineData(5, 3, 3)]
    public void CollectedBlock_IsKMinusDepthMinusOne(long submitted, int depth, long expected) =>
        Assert.Equal(expected, PipelineLatency.CollectedBlock(submitted, depth));
}
