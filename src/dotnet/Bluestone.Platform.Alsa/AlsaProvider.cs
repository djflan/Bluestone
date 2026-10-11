using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bluestone.Midi.Endpoints;

namespace Bluestone.Platform.Alsa;

/// <summary>
/// Endpoints from the Linux ALSA sequencer: hardware ports, software synthesizers, other
/// applications' ports, and virtual ports Bluestone publishes itself. Messages are sent directly
/// (not through an ALSA queue), so every endpoint is in the immediate delivery class. Inputs and
/// hot-plug notifications are not supported yet (ADR 0016).
/// </summary>
/// <remarks>
/// ALSA client and port numbers are reassigned when devices or applications restart, so endpoint IDs
/// are <c>client name:port name</c>, with <c>#2</c>, <c>#3</c> … appended to repeated names.
/// Enumeration uses its own sequencer handle so it never shares state with sends on the playback thread.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class AlsaProvider : IMidiEndpointProvider
{
    public const string ProviderId = "alsa";
    private const string VirtualPrefix = "virtual:";

    private readonly Lock _gate = new();
    private readonly List<AlsaOutput> _outputs = [];
    private readonly Dictionary<string, int> _virtualPorts = new(StringComparer.Ordinal);
    private IntPtr _seq;
    private IntPtr _query;
    private readonly int _sendPort = -1;
    private bool _disposed;

    /// <exception cref="EndpointUnavailableException">ALSA is not installed or the sequencer could not be opened.</exception>
    public AlsaProvider(string clientName = "Bluestone")
    {
        try
        {
            _seq = Open(clientName);
            _query = Open(clientName + " (query)");
        }
        catch (DllNotFoundException ex)
        {
            Dispose();
            throw new EndpointUnavailableException("ALSA (libasound.so.2) is not installed.", ex);
        }
        catch (EndpointUnavailableException)
        {
            Dispose();
            throw;
        }

        // Direct sends need a source port of our own; it is hidden from other applications.
        _sendPort = Alsa.snd_seq_create_simple_port(_seq, "Bluestone Send", Alsa.CapRead | Alsa.CapNoExport, Alsa.TypeMidiGeneric | Alsa.TypeApplication);
        if (_sendPort < 0)
        {
            var status = _sendPort;
            Dispose();
            throw new EndpointUnavailableException($"ALSA could not create a port ({Alsa.Error(status)}).");
        }
    }

    public string Id => ProviderId;

    public string DisplayName => "ALSA";

    public string TimingDescription => "ALSA sequencer: messages are sent directly when due by Bluestone's playback thread; no ALSA queue schedules them.";

    /// <summary>Raised when Bluestone publishes a virtual port. ALSA announcements are not subscribed to yet.</summary>
    public event EventHandler? EndpointsChanged;

    /// <summary>
    /// Publishes a virtual port that other applications see as a readable port. Bluestone lists it as an
    /// output; anything sent to it goes to the applications subscribed to it.
    /// </summary>
    public EndpointId CreateVirtualOutput(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_virtualPorts.ContainsKey(name))
            {
                return VirtualId(name);
            }

            var port = Alsa.snd_seq_create_simple_port(_seq, name, Alsa.CapRead | Alsa.CapSubsRead, Alsa.TypeMidiGeneric | Alsa.TypeApplication);
            if (port < 0)
            {
                throw new EndpointUnavailableException($"ALSA could not create virtual port '{name}' ({Alsa.Error(port)}).");
            }

            _virtualPorts[name] = port;
        }

        EndpointsChanged?.Invoke(this, EventArgs.Empty);
        return VirtualId(name);
    }

    public IReadOnlyList<EndpointDescriptor> GetEndpoints()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return [.. Enumerate().Select(e => e.Descriptor)];
        }
    }

    public ValueTask<IMidiOutput> OpenOutputAsync(EndpointId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var (descriptor, address) = Enumerate().FirstOrDefault(e => e.Descriptor.Id == id);
            if (descriptor is null)
            {
                throw new EndpointUnavailableException(id, "no such ALSA output");
            }

            var output = address is { } a
                ? new AlsaOutput(this, descriptor, _seq, _sendPort, a)
                : new AlsaOutput(this, descriptor, _seq, _virtualPorts[id.Value[VirtualPrefix.Length..]], null);
            _outputs.Add(output);
            return ValueTask.FromResult<IMidiOutput>(output);
        }
    }

    public ValueTask<IMidiInput> OpenInputAsync(EndpointId id, CancellationToken cancellationToken = default) =>
        throw new EndpointUnavailableException(id, "ALSA input is not supported yet");

    public void Dispose()
    {
        AlsaOutput[] outputs;
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

        if (_seq != IntPtr.Zero)
        {
            // Closing the client also removes its ports.
            _ = Alsa.snd_seq_close(_seq);
            _seq = IntPtr.Zero;
        }

        if (_query != IntPtr.Zero)
        {
            _ = Alsa.snd_seq_close(_query);
            _query = IntPtr.Zero;
        }
    }

    internal void Forget(AlsaOutput output)
    {
        lock (_gate)
        {
            _outputs.Remove(output);
        }
    }

    private static IntPtr Open(string clientName)
    {
        var status = Alsa.snd_seq_open(out var seq, "default", Alsa.OpenOutput, Alsa.NonBlock);
        if (status < 0)
        {
            throw new EndpointUnavailableException($"The ALSA sequencer could not be opened ({Alsa.Error(status)}).");
        }

        _ = Alsa.snd_seq_set_client_name(seq, clientName);
        return seq;
    }

    private static EndpointId VirtualId(string name) => new(ProviderId, VirtualPrefix + name);

    private static string? ReadName(IntPtr text) => text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text)?.Trim();

    private static EndpointTransport TransportOf(uint type) => type switch
    {
        _ when (type & (Alsa.TypeHardware | Alsa.TypePort)) != 0 => EndpointTransport.Physical,
        _ when (type & (Alsa.TypeSoftware | Alsa.TypeSynthesizer)) != 0 => EndpointTransport.Software,
        _ when (type & Alsa.TypeApplication) != 0 => EndpointTransport.Virtual,
        _ => EndpointTransport.Unknown,
    };

    /// <summary>Lists Bluestone's virtual ports, then every writable, subscribable port of other clients.</summary>
    private List<(EndpointDescriptor Descriptor, (int Client, int Port)? Address)> Enumerate()
    {
        var result = new List<(EndpointDescriptor, (int, int)?)>();
        foreach (var name in _virtualPorts.Keys.Order(StringComparer.Ordinal))
        {
            result.Add((new EndpointDescriptor(VirtualId(name), name, EndpointDirection.Output, EndpointTransport.Virtual, EndpointCapabilities.SystemExclusive, "Bluestone"), null));
        }

        var own = new HashSet<int> { Alsa.snd_seq_client_id(_seq), Alsa.snd_seq_client_id(_query) };
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var clientInfo = NativeMemory.AllocZeroed(Alsa.snd_seq_client_info_sizeof());
        var portInfo = NativeMemory.AllocZeroed(Alsa.snd_seq_port_info_sizeof());
        try
        {
            const uint writable = Alsa.CapWrite | Alsa.CapSubsWrite;
            Alsa.snd_seq_client_info_set_client((IntPtr)clientInfo, -1);
            while (Alsa.snd_seq_query_next_client(_query, (IntPtr)clientInfo) >= 0)
            {
                var client = Alsa.snd_seq_client_info_get_client((IntPtr)clientInfo);
                if (client == 0 || own.Contains(client))
                {
                    // Client 0 is the system timer and announcement client.
                    continue;
                }

                var clientName = ReadName(Alsa.snd_seq_client_info_get_name((IntPtr)clientInfo)) ?? string.Create(CultureInfo.InvariantCulture, $"Client {client}");
                Alsa.snd_seq_port_info_set_client((IntPtr)portInfo, client);
                Alsa.snd_seq_port_info_set_port((IntPtr)portInfo, -1);
                while (Alsa.snd_seq_query_next_port(_query, (IntPtr)portInfo) >= 0)
                {
                    var caps = Alsa.snd_seq_port_info_get_capability((IntPtr)portInfo);
                    if ((caps & writable) != writable || (caps & Alsa.CapNoExport) != 0)
                    {
                        continue;
                    }

                    var port = Alsa.snd_seq_port_info_get_port((IntPtr)portInfo);
                    var portName = ReadName(Alsa.snd_seq_port_info_get_name((IntPtr)portInfo)) ?? string.Create(CultureInfo.InvariantCulture, $"Port {port}");
                    var key = $"{clientName}:{portName}";
                    var occurrence = seen[key] = seen.GetValueOrDefault(key) + 1;
                    var value = occurrence == 1 ? key : string.Create(CultureInfo.InvariantCulture, $"{key}#{occurrence}");
                    var display = portName.StartsWith(clientName, StringComparison.Ordinal) ? portName : $"{clientName}: {portName}";
                    if (occurrence > 1)
                    {
                        display = string.Create(CultureInfo.InvariantCulture, $"{display} ({occurrence})");
                    }

                    var descriptor = new EndpointDescriptor(
                        new EndpointId(ProviderId, value),
                        display,
                        EndpointDirection.Output,
                        TransportOf(Alsa.snd_seq_port_info_get_type((IntPtr)portInfo)),
                        EndpointCapabilities.SystemExclusive);
                    result.Add((descriptor, (client, port)));
                }
            }
        }
        finally
        {
            NativeMemory.Free(clientInfo);
            NativeMemory.Free(portInfo);
        }

        return result;
    }
}
