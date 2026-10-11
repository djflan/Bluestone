using System.Runtime.Versioning;
using Bluestone.Midi.Endpoints;
using Bluestone.Platform.Alsa;

namespace Bluestone.Tests.Integration.Platform;

/// <summary>
/// Exercises the real ALSA sequencer on Linux using only Bluestone's own virtual port and the kernel's
/// "Midi Through" loopback port. These tests never send to physical hardware.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class AlsaProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AlsaProvider CreateProvider()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("ALSA is only available on Linux.");
        }

        try
        {
            return new AlsaProvider("Bluestone Tests");
        }
        catch (EndpointUnavailableException ex)
        {
            Assert.Skip($"The ALSA sequencer is unavailable: {ex.Message}");
            throw;
        }
    }

    [Fact]
    public void Provider_EnumeratesOutputsWithUniqueIds()
    {
        using var provider = CreateProvider();

        var endpoints = provider.GetEndpoints();

        foreach (var endpoint in endpoints)
        {
            TestContext.Current.SendDiagnosticMessage($"{endpoint.Id} ({endpoint.Transport})");
        }

        Assert.All(endpoints, e =>
        {
            Assert.Equal(AlsaProvider.ProviderId, e.Id.Provider);
            Assert.Equal(EndpointDirection.Output, e.Direction);
            Assert.Equal(EndpointCapabilities.SystemExclusive, e.Capabilities);
        });
        Assert.Equal(endpoints.Count, endpoints.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public async Task VirtualOutput_IsListed_AndAcceptsMessages()
    {
        using var provider = CreateProvider();

        var id = provider.CreateVirtualOutput("Bluestone Test Out");
        using var output = await provider.OpenOutputAsync(id, Ct);

        Assert.Contains(provider.GetEndpoints(), e => e.Id == id && e.Transport == EndpointTransport.Virtual);
        Assert.Equal(SendResult.Sent, output.Send([0x90, 0x3C, 0x64], MidiTimestamp.Immediate));
        Assert.Equal(SendResult.Sent, output.Send([0xF0, 0x7D, 0x01, 0x02, 0xF7], MidiTimestamp.Immediate));
        Assert.Equal(SendResult.Rejected, output.Send([0x90, 0x3C], MidiTimestamp.Immediate));
    }

    [Fact]
    public async Task MidiThrough_AcceptsShortAndSysExMessages()
    {
        using var provider = CreateProvider();
        var through = provider.GetEndpoints().FirstOrDefault(e => e.Id.Value.StartsWith("Midi Through:", StringComparison.Ordinal));
        if (through is null)
        {
            Assert.Skip("The snd-seq-dummy (Midi Through) module is not loaded.");
        }

        using var output = await provider.OpenOutputAsync(through.Id, Ct);

        Assert.Equal(SendResult.Sent, output.Send([0xB0, 0x7B, 0x00], MidiTimestamp.Immediate));
        var sysEx = new byte[2048];
        sysEx[0] = 0xF0;
        sysEx[1] = 0x7D;
        sysEx[^1] = 0xF7;
        Assert.Equal(SendResult.Sent, output.Send(sysEx, MidiTimestamp.Immediate));
    }

    [Fact]
    public async Task OpenUnknownOutput_AndInput_Throw()
    {
        using var provider = CreateProvider();

        await Assert.ThrowsAsync<EndpointUnavailableException>(
            async () => await provider.OpenOutputAsync(new EndpointId(AlsaProvider.ProviderId, "No such client:No such port"), Ct));
        await Assert.ThrowsAsync<EndpointUnavailableException>(
            async () => await provider.OpenInputAsync(new EndpointId(AlsaProvider.ProviderId, "Any"), Ct));
    }

    [Fact]
    public async Task DisposingProvider_ClosesOpenOutputs()
    {
        var provider = CreateProvider();
        var output = await provider.OpenOutputAsync(provider.CreateVirtualOutput("Bluestone Test Out"), Ct);

        provider.Dispose();

        Assert.Equal(EndpointState.Closed, output.State);
        Assert.Equal(SendResult.Closed, output.Send([0xB0, 0x7B, 0x00], MidiTimestamp.Immediate));
        Assert.Throws<ObjectDisposedException>(provider.GetEndpoints);
    }
}
