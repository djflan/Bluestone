using Bluestone.Presentation;

namespace Bluestone.Tests.Unit.Presentation;

public sealed class LatestOnBackgroundTests
{
    [Fact]
    public async Task Posting_NeverWaitsForTheSender_AndTheLatestValueArrivesLast()
    {
        using var release = new ManualResetEventSlim();
        var sent = new List<int>();
        var latest = new LatestOnBackground<int>(value =>
        {
            release.Wait(TestContext.Current.CancellationToken);
            lock (sent)
            {
                sent.Add(value);
            }
        });

        // The sender is blocked, as a graph rebuild would block it; posting still returns at once.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 1; i <= 50; i++)
        {
            latest.Post(i);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        release.Set();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (sent)
            {
                if (sent.Count > 0 && sent[^1] == 50)
                {
                    break;
                }
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        lock (sent)
        {
            Assert.Equal(50, sent[^1]);
            Assert.True(sent.Count <= 2, $"Only the first and the latest should be sent, not {sent.Count} values.");
            Assert.Equal(sent.Order(), sent);
        }
    }
}
