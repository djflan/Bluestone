using System.Globalization;
using System.Runtime.Versioning;
using Bluestone.Midi.Endpoints;

namespace Bluestone.Platform.Windows;

/// <summary>
/// MIDI outputs from the legacy Windows multimedia API (WinMM). WinMM cannot schedule messages, so
/// every endpoint is in the immediate delivery class and Bluestone's playback thread waits for each
/// message. Inputs and hot-plug notifications are not supported yet (ADR 0014).
/// </summary>
/// <remarks>
/// WinMM identifies devices only by index, which shifts when devices come and go. Endpoint IDs are
/// therefore the device name, with <c>#2</c>, <c>#3</c> … appended to repeated names.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WinMmProvider : IMidiEndpointProvider
{
    public const string ProviderId = "winmm";

    private readonly Lock _gate = new();
    private readonly List<WinMmOutput> _outputs = [];
    private bool _disposed;

    public string Id => ProviderId;

    public string DisplayName => "Windows MIDI (WinMM)";

    public string TimingDescription => "WinMM: messages are sent when due by Bluestone's playback thread; the system does not schedule them.";

    /// <summary>WinMM has no device-change notifications, so this event is never raised.</summary>
    public event EventHandler? EndpointsChanged
    {
        add { }
        remove { }
    }

    public IReadOnlyList<EndpointDescriptor> GetEndpoints()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return [.. Enumerate().Select(d => d.Descriptor)];
        }
    }

    public ValueTask<IMidiOutput> OpenOutputAsync(EndpointId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var (descriptor, device) = Enumerate().FirstOrDefault(d => d.Descriptor.Id == id);
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

    public ValueTask<IMidiInput> OpenInputAsync(EndpointId id, CancellationToken cancellationToken = default) =>
        throw new EndpointUnavailableException(id, "WinMM input is not supported yet");

    public void Dispose()
    {
        WinMmOutput[] outputs;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            outputs = [.. _outputs];
            _outputs.Clear();
        }

        foreach (var output in outputs)
        {
            output.Dispose();
        }
    }

    internal void Forget(WinMmOutput output)
    {
        lock (_gate)
        {
            _outputs.Remove(output);
        }
    }

    private static unsafe List<(EndpointDescriptor Descriptor, uint Device)> Enumerate()
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
}
