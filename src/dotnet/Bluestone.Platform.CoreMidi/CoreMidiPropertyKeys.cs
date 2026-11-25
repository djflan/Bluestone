namespace Bluestone.Platform.CoreMidi;

/// <summary>The CoreMIDI property-name CFStrings (<c>kMIDIProperty…</c>) Bluestone reads from endpoints.</summary>
internal sealed record CoreMidiPropertyKeys(
    IntPtr Name,
    IntPtr DisplayName,
    IntPtr UniqueId,
    IntPtr Manufacturer,
    IntPtr Model,
    IntPtr Offline,
    IntPtr DriverOwner)
{
    /// <summary>The exported symbol names, in constructor order.</summary>
    public static readonly IReadOnlyList<string> ExportNames =
    [
        "kMIDIPropertyName",
        "kMIDIPropertyDisplayName",
        "kMIDIPropertyUniqueID",
        "kMIDIPropertyManufacturer",
        "kMIDIPropertyModel",
        "kMIDIPropertyOffline",
        "kMIDIPropertyDriverOwner",
    ];
}

/// <summary>
/// Resolves <see cref="CoreMidiPropertyKeys"/> once a CoreMIDI client exists, and caches them only when
/// every key is valid.
/// </summary>
/// <remarks>
/// CoreMIDI's exported property constants can read as null until the process has created a client
/// (issue #112). Resolving them earlier, from a static initializer that any CoreMIDI binding call
/// triggers before <c>MIDIClientCreate</c>, cached nulls that made every endpoint lookup fail. This
/// cache therefore never reads the exports before <see cref="OnClientCreated"/> and never caches a null.
/// Thread-safe; it has no platform dependency so it can be tested on any OS.
/// </remarks>
internal sealed class CoreMidiPropertyKeyCache(Func<string, IntPtr> readExport)
{
    private readonly Func<string, IntPtr> _readExport = readExport ?? throw new ArgumentNullException(nameof(readExport));
    private volatile bool _clientCreated;
    private CoreMidiPropertyKeys? _keys;

    /// <summary>Records that <c>MIDIClientCreate</c> has succeeded in this process.</summary>
    public void OnClientCreated() => _clientCreated = true;

    /// <summary>
    /// Returns the keys, reading the exports if they are not cached yet. Returns null, without reading
    /// anything, before a client has been created, and returns null (caching nothing) if any export is
    /// still null.
    /// </summary>
    public CoreMidiPropertyKeys? TryResolve()
    {
        if (Volatile.Read(ref _keys) is { } cached)
        {
            return cached;
        }

        if (!_clientCreated)
        {
            return null;
        }

        var values = new IntPtr[CoreMidiPropertyKeys.ExportNames.Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = _readExport(CoreMidiPropertyKeys.ExportNames[i]);
            if (values[i] == IntPtr.Zero)
            {
                return null;
            }
        }

        var resolved = new CoreMidiPropertyKeys(values[0], values[1], values[2], values[3], values[4], values[5], values[6]);
        return Interlocked.CompareExchange(ref _keys, resolved, null) ?? resolved;
    }
}
