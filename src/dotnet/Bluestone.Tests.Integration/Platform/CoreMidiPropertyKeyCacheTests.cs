using Bluestone.Platform.CoreMidi;

namespace Bluestone.Tests.Integration.Platform;

/// <summary>
/// The CoreMIDI property constants can read as null until a client exists (issue #112). These tests
/// use a fake export reader, so they run on every OS.
/// </summary>
public sealed class CoreMidiPropertyKeyCacheTests
{
    private static readonly Dictionary<string, IntPtr> Exports = CoreMidiPropertyKeys.ExportNames
        .Select((name, i) => (name, value: (IntPtr)(0x1000 + (i * 0x10))))
        .ToDictionary(e => e.name, e => e.value, StringComparer.Ordinal);

    [Fact]
    public void TryResolve_BeforeClientCreated_ReadsNothing()
    {
        var reads = new List<string>();
        var cache = new CoreMidiPropertyKeyCache(name =>
        {
            reads.Add(name);
            return Exports[name];
        });

        Assert.Null(cache.TryResolve());
        Assert.Empty(reads);
    }

    [Fact]
    public void TryResolve_AfterClientCreated_MapsEveryExport()
    {
        var cache = new CoreMidiPropertyKeyCache(name => Exports[name]);
        cache.OnClientCreated();

        var keys = cache.TryResolve();

        Assert.Equal(
            new CoreMidiPropertyKeys(
                Exports["kMIDIPropertyName"],
                Exports["kMIDIPropertyDisplayName"],
                Exports["kMIDIPropertyUniqueID"],
                Exports["kMIDIPropertyManufacturer"],
                Exports["kMIDIPropertyModel"],
                Exports["kMIDIPropertyOffline"],
                Exports["kMIDIPropertyDriverOwner"]),
            keys);
    }

    [Fact]
    public void TryResolve_NullExport_IsNotCachedAndIsReadAgain()
    {
        var nameReady = false;
        var cache = new CoreMidiPropertyKeyCache(name => name == "kMIDIPropertyName" && !nameReady ? IntPtr.Zero : Exports[name]);
        cache.OnClientCreated();

        Assert.Null(cache.TryResolve());

        nameReady = true;
        var keys = cache.TryResolve();

        Assert.NotNull(keys);
        Assert.Equal(Exports["kMIDIPropertyName"], keys.Name);
    }

    [Fact]
    public void TryResolve_OnceResolved_IsCachedAndStopsReading()
    {
        var reads = 0;
        var cache = new CoreMidiPropertyKeyCache(name =>
        {
            reads++;
            return Exports[name];
        });
        cache.OnClientCreated();

        var first = cache.TryResolve();
        var readsAfterFirst = reads;
        var second = cache.TryResolve();

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(CoreMidiPropertyKeys.ExportNames.Count, readsAfterFirst);
        Assert.Equal(readsAfterFirst, reads);
    }

    [Fact]
    public async Task TryResolve_ConcurrentCallers_AllSeeTheSameKeys()
    {
        var cache = new CoreMidiPropertyKeyCache(name => Exports[name]);
        cache.OnClientCreated();

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(cache.TryResolve, TestContext.Current.CancellationToken)));

        Assert.All(results, Assert.NotNull);
        Assert.All(results, r => Assert.Same(results[0], r));
    }
}
