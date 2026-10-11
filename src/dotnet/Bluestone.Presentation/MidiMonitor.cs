using System.Collections.Concurrent;
using Bluestone.Midi.Endpoints;

namespace Bluestone.Presentation;

/// <summary>A received message, already formatted for display.</summary>
public sealed record MonitorEntry(TimeSpan Time, string Text);

/// <summary>
/// Shows what is sent to a monitored endpoint. Receiving happens on an adapter thread and only
/// enqueues; the UI drains the queue on its own timer. The queue is bounded and drops the oldest
/// entries when the UI falls behind.
/// </summary>
public sealed class MidiMonitor : IDisposable
{
    private const int Capacity = 2048;

    private readonly IMidiInput _input;
    private readonly ConcurrentQueue<(byte[] Bytes, TimeSpan Time)> _pending = new();

    public MidiMonitor(IMidiInput input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _input.SetReceiver(OnReceive);
    }

    public string EndpointName => _input.Endpoint.DisplayName;

    public IReadOnlyList<MonitorEntry> Drain(int max = 256)
    {
        var entries = new List<MonitorEntry>();
        while (entries.Count < max && _pending.TryDequeue(out var item))
        {
            entries.Add(new MonitorEntry(item.Time, Formatting.Message(item.Bytes)));
        }

        return entries;
    }

    public void Dispose()
    {
        _input.SetReceiver(null);
        _input.Dispose();
    }

    private void OnReceive(ReadOnlySpan<byte> message, TimeSpan timestamp)
    {
        _pending.Enqueue((message.ToArray(), timestamp));
        while (_pending.Count > Capacity && _pending.TryDequeue(out _))
        {
        }
    }
}
