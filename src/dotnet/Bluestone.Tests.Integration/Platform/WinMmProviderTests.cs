using System.Runtime.Versioning;
using Bluestone.Midi.Endpoints;
using Bluestone.Platform.Windows;

namespace Bluestone.Tests.Integration.Platform;

/// <summary>
/// Exercises the real WinMM stack on Windows. These tests never send a sounding message: they only
/// open software synthesizers and send messages that must be rejected, or send after closing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinMmProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("WinMM is only available on Windows.");
        }
    }

    private static EndpointDescriptor RequireSoftwareSynth(WinMmProvider provider)
    {
        var synth = provider.GetEndpoints().FirstOrDefault(e => e.Transport == EndpointTransport.Software);
        if (synth is null)
        {
            Assert.Skip("No WinMM software synthesizer is installed.");
        }

        return synth;
    }

    private static async Task<IMidiOutput> OpenSoftwareSynthAsync(WinMmProvider provider)
    {
        var synth = RequireSoftwareSynth(provider);
        try
        {
            return await provider.OpenOutputAsync(synth.Id, Ct);
        }
        catch (EndpointUnavailableException ex)
        {
            // Machines without an audio device (such as CI runners) list the synthesizer but cannot open it.
            Assert.Skip($"The WinMM software synthesizer could not be opened: {ex.Message}");
            throw;
        }
    }

    [Fact]
    public void Provider_EnumeratesOutputsWithUniqueIds()
    {
        RequireWindows();
        using var provider = new WinMmProvider();

        var endpoints = provider.GetEndpoints();

        foreach (var endpoint in endpoints)
        {
            TestContext.Current.SendDiagnosticMessage($"{endpoint.Id} ({endpoint.Transport})");
        }

        Assert.All(endpoints, e =>
        {
            Assert.Equal(WinMmProvider.ProviderId, e.Id.Provider);
            Assert.Equal(EndpointDirection.Output, e.Direction);
            Assert.Equal(EndpointCapabilities.SystemExclusive, e.Capabilities);
        });
        Assert.Equal(endpoints.Count, endpoints.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public async Task OpenUnknownOutput_Throws()
    {
        RequireWindows();
        using var provider = new WinMmProvider();

        await Assert.ThrowsAsync<EndpointUnavailableException>(
            async () => await provider.OpenOutputAsync(new EndpointId(WinMmProvider.ProviderId, "No such device"), Ct));
    }

    [Fact]
    public async Task OpenInput_IsNotSupported()
    {
        RequireWindows();
        using var provider = new WinMmProvider();

        await Assert.ThrowsAsync<EndpointUnavailableException>(
            async () => await provider.OpenInputAsync(new EndpointId(WinMmProvider.ProviderId, "Any"), Ct));
    }

    [Fact]
    public async Task Output_RejectsInvalidMessages_AndClosesOnDispose()
    {
        RequireWindows();
        using var provider = new WinMmProvider();
        var states = new List<EndpointState>();

        var output = await OpenSoftwareSynthAsync(provider);
        output.StateChanged += (_, e) => states.Add(e.State);

        Assert.Equal(EndpointState.Open, output.State);
        Assert.Equal(SendResult.Rejected, output.Send([0x90, 0x3C], MidiTimestamp.Immediate));
        Assert.Equal(SendResult.Rejected, output.Send([0xF0, 0x7E, 0x7F, 0x09, 0x01], MidiTimestamp.Immediate));

        var oversized = new byte[ushort.MaxValue + 1];
        oversized[0] = 0xF0;
        oversized[^1] = 0xF7;
        Assert.Equal(SendResult.Rejected, output.Send(oversized, MidiTimestamp.Immediate));

        output.Dispose();

        Assert.Equal(EndpointState.Closed, output.State);
        Assert.Equal(SendResult.Closed, output.Send([0xB0, 0x7B, 0x00], MidiTimestamp.Immediate));
        Assert.Equal([EndpointState.Closed], states);
    }

    [Fact]
    public async Task SysEx_IsSent_AndBuffersAreReused()
    {
        RequireWindows();
        using var provider = new WinMmProvider();
        using var output = await OpenSoftwareSynthAsync(provider);

        // GM System On resets the synth; it makes no sound. Sending more messages than there are
        // buffers proves finished buffers are reclaimed, and a 2 KB message proves a buffer can grow.
        byte[] gmOn = [0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7];
        var large = new byte[2048];
        large[0] = 0xF0;
        large[1] = 0x7D; // non-commercial manufacturer ID: devices ignore it
        large[^1] = 0xF7;

        for (var i = 0; i < 3 * 8; i++)
        {
            var message = i % 4 == 3 ? large : gmOn;
            Assert.Equal(SendResult.Sent, await SendWhenReadyAsync(output, message));
        }
    }

    private static async Task<SendResult> SendWhenReadyAsync(IMidiOutput output, byte[] message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            var result = output.Send(message, MidiTimestamp.Immediate);
            if (result != SendResult.QueueFull || DateTime.UtcNow > deadline)
            {
                return result;
            }

            await Task.Delay(5, Ct);
        }
    }

    [Fact]
    public async Task DisposingProvider_ClosesOpenOutputs()
    {
        RequireWindows();
        var provider = new WinMmProvider();
        var output = await OpenSoftwareSynthAsync(provider);

        provider.Dispose();

        Assert.Equal(EndpointState.Closed, output.State);
        Assert.Throws<ObjectDisposedException>(provider.GetEndpoints);
    }
}
