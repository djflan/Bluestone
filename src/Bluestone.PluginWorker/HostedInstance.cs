using System.Diagnostics;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;
using Bluestone.PluginWorker.Native;
using Bluestone.PluginWorker.Plugins;

namespace Bluestone.PluginWorker;

/// <summary>
/// One plugin instance in the worker: the plugin, its exchange, and a dedicated processing thread that runs submitted
/// blocks. The thread spins briefly when idle, then yields, then sleeps 1 ms (see docs/plugin-hosting.md for the CPU
/// versus wake-up latency trade-off). Control calls and processing are serialized by a lock; the worker side may
/// block, the host side never does.
/// </summary>
internal sealed class HostedInstance : IDisposable
{
    private static readonly long SpinTicks = Stopwatch.Frequency / 5000;   // 200 microseconds
    private static readonly long YieldTicks = Stopwatch.Frequency / 200;   // 5 milliseconds

    private readonly Lock _gate = new();
    private readonly IHostedPlugin _plugin;
    private readonly WorkerBlockExchange _exchange;
    private readonly double _sampleRate;
    private readonly float[] _input;
    private readonly float[] _output;
    private readonly PluginEvent[] _events;
    private readonly ParameterChange[] _changes;
    private readonly OutputEventList _outputEvents;
    private readonly Thread _thread;
    private volatile bool _stop;

    public HostedInstance(PluginInstanceId id, IHostedPlugin plugin, WorkerBlockExchange exchange, double sampleRate)
    {
        Id = id;
        _plugin = plugin;
        _exchange = exchange;
        _sampleRate = sampleRate;
        var options = exchange.Options;
        _input = new float[options.InputChannels * options.MaxFrames];
        _output = new float[options.OutputChannels * options.MaxFrames];
        _events = new PluginEvent[options.EventCapacity];
        _changes = new ParameterChange[options.ParameterChangeCapacity];
        _outputEvents = new OutputEventList(options.EventCapacity);
        _thread = new Thread(Run) { IsBackground = true, Name = $"plugin {id}", Priority = ThreadPriority.AboveNormal };
    }

    public PluginInstanceId Id { get; }

    public string ExchangePath => _exchange.Path;

    public void Start() => _thread.Start();

    public bool TrySetParameter(uint id, double value)
    {
        lock (_gate)
        {
            return _plugin.TrySetParameter(id, value);
        }
    }

    /// <summary>The latency the plugin reports now, in frames.</summary>
    public int LatencyFrames
    {
        get
        {
            lock (_gate)
            {
                return _plugin.LatencyFrames;
            }
        }
    }

    public ParameterValue[] GetParameters()
    {
        lock (_gate)
        {
            return [.. _plugin.Parameters.Select(p => new ParameterValue(p.Id, _plugin.GetParameter(p.Id)))];
        }
    }

    public bool IsDisposed => _stop;

    /// <summary>Saves state; false with the plugin's reason when it could not.</summary>
    public bool TrySaveState(out PluginStateData? state, out string? error)
    {
        lock (_gate)
        {
            try
            {
                state = _plugin.SaveState();
                error = null;
                return true;
            }
            catch (InvalidOperationException ex)
            {
                state = null;
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>Opens the plugin's editor; null on success, else why not. Main thread.</summary>
    public string? OpenEditor(string title) =>
        _plugin is NativePlugin native ? native.OpenEditor(title) : $"{_plugin.Identity.DisplayName} has no editor.";

    public void CloseEditor()
    {
        if (_plugin is NativePlugin native)
        {
            native.CloseEditor();
        }
    }

    /// <summary>Loads state; on failure the plugin is at its defaults and the message says why.</summary>
    public string? TryLoadState(PluginStateData state)
    {
        lock (_gate)
        {
            try
            {
                _plugin.LoadState(state);
                return null;
            }
            catch (InvalidDataException ex)
            {
                return ex.Message;
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _exchange.MarkClosed();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(2)))
        {
            // A plugin stuck inside Process: leave the mapping alone, the process is about to go anyway.
            return;
        }

        lock (_gate)
        {
            (_plugin as IDisposable)?.Dispose();
        }

        _exchange.Dispose();
        try
        {
            File.Delete(_exchange.Path);
        }
        catch (IOException)
        {
            // The host also cleans up; a file still mapped on Windows cannot be deleted yet.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Run()
    {
        var idleSince = 0L;
        while (!_stop && !_exchange.IsStopped)
        {
            if (WorkerFaults.IsHung)
            {
                Thread.Sleep(Timeout.Infinite);
            }

            _exchange.Heartbeat();
            if (_exchange.TryBeginBlock(out var block))
            {
                ProcessBlock(ref block);
                idleSince = 0;
                continue;
            }

            var now = Stopwatch.GetTimestamp();
            if (idleSince == 0)
            {
                idleSince = now;
            }

            var idle = now - idleSince;
            if (idle < SpinTicks)
            {
                Thread.SpinWait(20);
            }
            else if (idle < YieldTicks)
            {
                Thread.Yield();
            }
            else
            {
                Thread.Sleep(1);
            }
        }
    }

    private void ProcessBlock(ref WorkerBlock block)
    {
        var frames = block.Frames;
        var options = _exchange.Options;
        var status = BlockStatus.Ok;
        lock (_gate)
        {
            for (var c = 0; c < options.InputChannels; c++)
            {
                block.Input(c).CopyTo(_input.AsSpan(c * frames, frames));
            }

            var eventCount = 0;
            for (var i = 0; i < block.InputEventCount; i++)
            {
                if (block.TryReadInputEvent(i, ref _events[eventCount]))
                {
                    eventCount++;
                }
            }

            var changeCount = 0;
            for (var i = 0; i < block.ParameterChangeCount; i++)
            {
                if (block.TryReadParameterChange(i, out var change))
                {
                    _changes[changeCount++] = change;
                }
            }

            SortByOffset(_events.AsSpan(0, eventCount));
            SortByOffset(_changes.AsSpan(0, changeCount));
            var output = _output.AsSpan(0, options.OutputChannels * frames);
            output.Clear();
            _outputEvents.Clear();
            var started = Stopwatch.GetTimestamp();
            try
            {
                _plugin.Process(new ProcessArgs(frames, _input.AsSpan(0, options.InputChannels * frames), output, _events.AsSpan(0, eventCount), _changes.AsSpan(0, changeCount), block.Transport), _outputEvents);
                if (Stopwatch.GetElapsedTime(started).TotalSeconds > frames / _sampleRate)
                {
                    status = BlockStatus.DeadlineMissed;
                }
            }
#pragma warning disable CA1031 // A plugin failure must become a block status, whatever it is.
            catch (Exception)
#pragma warning restore CA1031
            {
                status = BlockStatus.PluginError;
                output.Clear();
                _outputEvents.Clear();
            }

            for (var c = 0; c < options.OutputChannels; c++)
            {
                output.Slice(c * frames, frames).CopyTo(block.Output(c));
            }

            foreach (ref readonly var e in _outputEvents.Events)
            {
                block.TryWriteOutputEvent(e);
            }

            for (var i = 0; i < _outputEvents.Dropped; i++)
            {
                block.CountDroppedOutputEvent();
            }
        }

        _exchange.CompleteBlock(ref block, status);
    }

    // Stable insertion sorts: blocks hold few events, and these must not allocate.
    private static void SortByOffset(Span<PluginEvent> events)
    {
        for (var i = 1; i < events.Length; i++)
        {
            var current = events[i];
            var j = i - 1;
            while (j >= 0 && events[j].SampleOffset > current.SampleOffset)
            {
                events[j + 1] = events[j];
                j--;
            }

            events[j + 1] = current;
        }
    }

    private static void SortByOffset(Span<ParameterChange> changes)
    {
        for (var i = 1; i < changes.Length; i++)
        {
            var current = changes[i];
            var j = i - 1;
            while (j >= 0 && changes[j].SampleOffset > current.SampleOffset)
            {
                changes[j + 1] = changes[j];
                j--;
            }

            changes[j + 1] = current;
        }
    }
}
