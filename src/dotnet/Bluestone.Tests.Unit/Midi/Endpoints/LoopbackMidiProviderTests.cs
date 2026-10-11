using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;

namespace Bluestone.Tests.Unit.Midi.Endpoints;

public sealed class LoopbackMidiProviderTests : IDisposable
{
    private static readonly byte[] NoteOn = [0x90, 0x3C, 0x64];

    private readonly VirtualClock _clock = new(TimeSpan.FromSeconds(10));
    private readonly LoopbackMidiProvider _provider;

    public LoopbackMidiProviderTests() => _provider = new LoopbackMidiProvider(_clock);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _provider.Dispose();

    [Fact]
    public void CreatePort_ExposesOutputAndInputEndpoints()
    {
        var port = _provider.CreatePort("Bus 1", "bus1");

        var endpoints = _provider.GetEndpoints();

        Assert.Equal([EndpointDirection.Output, EndpointDirection.Input], endpoints.Select(e => e.Direction));
        Assert.All(endpoints, e => Assert.Equal("Bus 1", e.DisplayName));
        Assert.Equal(new EndpointId("loopback", "bus1/out"), port.OutputId);
        Assert.Equal(port.InputId, endpoints[1].Id);
    }

    [Fact]
    public async Task Send_RecordsAndDeliversToInput()
    {
        var port = _provider.CreatePort("Bus");
        using var output = await _provider.OpenOutputAsync(port.OutputId, Ct);
        using var input = await _provider.OpenInputAsync(port.InputId, Ct);
        var received = new List<(byte[] Bytes, TimeSpan Time)>();
        input.SetReceiver((message, time) => received.Add((message.ToArray(), time)));

        Assert.Equal(SendResult.Sent, output.Send(NoteOn, MidiTimestamp.Immediate));
        Assert.Equal(SendResult.Sent, output.Send(NoteOn, MidiTimestamp.At(TimeSpan.FromSeconds(11))));

        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11)], received.Select(r => r.Time));
        Assert.Equal(2, port.Sent.Count);
        Assert.Equal(NoteOn, port.Sent[0].Bytes);
        Assert.Equal(MidiTimestamp.At(TimeSpan.FromSeconds(11)), port.Sent[1].Requested);
        Assert.Equal(TimeSpan.FromSeconds(10), port.Sent[1].SentAt);
    }

    [Fact]
    public async Task Send_RejectsMalformedMessages()
    {
        var port = _provider.CreatePort("Bus");
        using var output = await _provider.OpenOutputAsync(port.OutputId, Ct);

        Assert.Equal(SendResult.Rejected, output.Send([0x90, 0x3C], MidiTimestamp.Immediate));
        Assert.Equal(SendResult.Rejected, output.Send([0x3C, 0x64], MidiTimestamp.Immediate));
        Assert.Empty(port.Sent);
    }

    [Fact]
    public async Task Send_RejectsSysExWhenUnsupported()
    {
        using var provider = new LoopbackMidiProvider(_clock, EndpointCapabilities.None);
        var port = provider.CreatePort("Bus");
        using var output = await provider.OpenOutputAsync(port.OutputId, Ct);

        Assert.Equal(SendResult.Rejected, output.Send([0xF0, 0x7E, 0xF7], MidiTimestamp.Immediate));
        Assert.Equal(SendResult.Sent, output.Send(NoteOn, MidiTimestamp.Immediate));
    }

    [Fact]
    public async Task FailNextSends_SimulatesBackpressure()
    {
        var port = _provider.CreatePort("Bus");
        using var output = await _provider.OpenOutputAsync(port.OutputId, Ct);
        port.FailNextSends(2);

        Assert.Equal(SendResult.QueueFull, output.Send(NoteOn, MidiTimestamp.Immediate));
        Assert.Equal(SendResult.QueueFull, output.Send(NoteOn, MidiTimestamp.Immediate));
        Assert.Equal(SendResult.Sent, output.Send(NoteOn, MidiTimestamp.Immediate));
        Assert.Single(port.Sent);
    }

    [Fact]
    public async Task RemovePort_DisconnectsOpenHandles()
    {
        var port = _provider.CreatePort("Bus", "bus");
        using var output = await _provider.OpenOutputAsync(port.OutputId, Ct);
        var states = new List<EndpointState>();
        output.StateChanged += (_, e) => states.Add(e.State);
        var changes = 0;
        _provider.EndpointsChanged += (_, _) => changes++;

        _provider.RemovePort("bus");

        Assert.Equal(EndpointState.Disconnected, output.State);
        Assert.Equal([EndpointState.Disconnected], states);
        Assert.Equal(SendResult.Disconnected, output.Send(NoteOn, MidiTimestamp.Immediate));
        Assert.Empty(_provider.GetEndpoints());
        Assert.Equal(1, changes);
        await Assert.ThrowsAsync<EndpointUnavailableException>(async () => await _provider.OpenOutputAsync(port.OutputId, Ct));
    }

    [Fact]
    public async Task ReplugWithSameKey_RestoresTheSameEndpointId()
    {
        var original = _provider.CreatePort("Bus", "bus");
        _provider.RemovePort("bus");

        var replugged = _provider.CreatePort("Bus (renamed)", "bus");
        using var output = await _provider.OpenOutputAsync(original.OutputId, Ct);

        Assert.Equal(original.OutputId, replugged.OutputId);
        Assert.Equal(EndpointState.Open, output.State);
    }

    [Fact]
    public void RenamePort_RaisesEndpointsChanged()
    {
        _provider.CreatePort("Old", "k");
        var changes = 0;
        _provider.EndpointsChanged += (_, _) => changes++;

        _provider.RenamePort("k", "New");

        Assert.Equal(1, changes);
        Assert.All(_provider.GetEndpoints(), e => Assert.Equal("New", e.DisplayName));
    }

    [Fact]
    public async Task Dispose_ClosesHandleOnceAndStopsDelivery()
    {
        var port = _provider.CreatePort("Bus");
        var output = await _provider.OpenOutputAsync(port.OutputId, Ct);
        var input = await _provider.OpenInputAsync(port.InputId, Ct);
        var received = 0;
        input.SetReceiver((_, _) => received++);
        var closed = 0;
        output.StateChanged += (_, e) => closed += e.State == EndpointState.Closed ? 1 : 0;

        output.Dispose();
        output.Dispose();
        input.Dispose();
        port.Inject(NoteOn);

        Assert.Equal(1, closed);
        Assert.Equal(EndpointState.Closed, output.State);
        Assert.Equal(SendResult.Closed, output.Send(NoteOn, MidiTimestamp.Immediate));
        Assert.Equal(0, received);
    }

    [Fact]
    public async Task ClosedHandle_StaysClosedWhenPortIsRemoved()
    {
        var port = _provider.CreatePort("Bus", "bus");
        var output = await _provider.OpenOutputAsync(port.OutputId, Ct);
        output.Dispose();

        _provider.RemovePort("bus");

        Assert.Equal(EndpointState.Closed, output.State);
    }

    [Fact]
    public async Task Open_RejectsUnknownOrWrongDirectionIds()
    {
        var port = _provider.CreatePort("Bus");

        await Assert.ThrowsAsync<EndpointUnavailableException>(async () => await _provider.OpenOutputAsync(port.InputId, Ct));
        await Assert.ThrowsAsync<EndpointUnavailableException>(async () => await _provider.OpenInputAsync(port.OutputId, Ct));
        await Assert.ThrowsAsync<EndpointUnavailableException>(async () => await _provider.OpenOutputAsync(new EndpointId("other", "bus/out"), Ct));
    }

    [Fact]
    public async Task Inject_SimulatesDeviceTransmission()
    {
        var port = _provider.CreatePort("Bus");
        using var input = await _provider.OpenInputAsync(port.InputId, Ct);
        byte[]? received = null;
        input.SetReceiver((message, _) => received = message.ToArray());

        port.Inject([0xF8]);

        Assert.Equal([0xF8], received);
    }

    [Fact]
    public async Task Send_IsSafeFromManyThreads()
    {
        var port = _provider.CreatePort("Bus");
        using var output = await _provider.OpenOutputAsync(port.OutputId, Ct);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                output.Send(NoteOn, MidiTimestamp.Immediate);
            }
        }, Ct)));

        Assert.Equal(4000, port.Sent.Count);
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("p", " ")]
    public void EndpointId_RejectsBlankParts(string provider, string value) =>
        Assert.ThrowsAny<ArgumentException>(() => new EndpointId(provider, value));
}
