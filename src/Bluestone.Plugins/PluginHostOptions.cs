using System.Collections.Immutable;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Plugins;

/// <summary>Settings for a <see cref="PluginHostManager"/>.</summary>
public sealed record PluginHostOptions
{
    /// <summary>The worker to launch; null finds <c>Bluestone.PluginWorker</c> beside this assembly.</summary>
    public string? WorkerPath { get; init; }

    /// <summary>Where data-plane files go; null uses a fresh directory under the system temporary directory.</summary>
    public string? DataDirectory { get; init; }

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Per-request timeout on the control plane. A timeout means the worker is hung and it is killed.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Silence after which a worker is declared hung and killed.</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long <see cref="PluginInstance.ProcessBlock"/> may spin for a block that is not Done yet. Keep it well below
    /// the block period; when it expires the block is late and the failure policy decides the output.
    /// </summary>
    public TimeSpan CollectWait { get; init; } = TimeSpan.FromMilliseconds(2);

    /// <summary>Blocks in flight: block k is submitted and block k - (depth - 1) collected. 1 to the slot count.</summary>
    public int PipelineDepth { get; init; } = 2;

    public int SlotCount { get; init; } = ExchangeOptions.DefaultSlotCount;

    /// <summary>Capture state from every running instance this often; null (the default) never does.</summary>
    public TimeSpan? PeriodicSnapshotInterval { get; init; }

    /// <summary>Must be set to even ask for <see cref="HostingMode.WithinBluestone"/>, which is still not supported.</summary>
    public bool AllowWithinBluestone { get; init; }

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Extra environment for worker processes. Internal: used by tests to induce a crash at startup.</summary>
    internal IReadOnlyDictionary<string, string>? WorkerEnvironment { get; init; }
}

/// <summary>What to create: a plugin, its audio configuration, and how to host and recover it.</summary>
public sealed record PluginInstanceRequest
{
    public PluginInstanceRequest(PluginIdentity plugin, double sampleRate, int maxBlockFrames, int inputChannels, int outputChannels)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (!(sampleRate is >= ProtocolLimits.MinSampleRate and <= ProtocolLimits.MaxSampleRate))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Unsupported sample rate.");
        }

        _ = new ExchangeOptions(maxBlockFrames, inputChannels, outputChannels);
        Plugin = plugin;
        SampleRate = sampleRate;
        MaxBlockFrames = maxBlockFrames;
        InputChannels = inputChannels;
        OutputChannels = outputChannels;
    }

    public PluginIdentity Plugin { get; }

    public double SampleRate { get; }

    public int MaxBlockFrames { get; }

    public int InputChannels { get; }

    public int OutputChannels { get; }

    /// <summary>
    /// How the instance shares a worker process. <see cref="HostingMode.Individually"/> here; the application chooses
    /// its own default (ADR 0034). <see cref="PluginHostManager.ChangeHostingAsync"/> moves a live instance.
    /// </summary>
    public HostingMode Hosting { get; init; } = HostingMode.Individually;

    public FailurePolicy FailurePolicy { get; init; } = FailurePolicy.Silence;

    public RestartPolicy RestartPolicy { get; init; } = RestartPolicy.Default;

    /// <summary>A snapshot (for example from the project) to restore when the instance is created.</summary>
    public PluginStateSnapshot? RestoreFrom { get; init; }

    /// <summary>The module (VST3 bundle or CLAP file) the plugin comes from; null for the built-in reference plugins.</summary>
    public string? ModulePath { get; init; }

    public int EventCapacity { get; init; } = ExchangeOptions.DefaultEventCapacity;

    public int ParameterChangeCapacity { get; init; } = ExchangeOptions.DefaultParameterChangeCapacity;
}

/// <summary>
/// State captured from a live instance: the plugin's opaque state plus the parameter values at that moment. This is
/// what the application persists and what recovery restores; a crashed plugin cannot be asked for newer state.
/// </summary>
public sealed record PluginStateSnapshot(PluginIdentity Plugin, PluginStateData State, ImmutableArray<ParameterValue> ParameterValues, DateTimeOffset CapturedAt);
