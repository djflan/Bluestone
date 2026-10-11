namespace Bluestone.Tests.Integration;

/// <summary>
/// Tests that measure real-time delivery (the playback thread, CoreMIDI). They run apart from the other test
/// collections, so parallel work does not make them late.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingTests
{
    public const string Name = "Real-time timing";
}
