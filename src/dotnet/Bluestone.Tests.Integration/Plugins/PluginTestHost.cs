using System.Diagnostics;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Tests.Integration.Plugins;

/// <summary>
/// One test's plugin-hosting sandbox: a unique directory under the test binaries, a manager whose worker processes
/// are tracked, and cleanup that fails the test if any worker process outlives the manager.
/// </summary>
internal sealed class PluginTestHost : IAsyncDisposable
{
    public static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);

    private readonly HashSet<int> _processIds = [];

    public PluginTestHost(Func<PluginHostOptions, PluginHostOptions>? configure = null)
    {
        Directory = Path.Combine(AppContext.BaseDirectory, "plugin-test-files", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        var options = new PluginHostOptions
        {
            DataDirectory = Path.Combine(Directory, "data"),
            HandshakeTimeout = TimeSpan.FromSeconds(30),
            RequestTimeout = TimeSpan.FromSeconds(15),
            HeartbeatInterval = TimeSpan.FromMilliseconds(250),
            HeartbeatTimeout = TimeSpan.FromSeconds(15),

            // Tests are not real time: a generous bounded wait keeps results deterministic.
            CollectWait = TimeSpan.FromSeconds(5),
        };
        Manager = new PluginHostManager(configure?.Invoke(options) ?? options);
    }

    public string Directory { get; }

    public PluginHostManager Manager { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static PluginIdentity Gain { get; } = Reference("reference.gain", "Reference Gain", PluginKind.AudioEffect);

    public static PluginIdentity Sine { get; } = Reference("reference.sine", "Reference Sine", PluginKind.Instrument);

    public static PluginIdentity Transpose { get; } = Reference("reference.transpose", "Reference Transpose", PluginKind.MidiEffect);

    public static PluginIdentity Reference(string pluginId, string name, PluginKind kind) =>
        new("bluestone-reference", "bluestone.reference", pluginId, name, "Bluestone", kind, "1.0.0");

    public static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Kills a worker from outside, as a crash would: a real <see cref="Process.Kill()"/>, no notification.</summary>
    public static void KillProcess(int processId)
    {
        using var process = Process.GetProcessById(processId);
        process.Kill();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(10)), $"Process {processId} did not die.");
    }

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < timeout, $"Timed out after {timeout.TotalSeconds} s waiting for {what}.");
            await Task.Delay(20, Ct);
        }
    }

    public static Task WaitForStateAsync(PluginInstance instance, PluginInstanceState state) =>
        WaitUntilAsync(() => instance.Status.State == state, StatusTimeout, $"{instance.Plugin.PluginId} to become {state} (is {instance.Status})");

    public async Task<PluginInstance> CreateAsync(PluginInstanceRequest request)
    {
        var instance = await Manager.CreateInstanceAsync(request, Ct);
        Track(instance);
        return instance;
    }

    public async Task<PluginInstance> CreateRunningAsync(PluginInstanceRequest request)
    {
        var instance = await CreateAsync(request);
        Assert.True(instance.Status.State == PluginInstanceState.Running, $"Expected Running but was {instance.Status}: {instance.Status.Message}");
        return instance;
    }

    /// <summary>Remembers the instance's current worker so cleanup can prove it is gone.</summary>
    public void Track(PluginInstance instance)
    {
        if (instance.WorkerProcessId is { } pid)
        {
            lock (_processIds)
            {
                _processIds.Add(pid);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Manager.DisposeAsync();
            int[] pids;
            lock (_processIds)
            {
                pids = [.. _processIds];
            }

            var survivors = new List<int>();
            foreach (var pid in pids)
            {
                var watch = Stopwatch.StartNew();
                while (IsRunning(pid) && watch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await Task.Delay(20);
                }

                if (IsRunning(pid))
                {
                    survivors.Add(pid);
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                    {
                    }
                }
            }

            Assert.True(survivors.Count == 0, $"Worker processes outlived the manager: {string.Join(", ", survivors)}");
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>Drives <see cref="PluginInstance.ProcessBlock"/> with preallocated buffers, like an audio thread would.</summary>
internal sealed class BlockDriver
{
    private readonly PluginInstance _instance;
    private readonly PluginEvent[] _outputEvents = new PluginEvent[256];

    public BlockDriver(PluginInstance instance, int frames)
    {
        _instance = instance;
        Frames = frames;
        Input = new float[instance.Request.InputChannels * frames];
        Output = new float[instance.Request.OutputChannels * frames];
    }

    public int Frames { get; }

    public float[] Input { get; }

    public float[] Output { get; }

    public long Position { get; private set; }

    public ReadOnlySpan<PluginEvent> OutputEvents => _outputEvents.AsSpan(0, LastOutcome.OutputEventCount);

    public BlockOutcome LastOutcome { get; private set; }

    public BlockOutcome Process(ReadOnlySpan<PluginEvent> events = default)
    {
        LastOutcome = _instance.ProcessBlock(Frames, Input, Output, events, _outputEvents, new TransportState(Position, 120, true));
        Position += Frames;
        return LastOutcome;
    }

    public float[] Channel(int channel) => Output.AsSpan(channel * Frames, Frames).ToArray();
}
