using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;

namespace Bluestone.Tests.Unit.Midi.Endpoints;

public sealed class EndpointDirectoryTests
{
    private sealed class RenamedLoopback(LoopbackMidiProvider inner) : IMidiEndpointProvider
    {
        public string Id => "second";

        public string DisplayName => "Second";

        public string TimingDescription => inner.TimingDescription;

        public event EventHandler? EndpointsChanged
        {
            add => inner.EndpointsChanged += value;
            remove => inner.EndpointsChanged -= value;
        }

        public IReadOnlyList<EndpointDescriptor> GetEndpoints() => inner.GetEndpoints();

        public ValueTask<IMidiOutput> OpenOutputAsync(EndpointId id, CancellationToken cancellationToken = default) => inner.OpenOutputAsync(id, cancellationToken);

        public ValueTask<IMidiInput> OpenInputAsync(EndpointId id, CancellationToken cancellationToken = default) => inner.OpenInputAsync(id, cancellationToken);

        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public async Task Directory_AggregatesProvidersAndDispatchesOpens()
    {
        using var first = new LoopbackMidiProvider(new VirtualClock());
        using var second = new LoopbackMidiProvider(new VirtualClock());
        var port = first.CreatePort("A", "a");
        second.CreatePort("B", "b");
        using var directory = new EndpointDirectory([first, new RenamedLoopback(second)]);
        var changes = 0;
        directory.EndpointsChanged += (_, _) => changes++;

        Assert.Equal(4, directory.GetEndpoints().Count);
        Assert.Equal(2, directory.GetEndpoints(EndpointDirection.Output).Count);
        using var output = await directory.OpenOutputAsync(port.OutputId, TestContext.Current.CancellationToken);
        Assert.Equal("A", output.Endpoint.DisplayName);

        second.CreatePort("C", "c");
        Assert.Equal(1, changes);
        await Assert.ThrowsAsync<EndpointUnavailableException>(async () => await directory.OpenOutputAsync(new EndpointId("nobody", "x"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Directory_RejectsDuplicateProviderIds()
    {
        using var a = new LoopbackMidiProvider(new VirtualClock());
        using var b = new LoopbackMidiProvider(new VirtualClock());

        Assert.Throws<ArgumentException>(() => new EndpointDirectory([a, b]));
    }
}
