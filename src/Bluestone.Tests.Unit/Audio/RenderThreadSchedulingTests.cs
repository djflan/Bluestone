using Bluestone.Audio.Devices;

namespace Bluestone.Tests.Unit.Audio;

public sealed class RenderThreadSchedulingTests
{
    [Fact]
    public void PromoteCurrentThread_SaysWhatItDid_AndOnMacOSGetsTheRealTimePolicy()
    {
        string? result = null;
        var thread = new Thread(() => result = RenderThreadScheduling.PromoteCurrentThread(TimeSpan.FromMilliseconds(21.3)));
        thread.Start();
        thread.Join();

        Assert.False(string.IsNullOrWhiteSpace(result));
        if (OperatingSystem.IsMacOS())
        {
            Assert.StartsWith("real-time", result, StringComparison.Ordinal);
        }
        else if (OperatingSystem.IsWindows())
        {
            Assert.Contains("MMCSS", result, StringComparison.Ordinal);
        }
    }
}
