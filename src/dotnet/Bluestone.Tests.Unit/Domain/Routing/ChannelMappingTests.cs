using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;

namespace Bluestone.Tests.Unit.Domain.Routing;

public sealed class ChannelMappingTests
{
    private static MidiChannel Ch(int n) => MidiChannel.FromNumber(n);

    [Fact]
    public void Preserve_LeavesEveryChannelAlone()
    {
        Assert.True(ChannelMapping.Preserve.IsPreserving);
        Assert.All(MidiChannel.All, c =>
        {
            Assert.True(ChannelMapping.Preserve.TryMap(c, out var sent));
            Assert.Equal(c, sent);
        });
    }

    [Fact]
    public void Force_SendsEverythingOnOneChannel()
    {
        var mapping = ChannelMapping.ForceTo(Ch(4));

        Assert.All(MidiChannel.All, c =>
        {
            Assert.True(mapping.TryMap(c, out var sent));
            Assert.Equal(Ch(4), sent);
        });
        Assert.False(mapping.IsPreserving);
    }

    [Fact]
    public void Remap_ChangesListedChannelsAndKeepsTheRest()
    {
        var mapping = new ChannelMapping { Remap = [new ChannelRemap(Ch(1), Ch(5)), new ChannelRemap(Ch(2), Ch(6))] };

        Assert.True(mapping.TryMap(Ch(1), out var one));
        Assert.True(mapping.TryMap(Ch(2), out var two));
        Assert.True(mapping.TryMap(Ch(3), out var three));
        Assert.Equal(Ch(5), one);
        Assert.Equal(Ch(6), two);
        Assert.Equal(Ch(3), three);
    }

    [Fact]
    public void Only_FiltersOtherChannelsOut_BeforeMapping()
    {
        var mapping = new ChannelMapping { Only = [Ch(10), Ch(2)], Force = Ch(1) };

        Assert.False(mapping.TryMap(Ch(3), out _));
        Assert.True(mapping.TryMap(Ch(10), out var sent));
        Assert.Equal(Ch(1), sent);
        Assert.Equal<MidiChannel>([Ch(2), Ch(10)], mapping.Only);
    }

    [Fact]
    public void ARemapTable_CannotMapAChannelTwice() =>
        Assert.Throws<ArgumentException>(() => new ChannelMapping { Remap = [new ChannelRemap(Ch(1), Ch(2)), new ChannelRemap(Ch(1), Ch(3))] });

    [Fact]
    public void ForceAndRemap_AreFlaggedInconsistent()
    {
        Assert.False(new ChannelMapping { Force = Ch(1), Remap = [new ChannelRemap(Ch(2), Ch(3))] }.IsConsistent);
        Assert.True(new ChannelMapping { Force = Ch(1) }.IsConsistent);
    }

    [Fact]
    public void Equality_ComparesContents()
    {
        Assert.Equal(new ChannelMapping { Only = [Ch(2), Ch(1)] }, new ChannelMapping { Only = [Ch(1), Ch(2)] });
        Assert.Equal(ChannelMapping.ForceTo(Ch(3)).GetHashCode(), ChannelMapping.ForceTo(Ch(3)).GetHashCode());
        Assert.NotEqual(ChannelMapping.ForceTo(Ch(3)), ChannelMapping.Preserve);
    }
}
