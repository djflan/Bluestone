using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;

namespace Bluestone.Tests.Unit.Plugins;

/// <summary>Host-manager rules that need no worker process.</summary>
public sealed class PluginHostManagerTests : IDisposable
{
    private static readonly PluginIdentity Gain = new("bluestone-reference", "bluestone.reference", "reference.gain", "Gain", "Bluestone", PluginKind.AudioEffect, "1");
    private static readonly PluginIdentity Sine = new("bluestone-reference", "bluestone.reference", "reference.sine", "Sine", "Bluestone", PluginKind.Instrument, "1");

    private readonly TestDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task WithinBluestone_IsRefusedUnlessAllowed_AndUnsupportedEvenThen()
    {
        var request = new PluginInstanceRequest(Gain, 48_000, 64, 1, 1) { Hosting = HostingMode.WithinBluestone };
        await using var strict = new PluginHostManager(new PluginHostOptions { DataDirectory = _directory.File("a") });
        await using var permissive = new PluginHostManager(new PluginHostOptions { DataDirectory = _directory.File("b"), AllowWithinBluestone = true });

        await Assert.ThrowsAsync<InvalidOperationException>(() => strict.CreateInstanceAsync(request, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => permissive.CreateInstanceAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(strict.Instances);
        Assert.Equal(0, strict.WorkerLaunchCount + permissive.WorkerLaunchCount);
    }

    [Fact]
    public async Task BypassInput_IsRefusedForInstrumentsAndMismatchedChannels()
    {
        await using var manager = new PluginHostManager(new PluginHostOptions { DataDirectory = _directory.File("a") });

        await Assert.ThrowsAsync<ArgumentException>(() => manager.CreateInstanceAsync(new PluginInstanceRequest(Sine, 48_000, 64, 0, 2) { FailurePolicy = FailurePolicy.BypassInput }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.CreateInstanceAsync(new PluginInstanceRequest(Gain, 48_000, 64, 1, 2) { FailurePolicy = FailurePolicy.BypassInput }, TestContext.Current.CancellationToken));
        Assert.Equal(0, manager.WorkerLaunchCount);
    }

    [Fact]
    public async Task ASnapshotOfAnotherPlugin_IsRefused()
    {
        await using var manager = new PluginHostManager(new PluginHostOptions { DataDirectory = _directory.File("a") });
        var snapshot = new PluginStateSnapshot(Sine, new PluginStateData("f", []), [], DateTimeOffset.UnixEpoch);

        await Assert.ThrowsAsync<ArgumentException>(() => manager.CreateInstanceAsync(new PluginInstanceRequest(Gain, 48_000, 64, 1, 1) { RestoreFrom = snapshot }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Defaults_HostIndividually_FailToSilence_AndDoNotRestartAutomatically()
    {
        var request = new PluginInstanceRequest(Gain, 48_000, 64, 1, 1);
        var options = new PluginHostOptions();

        Assert.Equal(HostingMode.Individually, request.Hosting);
        Assert.Equal(FailurePolicy.Silence, request.FailurePolicy);
        Assert.False(request.RestartPolicy.AutoRestart);
        Assert.Null(options.PeriodicSnapshotInterval);
        Assert.Equal(2, options.PipelineDepth);
    }

    [Fact]
    public void APipelineDeeperThanTheSlots_IsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginHostManager(new PluginHostOptions { DataDirectory = _directory.File("a"), PipelineDepth = 3, SlotCount = 2 }));
}
