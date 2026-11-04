using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading.Channels;
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

    private static (EndpointDescriptor Input, EndpointDescriptor Output) RequireLoopback(WinMmProvider provider)
    {
        var endpoints = provider.GetEndpoints()
            .Where(e => e.DisplayName.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Any(e => e.Direction == EndpointDirection.Input)
                && group.Any(e => e.Direction == EndpointDirection.Output));
        if (endpoints is null)
        {
            Assert.Skip("No WinMM MIDI loopback input and output are installed.");
        }

        return (endpoints.First(e => e.Direction == EndpointDirection.Input), endpoints.First(e => e.Direction == EndpointDirection.Output));
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
    public void Provider_EnumeratesInputsAndOutputsWithUniqueIds()
    {
        RequireWindows();
        using var provider = new WinMmProvider();

        var endpoints = provider.GetEndpoints();
        Assert.SkipUnless(endpoints.Any(e => e.Direction == EndpointDirection.Input), "No WinMM MIDI input devices are available.");

        foreach (var endpoint in endpoints)
        {
            TestContext.Current.SendDiagnosticMessage($"{endpoint.Id} ({endpoint.Transport})");
        }

        Assert.All(endpoints, e =>
        {
            Assert.Equal(WinMmProvider.ProviderId, e.Id.Provider);
            Assert.Equal(EndpointCapabilities.SystemExclusive, e.Capabilities);
        });
        Assert.Equal(endpoints.Count, endpoints.Select(e => e.Id).Distinct().Count());
        Assert.Contains(endpoints, e => e.Direction == EndpointDirection.Input);
        Assert.Contains(endpoints, e => e.Direction == EndpointDirection.Output);
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
    public async Task OpenUnknownInput_Throws()
    {
        RequireWindows();
        using var provider = new WinMmProvider();

        await Assert.ThrowsAsync<EndpointUnavailableException>(
            async () => await provider.OpenInputAsync(new EndpointId(WinMmProvider.ProviderId, "input:No such device"), Ct));
    }

    [Fact]
    public async Task Loopback_ReturnsShortAndSystemExclusiveMessagesWithTimestamps()
    {
        RequireWindows();
        using var provider = new WinMmProvider();
        var (inputEndpoint, outputEndpoint) = RequireLoopback(provider);
        using var input = await provider.OpenInputAsync(inputEndpoint.Id, Ct);
        using var output = await provider.OpenOutputAsync(outputEndpoint.Id, Ct);
        var received = Channel.CreateUnbounded<(byte[] Message, TimeSpan Timestamp)>();
        input.SetReceiver((message, timestamp) => received.Writer.TryWrite((message.ToArray(), timestamp)));

        byte[] controlChange = [0xB0, 0x7B, 0x00];
        byte[] systemExclusive = [0xF0, 0x7D, 0x01, 0x02, 0xF7];
        Assert.Equal(SendResult.Sent, output.Send(controlChange, MidiTimestamp.Immediate));
        var shortResult = await received.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(controlChange, shortResult.Message);
        Assert.True(shortResult.Timestamp > TimeSpan.Zero);

        Assert.Equal(SendResult.Sent, output.Send(systemExclusive, MidiTimestamp.Immediate));
        var longResult = await received.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(systemExclusive, longResult.Message);
        Assert.True(longResult.Timestamp >= shortResult.Timestamp);
    }

    [Fact]
    public async Task Provider_RaisesEndpointsChangedWhenMidiServicesAddsAndRemovesALoopback()
    {
        RequireWindows();
        var midi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows MIDI Services", "Tools", "midi.exe");
        Assert.SkipUnless(File.Exists(midi), "Windows MIDI Services command-line tools are not installed.");

        using var provider = new WinMmProvider();
        using var changed = new ManualResetEventSlim();
        provider.EndpointsChanged += (_, _) => changed.Set();
        var name = $"BSHotplug{Guid.NewGuid():N}"[..31];
        string? associationId = null;
        var created = false;
        try
        {
            var output = await RunMidiAsync(midi, $"basic-loopback create -n \"{name}\"");
            created = true;
            var match = Regex.Match(output, @"Association Id\s+\{(?<id>[0-9a-fA-F-]+)\}");
            if (!match.Success)
            {
                var listing = await RunMidiAsync(midi, "basic-loopback list");
                var entry = listing.Split('\n').FirstOrDefault(line => line.Contains(name, StringComparison.Ordinal));
                match = Regex.Match(entry ?? string.Empty, @"\{(?<id>[0-9a-fA-F-]+)\}");
            }

            Assert.True(match.Success, $"Windows MIDI Services did not return the loopback association id for {name}: {output}");
            associationId = match.Groups["id"].Value;
            Assert.True(changed.Wait(TimeSpan.FromSeconds(10), Ct), "The provider did not report the added loopback.");
            changed.Reset();
            Assert.Contains(provider.GetEndpoints(), e => e.DisplayName == name);
        }
        finally
        {
            if (created && associationId is not null)
            {
                _ = await RunMidiAsync(midi, $"basic-loopback remove -i \"{{{associationId}}}\"");
            }
        }

        Assert.True(changed.Wait(TimeSpan.FromSeconds(10), Ct), "The provider did not report the removed loopback.");
        Assert.DoesNotContain(provider.GetEndpoints(), e => e.DisplayName == name);
    }

    private static async Task<string> RunMidiAsync(string executable, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var output = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"Windows MIDI Services failed: {error}");
        return output;
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
