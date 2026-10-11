using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;

namespace Bluestone.Platform.CoreMidi;

/// <summary>
/// Endpoints from macOS CoreMIDI: hardware ports, the IAC bus, network sessions, other apps' virtual
/// ports, and virtual ports Bluestone publishes itself. Endpoint IDs are CoreMIDI unique IDs.
/// </summary>
/// <remarks>
/// CoreMIDI delivers setup notifications on the run loop current when the process first created a
/// client, so every client is created on <see cref="CoreMidiRunLoop"/>, which never exits. Outputs to destinations pass timestamps to
/// <c>MIDISend</c>, which CoreMIDI schedules. Virtual ports published by Bluestone deliver immediately.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed unsafe class CoreMidiProvider : IMidiEndpointProvider
{
    public const string ProviderId = "coremidi";
    private const string VirtualPrefix = "virtual:";

    private readonly Lock _gate = new();
    private readonly List<CoreMidiOutput> _outputs = [];
    private readonly List<CoreMidiInput> _inputs = [];
    private readonly Dictionary<string, uint> _virtualSources = new(StringComparer.Ordinal);
    private GCHandle _self;
    private uint _client;
    private uint _outputPort;
    private volatile bool _disposed;

    /// <exception cref="EndpointUnavailableException">CoreMIDI could not create a client (for example, the MIDI server is unreachable).</exception>
    public CoreMidiProvider(IMonotonicClock clock, string clientName = "Bluestone")
    {
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        HostTime = new HostTime(clock);
        _self = GCHandle.Alloc(this);
        var status = CoreMidiRunLoop.Invoke(() => CreateClient(clientName));
        if (status != Native.NoError)
        {
            Dispose();
            throw new EndpointUnavailableException(string.Create(CultureInfo.InvariantCulture, $"CoreMIDI could not start (OSStatus {status})."));
        }
    }

    public string Id => ProviderId;

    public string DisplayName => "CoreMIDI";

    public string TimingDescription => "CoreMIDI: hardware and IAC destinations receive timestamped messages scheduled by the system; Bluestone's own virtual ports send immediately.";

    public IMonotonicClock Clock { get; }

    internal HostTime HostTime { get; }

    internal uint OutputPort => _outputPort;

    internal uint Client => _client;

    public event EventHandler? EndpointsChanged;

    /// <summary>
    /// Publishes a virtual MIDI port that other applications see as a source. Bluestone lists it as an
    /// output; anything sent to it is delivered to those applications.
    /// </summary>
    public EndpointId CreateVirtualOutput(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_virtualSources.ContainsKey(name))
            {
                return VirtualId(name);
            }

            var cfName = Native.CreateString(name);
            try
            {
                var status = Native.MIDISourceCreate(_client, cfName, out var source);
                if (status != Native.NoError)
                {
                    throw new EndpointUnavailableException(string.Create(CultureInfo.InvariantCulture, $"CoreMIDI could not create virtual port '{name}' (OSStatus {status})."));
                }

                // A stable unique ID lets other applications remember the connection across launches.
                // If CoreMIDI refuses it (an ID collision), it keeps the one it assigned.
                _ = Native.MIDIObjectSetIntegerProperty(source, Native.PropertyUniqueId, StableUniqueId(name));
                _virtualSources[name] = source;
            }
            finally
            {
                Native.CFRelease(cfName);
            }
        }

        EndpointsChanged?.Invoke(this, EventArgs.Empty);
        return VirtualId(name);
    }

    public IReadOnlyList<EndpointDescriptor> GetEndpoints()
    {
        var result = new List<EndpointDescriptor>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var ownSources = _virtualSources.Values.ToHashSet();
            foreach (var (name, _) in _virtualSources.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                result.Add(new EndpointDescriptor(VirtualId(name), name, EndpointDirection.Output, EndpointTransport.Virtual, EndpointCapabilities.SystemExclusive, "Bluestone"));
            }

            var destinations = Native.MIDIGetNumberOfDestinations();
            for (nuint i = 0; i < destinations; i++)
            {
                if (Describe(Native.MIDIGetDestination(i), EndpointDirection.Output) is { } d)
                {
                    result.Add(d);
                }
            }

            var sources = Native.MIDIGetNumberOfSources();
            for (nuint i = 0; i < sources; i++)
            {
                var source = Native.MIDIGetSource(i);
                if (!ownSources.Contains(source) && Describe(source, EndpointDirection.Input) is { } d)
                {
                    result.Add(d);
                }
            }
        }

        return result;
    }

    public ValueTask<IMidiOutput> OpenOutputAsync(EndpointId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = GetEndpoints().FirstOrDefault(e => e.Id == id && e.Direction == EndpointDirection.Output)
            ?? throw new EndpointUnavailableException(id, "no such CoreMIDI output");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CoreMidiOutput output = id.Value.StartsWith(VirtualPrefix, StringComparison.Ordinal)
                ? new CoreMidiOutput(this, descriptor, _virtualSources[id.Value[VirtualPrefix.Length..]], isVirtualSource: true)
                : new CoreMidiOutput(this, descriptor, Find(id), isVirtualSource: false);
            _outputs.Add(output);
            return ValueTask.FromResult<IMidiOutput>(output);
        }
    }

    public ValueTask<IMidiInput> OpenInputAsync(EndpointId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = GetEndpoints().FirstOrDefault(e => e.Id == id && e.Direction == EndpointDirection.Input)
            ?? throw new EndpointUnavailableException(id, "no such CoreMIDI input");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var input = new CoreMidiInput(this, descriptor, Find(id));
            _inputs.Add(input);
            return ValueTask.FromResult<IMidiInput>(input);
        }
    }

    public void Dispose()
    {
        List<CoreMidiOutput> outputs;
        List<CoreMidiInput> inputs;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            outputs = [.. _outputs];
            inputs = [.. _inputs];
        }

        foreach (var output in outputs)
        {
            output.Dispose();
        }

        foreach (var input in inputs)
        {
            input.Dispose();
        }

        // Disposal failures are not actionable; CoreMIDI reclaims everything when the process exits.
        foreach (var source in _virtualSources.Values)
        {
            _ = Native.MIDIEndpointDispose(source);
        }

        if (_outputPort != 0)
        {
            _ = Native.MIDIPortDispose(_outputPort);
        }

        if (_client != 0)
        {
            _ = Native.MIDIClientDispose(_client);
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    internal void Forget(CoreMidiOutput output)
    {
        lock (_gate)
        {
            _outputs.Remove(output);
        }
    }

    internal void Forget(CoreMidiInput input)
    {
        lock (_gate)
        {
            _inputs.Remove(input);
        }
    }

    private static EndpointId VirtualId(string name) => new(ProviderId, VirtualPrefix + name);

    private static int StableUniqueId(string name)
    {
        // FNV-1a over the name: stable across launches, unlikely to collide with hardware IDs.
        var hash = 2166136261u;
        foreach (var c in "bluestone:" + name)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return (int)hash;
    }

    private static uint Find(EndpointId id)
    {
        if (!int.TryParse(id.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uniqueId)
            || Native.MIDIObjectFindByUniqueID(uniqueId, out var endpoint, out _) != Native.NoError)
        {
            throw new EndpointUnavailableException(id, "the endpoint is no longer present");
        }

        return endpoint;
    }

    private static EndpointDescriptor? Describe(uint endpoint, EndpointDirection direction)
    {
        if (endpoint == 0 || Native.GetInteger(endpoint, Native.PropertyOffline) is 1 || Native.GetInteger(endpoint, Native.PropertyUniqueId) is not { } uniqueId)
        {
            return null;
        }

        var name = Native.GetString(endpoint, Native.PropertyDisplayName) ?? Native.GetString(endpoint, Native.PropertyName) ?? "MIDI endpoint";
        var capabilities = EndpointCapabilities.SystemExclusive | (direction == EndpointDirection.Output ? EndpointCapabilities.ScheduledDelivery : EndpointCapabilities.None);
        return new EndpointDescriptor(
            new EndpointId(ProviderId, uniqueId.ToString(CultureInfo.InvariantCulture)),
            name,
            direction,
            Classify(endpoint),
            capabilities,
            Native.GetString(endpoint, Native.PropertyManufacturer),
            Native.GetString(endpoint, Native.PropertyModel));
    }

    private static EndpointTransport Classify(uint endpoint)
    {
        // Endpoints without an entity are virtual ports created by applications.
        if (Native.MIDIEndpointGetEntity(endpoint, out var entity) != Native.NoError || entity == 0
            || Native.MIDIEntityGetDevice(entity, out var device) != Native.NoError || device == 0)
        {
            return EndpointTransport.Virtual;
        }

        var driver = Native.GetString(device, Native.PropertyDriverOwner) ?? string.Empty;
        if (driver.Contains("IAC", StringComparison.OrdinalIgnoreCase))
        {
            return EndpointTransport.Virtual;
        }

        return driver.Contains("RTP", StringComparison.OrdinalIgnoreCase) || driver.Contains("Network", StringComparison.OrdinalIgnoreCase)
            ? EndpointTransport.Network
            : EndpointTransport.Physical;
    }

    /// <summary>Runs on <see cref="CoreMidiRunLoop"/> so notifications arrive on a run loop that never ends.</summary>
    private int CreateClient(string clientName)
    {
        var name = Native.CreateString(clientName);
        try
        {
            var status = Native.MIDIClientCreate(name, &OnNotify, GCHandle.ToIntPtr(_self), out _client);
            if (status != Native.NoError)
            {
                return status;
            }

            var portName = Native.CreateString(clientName + " Out");
            try
            {
                return Native.MIDIOutputPortCreate(_client, portName, out _outputPort);
            }
            finally
            {
                Native.CFRelease(portName);
            }
        }
        finally
        {
            Native.CFRelease(name);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnNotify(IntPtr message, IntPtr refCon)
    {
        if (refCon == IntPtr.Zero || GCHandle.FromIntPtr(refCon).Target is not CoreMidiProvider provider || provider._disposed)
        {
            return;
        }

        if (*(int*)message == Native.MsgSetupChanged)
        {
            // Never run subscribers on CoreMIDI's notification thread.
            ThreadPool.QueueUserWorkItem(static p => p.OnSetupChanged(), provider, preferLocal: false);
        }
    }

    private void OnSetupChanged()
    {
        if (_disposed)
        {
            return;
        }

        List<CoreMidiOutput> outputs;
        List<CoreMidiInput> inputs;
        lock (_gate)
        {
            outputs = [.. _outputs];
            inputs = [.. _inputs];
        }

        var present = GetEndpoints().Select(e => (e.Id, e.Direction)).ToHashSet();
        foreach (var output in outputs.Where(o => !present.Contains((o.Endpoint.Id, EndpointDirection.Output))))
        {
            output.MarkDisconnected();
        }

        foreach (var input in inputs.Where(i => !present.Contains((i.Endpoint.Id, EndpointDirection.Input))))
        {
            input.MarkDisconnected();
        }

        EndpointsChanged?.Invoke(this, EventArgs.Empty);
    }
}
