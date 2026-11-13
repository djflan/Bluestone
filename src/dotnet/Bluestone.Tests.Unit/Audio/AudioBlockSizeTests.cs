using Bluestone.Audio;

namespace Bluestone.Tests.Unit.Audio;

public sealed class AudioBlockSizeTests
{
    [Theory]
    [InlineData(480, 0, 1024, 480)] // WASAPI shared at 48 kHz: one block per 10 ms callback
    [InlineData(960, 0, 1024, 960)] // the Realtek ASIO driver's default
    [InlineData(512, 256, 1024, 512)] // the device's own size wins over the request
    [InlineData(2048, 0, 1024, 1024)] // too large: the largest divisor that fits
    [InlineData(1920, 0, 1024, 960)]
    [InlineData(0, 128, 1024, 128)] // the device does not say: the request
    [InlineData(0, 0, 1024, 256)] // neither says: the fallback
    [InlineData(0, 4096, 1024, 1024)]
    [InlineData(16, 16, 1024, 32)] // never below the minimum
    [InlineData(480, 0, 256, 240)] // a smaller limit still divides the callback evenly
    public void TheBlockFollowsTheDeviceCallback(int device, int requested, int maximum, int expected) =>
        Assert.Equal(expected, AudioBlockSize.For(device, requested, maximum));

    [Fact]
    public void AnOddCallbackThatNoDivisorFitsFallsBackToTheRequest() =>
        Assert.Equal(256, AudioBlockSize.For(1031, 256, 1024)); // 1031 is prime
}
