using System.Diagnostics.Tracing;

namespace Bluestone.Benchmarks;

/// <summary>Every garbage collection's suspension, from the runtime's GCSuspendEEBegin to GCRestartEEEnd events.</summary>
internal sealed class GcPauseListener : EventListener
{
    private const int SuspendBegin = 9;
    private const int RestartEnd = 3;
    private readonly Lock _gate = new();
    private readonly List<TimeSpan> _pauses = [];
    private DateTime? _suspended;

    public TimeSpan Max
    {
        get
        {
            lock (_gate)
            {
                return _pauses.Count == 0 ? TimeSpan.Zero : _pauses.Max();
            }
        }
    }

    public IReadOnlyList<TimeSpan> Pauses
    {
        get
        {
            lock (_gate)
            {
                return [.. _pauses];
            }
        }
    }

    public int LongerThan(TimeSpan limit)
    {
        lock (_gate)
        {
            return _pauses.Count(p => p > limit);
        }
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(source, EventLevel.Informational, (EventKeywords)1);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        lock (_gate)
        {
            if (data.EventId == SuspendBegin)
            {
                _suspended = data.TimeStamp;
            }
            else if (data.EventId == RestartEnd && _suspended is { } began)
            {
                _pauses.Add(data.TimeStamp - began);
                _suspended = null;
            }
        }
    }
}

/// <summary>Collections and pauses since construction, added to a report as metrics.</summary>
internal sealed class GcWatch : IDisposable
{
    private readonly int _gen0 = GC.CollectionCount(0);
    private readonly int _gen1 = GC.CollectionCount(1);
    private readonly int _gen2 = GC.CollectionCount(2);
    private readonly TimeSpan _paused = GC.GetTotalPauseDuration();
    private readonly GcPauseListener _listener = new();

    public void AddTo(TimingReport report)
    {
        report.Add(new Metric("gc.gen0", "collections", GC.CollectionCount(0) - _gen0));
        report.Add(new Metric("gc.gen1", "collections", GC.CollectionCount(1) - _gen1));
        report.Add(new Metric("gc.gen2", "collections", GC.CollectionCount(2) - _gen2));
        report.Add(new Metric("gc.pause-total", "ms", (GC.GetTotalPauseDuration() - _paused).TotalMilliseconds));
        report.Add(new Metric("gc.pause", "ms", Distribution: Distribution.Of(_listener.Pauses.Select(p => p.TotalMilliseconds))));
        report.Add(new Metric("process.working-set", "MB", Environment.WorkingSet / 1048576.0));
        report.Add(new Metric("gc.committed", "MB", GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0));
    }

    public void Dispose() => _listener.Dispose();
}
