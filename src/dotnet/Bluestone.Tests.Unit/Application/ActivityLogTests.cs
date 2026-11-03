using Bluestone.Application.Status;

namespace Bluestone.Tests.Unit.Application;

public sealed class ActivityLogTests
{
    private TimeSpan _now = TimeSpan.FromSeconds(10);

    private ActivityLog NewLog() => new(() => _now);

    [Fact]
    public void Begin_ListsTheActivity_AndTheNewestIsCurrent()
    {
        var log = NewLog();
        var changes = 0;
        log.Changed += (_, _) => changes++;

        var first = log.Begin("Open", "Opening a.bluestone…");
        var second = log.Begin("Plugins", "Scanning plugins…");

        Assert.Equal([first, second], log.Running);
        Assert.Same(second, log.Current);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Complete_EndsOnce_WithItsDuration()
    {
        var log = NewLog();
        var ended = new List<ActivityEndedEventArgs>();
        log.Ended += (_, e) => ended.Add(e);
        var activity = log.Begin("Save", "Saving a.bluestone…");

        _now += TimeSpan.FromMilliseconds(120);
        activity.Complete();
        activity.Complete("ignored");
        activity.Fail("ignored");
        activity.Dispose();

        var end = Assert.Single(ended);
        Assert.Equal(ActivityOutcome.Completed, end.Outcome);
        Assert.Equal("Saving a.bluestone…", end.Text);
        Assert.False(end.HasSummary);
        Assert.Equal(TimeSpan.FromMilliseconds(120), end.Duration);
        Assert.Empty(log.Running);
        Assert.Null(log.Current);
    }

    [Fact]
    public void CompleteWithASummary_AndFail_SayHowItEnded()
    {
        var log = NewLog();
        var ended = new List<ActivityEndedEventArgs>();
        log.Ended += (_, e) => ended.Add(e);

        log.Begin("Export", "Exporting a.mid…").Complete("Exported a.mid.");
        log.Begin("Open", "Opening b.bluestone…").Fail("The file is damaged.");

        Assert.Equal((ActivityOutcome.Completed, "Exported a.mid.", true), (ended[0].Outcome, ended[0].Text, ended[0].HasSummary));
        Assert.Equal((ActivityOutcome.Failed, "The file is damaged."), (ended[1].Outcome, ended[1].Text));
    }

    [Fact]
    public void Dispose_CompletesAnActivityThatWasNotEnded()
    {
        var log = NewLog();
        ActivityEndedEventArgs? ended = null;
        log.Ended += (_, e) => ended = e;

        using (log.Begin("Audio", "Opening the audio device…"))
        {
        }

        Assert.Equal(ActivityOutcome.Completed, ended?.Outcome);
    }

    [Fact]
    public void Report_UpdatesAtOnce_ButNotifiesAtMostEveryReportInterval()
    {
        var log = NewLog();
        var activity = log.Begin("Plugins", "Scanning plugins…");
        var changes = 0;
        log.Changed += (_, _) => changes++;

        activity.Report("Scanning plugins (1 of 3)…", 0.2);
        _now += ActivityLog.ReportInterval / 2;
        activity.Report("Scanning plugins (2 of 3)…", 1.4);

        Assert.Equal(0, changes);
        Assert.Equal("Scanning plugins (2 of 3)…", activity.Text);
        Assert.Equal(1, activity.Progress);

        _now += ActivityLog.ReportInterval;
        activity.Report("Scanning plugins (3 of 3)…");

        Assert.Equal(1, changes);
        Assert.Null(activity.Progress);
    }

    [Fact]
    public void Report_AfterTheActivityEnded_IsIgnored()
    {
        var log = NewLog();
        var activity = log.Begin("Plugins", "Scanning plugins…");
        activity.Complete();
        _now += TimeSpan.FromSeconds(1);
        var changes = 0;
        log.Changed += (_, _) => changes++;

        activity.Report("Late progress…");

        Assert.Equal("Scanning plugins…", activity.Text);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task ManyThreads_CanBeginAndEndActivitiesAtOnce()
    {
        var log = new ActivityLog();
        var ended = 0;
        log.Ended += (_, _) => Interlocked.Increment(ref ended);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                using var activity = log.Begin("Test", $"Work {t}.{i}…");
                activity.Report("Halfway…", 0.5);
            }
        }, TestContext.Current.CancellationToken)));

        Assert.Equal(1600, ended);
        Assert.Empty(log.Running);
    }
}
