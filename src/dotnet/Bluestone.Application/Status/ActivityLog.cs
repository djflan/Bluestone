using System.Diagnostics;

namespace Bluestone.Application.Status;

/// <summary>
/// What Bluestone is doing now. Long-running work (opening and saving projects, export, plugin scans, opening devices,
/// starting the engine) reports itself here from any thread, so the status line can say what is happening and the
/// Messages history can record how it ended. Real-time threads never report directly.
/// </summary>
public sealed class ActivityLog
{
    /// <summary>The shortest interval between progress notifications for one activity, so screen readers and the UI are not flooded.</summary>
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private readonly List<RunningActivity> _running = [];
    private readonly Func<TimeSpan> _clock;

    /// <param name="clock">The time source for durations and rate limiting; the monotonic clock by default.</param>
    public ActivityLog(Func<TimeSpan>? clock = null) => _clock = clock ?? (() => Stopwatch.GetElapsedTime(0));

    /// <summary>Raised on the reporting thread when an activity starts, reports progress (rate limited), or ends.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised once per activity when it ends, on the thread that ended it, after <see cref="Running"/> no longer lists it.</summary>
    public event EventHandler<ActivityEndedEventArgs>? Ended;

    /// <summary>The activities in progress, oldest first.</summary>
    public IReadOnlyList<RunningActivity> Running
    {
        get
        {
            lock (_gate)
            {
                return [.. _running];
            }
        }
    }

    /// <summary>The newest activity in progress, which the status line shows; null when nothing is running.</summary>
    public RunningActivity? Current
    {
        get
        {
            lock (_gate)
            {
                return _running.Count == 0 ? null : _running[^1];
            }
        }
    }

    /// <summary>Starts an activity. <paramref name="text"/> says what is happening, e.g. "Opening demo.bluestone…".</summary>
    /// <remarks>End it with <see cref="RunningActivity.Complete"/>, <see cref="RunningActivity.Fail"/>, or by disposing it.</remarks>
    public RunningActivity Begin(string source, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var activity = new RunningActivity(this, source, text, _clock());
        lock (_gate)
        {
            _running.Add(activity);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return activity;
    }

    internal TimeSpan Now => _clock();

    internal void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    internal void End(RunningActivity activity, ActivityOutcome outcome, string text, bool hasSummary)
    {
        lock (_gate)
        {
            _running.Remove(activity);
        }

        Ended?.Invoke(this, new ActivityEndedEventArgs(activity, outcome, text, _clock() - activity.Started) { HasSummary = hasSummary });
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>One activity in progress. Thread-safe; ending it more than once has no further effect.</summary>
public sealed class RunningActivity : IDisposable
{
    private readonly ActivityLog _log;
    private readonly Lock _gate = new();
    private string _text;
    private double? _progress;
    private TimeSpan _lastNotified;
    private bool _ended;

    internal RunningActivity(ActivityLog log, string source, string text, TimeSpan started)
    {
        _log = log;
        Source = source;
        _text = text;
        Started = started;
        _lastNotified = started;
    }

    /// <summary>The area doing the work, e.g. "Open" or "Plugins"; the Source column in Messages.</summary>
    public string Source { get; }

    /// <summary>When the activity started, on the log's clock.</summary>
    public TimeSpan Started { get; }

    /// <summary>What is happening now.</summary>
    public string Text
    {
        get
        {
            lock (_gate)
            {
                return _text;
            }
        }
    }

    /// <summary>How far along it is, from 0 to 1, when known.</summary>
    public double? Progress
    {
        get
        {
            lock (_gate)
            {
                return _progress;
            }
        }
    }

    /// <summary>Updates what is happening. Listeners hear about it at most every <see cref="ActivityLog.ReportInterval"/>.</summary>
    public void Report(string text, double? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        bool notify;
        lock (_gate)
        {
            if (_ended)
            {
                return;
            }

            _text = text;
            _progress = progress is { } p ? Math.Clamp(p, 0, 1) : null;
            var now = _log.Now;
            notify = now - _lastNotified >= ActivityLog.ReportInterval;
            if (notify)
            {
                _lastNotified = now;
            }
        }

        if (notify)
        {
            _log.Notify();
        }
    }

    /// <summary>Ends the activity successfully. <paramref name="summary"/> is what Messages records; by default the activity's text and duration.</summary>
    public void Complete(string? summary = null) => End(ActivityOutcome.Completed, summary);

    /// <summary>Ends the activity with a problem, which Messages records as an error.</summary>
    public void Fail(string problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);
        End(ActivityOutcome.Failed, problem);
    }

    /// <summary>Completes the activity if it has not ended.</summary>
    public void Dispose() => Complete();

    private void End(ActivityOutcome outcome, string? text)
    {
        string described;
        lock (_gate)
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            described = text ?? _text;
        }

        _log.End(this, outcome, described, hasSummary: text is not null);
    }
}

public enum ActivityOutcome
{
    Completed,
    Failed,
}

/// <summary>
/// How an activity ended. <see cref="Text"/> is the summary it was completed with, or its last text when it was
/// completed without one; for a failure it is the problem.
/// </summary>
public sealed class ActivityEndedEventArgs(RunningActivity activity, ActivityOutcome outcome, string text, TimeSpan duration) : EventArgs
{
    public RunningActivity Activity { get; } = activity;

    public ActivityOutcome Outcome { get; } = outcome;

    public string Text { get; } = text;

    /// <summary>Whether the activity was completed with its own summary rather than its last progress text.</summary>
    public bool HasSummary { get; init; }

    public TimeSpan Duration { get; } = duration;
}
