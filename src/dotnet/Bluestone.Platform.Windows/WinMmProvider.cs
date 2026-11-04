using System.Globalization;
using System.Runtime.Versioning;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;

namespace Bluestone.Platform.Windows;

/// <summary>
/// MIDI endpoints from the legacy Windows multimedia API (WinMM). WinMM cannot schedule messages, so
/// every output is in the immediate delivery class and Bluestone's playback thread waits for it.
/// </summary>
/// <remarks>
/// WinMM identifies devices only by index, which shifts when devices come and go. Endpoint IDs are
/// therefore the device name, with <c>#2</c>, <c>#3</c> … appended to repeated names; input IDs have
/// an <c>input:</c> prefix so same-named inputs and outputs remain distinct.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WinMmProvider : IMidiEndpointProvider
{
    public const string ProviderId = "winmm";

    private readonly Lock _gate = new();
    private readonly IMonotonicClock _clock;
    private readonly List<WinMmOutput> _outputs = [];
    private readonly List<WinMmInput> _inputs = [];
    private readonly Timer _endpointPoller;
    private EventHandler? _endpointsChanged;
    private ulong _lastEndpointSnapshot;
    private bool _disposed;

    public WinMmProvider(IMonotonicClock? clock = null)
    {
        _clock = clock ?? SystemMonotonicClock.Instance;
        _lastEndpointSnapshot = Snapshot();
        _endpointPoller = new Timer(CheckEndpoints, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public string Id => ProviderId;

    public string DisplayName => "Windows MIDI (WinMM)";

    public string TimingDescription => "WinMM: messages are sent when due by Bluestone's playback thread; the system does not schedule them.";

    public event EventHandler? EndpointsChanged
    {
        add => _endpointsChanged += value;
        remove => _endpointsChanged -= value;
    }

    public IReadOnlyList<EndpointDescriptor> GetEndpoints()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Enumerate();
        }
    }

    public ValueTask<IMidiOutput> OpenOutputAsync(EndpointId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var (descriptor, device) = EnumerateOutputs().FirstOrDefault(d => d.Descriptor.Id == id);
            if (descriptor is null)
            {
                throw new EndpointUnavailableException(id, "no such WinMM output");
            }

            var status = WinMm.midiOutOpen(out var handle, device, 0, 0, WinMm.CallbackNull);
            if (status != WinMm.NoError)
            {
                throw new EndpointUnavailableException(id, string.Create(CultureInfo.InvariantCulture, $"WinMM could not open the device (MMRESULT {status})"));
            }

            var output = new WinMmOutput(this, descriptor, handle);
            _outputs.Add(output);
            return ValueTask.FromResult<IMidiOutput>(output);
        }
    }

    public ValueTask<IMidiInput> OpenInputAsync(EndpointId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var (descriptor, device) = EnumerateInputs().FirstOrDefault(d => d.Descriptor.Id == id);
            if (descriptor is null)
            {
                throw new EndpointUnavailableException(id, "no such WinMM input");
            }

            var input = new WinMmInput(this, _clock, descriptor, device);
            _inputs.Add(input);
            return ValueTask.FromResult<IMidiInput>(input);
        }
    }

    public void Dispose()
    {
        WinMmOutput[] outputs;
        WinMmInput[] inputs;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            outputs = [.. _outputs];
            inputs = [.. _inputs];
            _outputs.Clear();
            _inputs.Clear();
        }

        _endpointPoller.Dispose();
        foreach (var output in outputs)
        {
            output.Dispose();
        }

        foreach (var input in inputs)
        {
            input.Dispose();
        }
    }

    internal void Forget(WinMmOutput output)
    {
        lock (_gate)
        {
            _outputs.Remove(output);
        }
    }

    internal void Forget(WinMmInput input)
    {
        lock (_gate)
        {
            _inputs.Remove(input);
        }
    }

    private void CheckEndpoints(object? state)
    {
        EventHandler? changed = null;
        WinMmOutput[] disconnectedOutputs = [];
        WinMmInput[] disconnectedInputs = [];
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var snapshot = Snapshot();
            if (snapshot != _lastEndpointSnapshot)
            {
                _lastEndpointSnapshot = snapshot;
                var ids = Enumerate().Select(endpoint => endpoint.Id).ToHashSet();
                disconnectedOutputs = [.. _outputs.Where(output => !ids.Contains(output.Endpoint.Id))];
                disconnectedInputs = [.. _inputs.Where(input => !ids.Contains(input.Endpoint.Id))];
                changed = _endpointsChanged;
            }
        }

        foreach (var output in disconnectedOutputs)
        {
            output.MarkDisconnected();
        }

        foreach (var input in disconnectedInputs)
        {
            input.MarkDisconnected();
        }

        changed?.Invoke(this, EventArgs.Empty);
    }

    private static unsafe ulong Snapshot()
    {
        var hash = 14_695_981_039_346_656_037UL;
        var outputCount = WinMm.midiOutGetNumDevs();
        hash = HashValue(hash, outputCount);
        for (uint device = 0; device < outputCount; device++)
        {
            hash = HashValue(hash, device);
            WinMm.MidiOutCaps caps;
            if (WinMm.midiOutGetDevCapsW(device, &caps, (uint)sizeof(WinMm.MidiOutCaps)) == WinMm.NoError)
            {
                hash = HashName(hash, new ReadOnlySpan<char>(caps.Name, 32));
                hash = HashValue(hash, caps.Technology);
            }
        }

        var inputCount = WinMm.midiInGetNumDevs();
        hash = HashValue(hash, inputCount);
        for (uint device = 0; device < inputCount; device++)
        {
            hash = HashValue(hash, device);
            WinMm.MidiInCaps caps;
            if (WinMm.midiInGetDevCapsW(device, &caps, (uint)sizeof(WinMm.MidiInCaps)) == WinMm.NoError)
            {
                hash = HashName(hash, new ReadOnlySpan<char>(caps.Name, 32));
            }
        }

        return hash;
    }

    private static ulong HashName(ulong hash, ReadOnlySpan<char> name)
    {
        foreach (var character in name)
        {
            if (character == '\0')
            {
                break;
            }

            hash = HashValue(hash, character);
        }

        return hash;
    }

    private static ulong HashValue(ulong hash, uint value)
    {
        for (var shift = 0; shift < 32; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= 1_099_511_628_211UL;
        }

        return hash;
    }

    private static IReadOnlyList<EndpointDescriptor> Enumerate() =>
        [.. EnumerateOutputs().Select(d => d.Descriptor), .. EnumerateInputs().Select(d => d.Descriptor)];

    private static unsafe List<(EndpointDescriptor Descriptor, uint Device)> EnumerateOutputs()
    {
        var result = new List<(EndpointDescriptor, uint)>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var count = WinMm.midiOutGetNumDevs();
        for (uint device = 0; device < count; device++)
        {
            WinMm.MidiOutCaps caps;
            if (WinMm.midiOutGetDevCapsW(device, &caps, (uint)sizeof(WinMm.MidiOutCaps)) != WinMm.NoError)
            {
                continue;
            }

            var raw = new ReadOnlySpan<char>(caps.Name, 32);
            var end = raw.IndexOf('\0');
            var name = (end >= 0 ? raw[..end] : raw).Trim().ToString();
            if (name.Length == 0)
            {
                name = string.Create(CultureInfo.InvariantCulture, $"MIDI output {device + 1}");
            }

            var occurrence = seen[name] = seen.GetValueOrDefault(name) + 1;
            var value = occurrence == 1 ? name : string.Create(CultureInfo.InvariantCulture, $"{name}#{occurrence}");
            var transport = caps.Technology switch
            {
                WinMm.TechnologyMidiPort => EndpointTransport.Physical,
                WinMm.TechnologySoftwareSynth => EndpointTransport.Software,
                _ => EndpointTransport.Unknown,
            };

            var descriptor = new EndpointDescriptor(
                new EndpointId(ProviderId, value),
                occurrence == 1 ? name : string.Create(CultureInfo.InvariantCulture, $"{name} ({occurrence})"),
                EndpointDirection.Output,
                transport,
                EndpointCapabilities.SystemExclusive);
            result.Add((descriptor, device));
        }

        return result;
    }

    private static unsafe List<(EndpointDescriptor Descriptor, uint Device)> EnumerateInputs()
    {
        var result = new List<(EndpointDescriptor, uint)>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var count = WinMm.midiInGetNumDevs();
        for (uint device = 0; device < count; device++)
        {
            WinMm.MidiInCaps caps;
            if (WinMm.midiInGetDevCapsW(device, &caps, (uint)sizeof(WinMm.MidiInCaps)) != WinMm.NoError)
            {
                continue;
            }

            var raw = new ReadOnlySpan<char>(caps.Name, 32);
            var end = raw.IndexOf('\0');
            var name = (end >= 0 ? raw[..end] : raw).Trim().ToString();
            if (name.Length == 0)
            {
                name = string.Create(CultureInfo.InvariantCulture, $"MIDI input {device + 1}");
            }

            var occurrence = seen[name] = seen.GetValueOrDefault(name) + 1;
            var value = occurrence == 1 ? name : string.Create(CultureInfo.InvariantCulture, $"{name}#{occurrence}");
            var descriptor = new EndpointDescriptor(
                new EndpointId(ProviderId, $"input:{value}"),
                occurrence == 1 ? name : string.Create(CultureInfo.InvariantCulture, $"{name} ({occurrence})"),
                EndpointDirection.Input,
                EndpointTransport.Unknown,
                EndpointCapabilities.SystemExclusive);
            result.Add((descriptor, device));
        }

        return result;
    }
}
