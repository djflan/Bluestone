using System.Collections.Immutable;
using System.Text.Json;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;
using Bluestone.PluginWorker.Plugins;

namespace Bluestone.PluginWorker.Native;

/// <summary>
/// A VST3 or CLAP plugin running in the native plugin host. Everything except <see cref="Process"/> runs on the
/// worker's main thread; <see cref="Process"/> runs on the instance's processing thread and allocates nothing.
/// System exclusive events are not passed to native plugins (they are dropped, as an unsupported event kind).
/// </summary>
internal sealed class NativePlugin : IHostedPlugin, IDisposable
{
    private const int MaxEvents = 4096;
    private const int NotificationBatch = 64;

    private readonly NativeNotification[] _notifications = new NativeNotification[NotificationBatch];
    private nint _handle;
    private NativeEvent[] _events = [];
    private NativeParameterChange[] _changes = [];
    private NativeEvent[] _outputEvents = [];
    private int _inputChannels;
    private int _outputChannels;

    private NativePlugin(PluginIdentity identity, nint handle)
    {
        Identity = identity;
        _handle = handle;
        Parameters = ReadParameters(handle);
        StateFormat = identity.Format == NativeModules.Vst3 ? "bluestone.vst3-state" : "bluestone.clap-state";
    }

    public PluginIdentity Identity { get; }

    public ImmutableArray<ParameterDescriptor> Parameters { get; private set; }

    public int LatencyFrames => (int)Math.Min(NativeMethods.PluginLatency(_handle), 1 << 24);

    public string StateFormat { get; }

    /// <summary>Loads <paramref name="modulePath"/> and creates an instance of <paramref name="identity"/>. Main thread.</summary>
    public static NativePlugin Create(PluginIdentity identity, string modulePath)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var module = NativeModules.Load(identity.Format, modulePath);
        if (NativeMethods.PluginCreate(module, identity.PluginId, out var handle, out var error) != 0)
        {
            throw new InvalidOperationException(NativeMethods.TakeString(error) ?? "The plugin could not be created.");
        }

        return new NativePlugin(identity, handle);
    }

    public void Prepare(double sampleRate, int maxFrames, int inputChannels, int outputChannels)
    {
        if (NativeMethods.PluginActivate(_handle, sampleRate, (uint)maxFrames, (uint)inputChannels, (uint)outputChannels, out var error) != 0)
        {
            throw new NotSupportedException(NativeMethods.TakeString(error) ?? "The plugin could not be activated.");
        }

        _inputChannels = inputChannels;
        _outputChannels = outputChannels;
        _events = new NativeEvent[MaxEvents];
        _changes = new NativeParameterChange[ProtocolLimits.MaxParameterChangeCapacity];
        _outputEvents = new NativeEvent[MaxEvents];
    }

    public double GetParameter(uint id) => NativeMethods.PluginGetParameter(_handle, id, out var value) == 1 ? value : double.NaN;

    public bool TrySetParameter(uint id, double value) =>
        NormalizedValue.IsValid(value) && NativeMethods.PluginSetParameter(_handle, id, value) == 1;

    public void Process(in ProcessArgs args, OutputEventList output)
    {
        var eventCount = 0;
        foreach (ref readonly var e in args.Events)
        {
            if (e.Kind == PluginEventKind.SystemExclusive || eventCount == _events.Length)
            {
                continue;
            }

            _events[eventCount++] = new NativeEvent { Offset = e.SampleOffset, Kind = (byte)e.Kind, Channel = e.Channel, Data1 = e.Data1, Data2 = e.Data2 };
        }

        var changeCount = Math.Min(args.ParameterChanges.Length, _changes.Length);
        for (var i = 0; i < changeCount; i++)
        {
            var change = args.ParameterChanges[i];
            _changes[i] = new NativeParameterChange { Offset = change.SampleOffset, Id = change.ParameterId, Value = change.Value };
        }

        var capacity = Math.Min(_outputEvents.Length, output.Capacity);
        var block = new NativeBlock
        {
            Frames = (uint)args.Frames,
            InputChannels = (uint)_inputChannels,
            OutputChannels = (uint)_outputChannels,
            EventCount = (uint)eventCount,
            ChangeCount = (uint)changeCount,
            OutputEventCapacity = (uint)capacity,
            Position = args.Transport.SamplePosition,
            Tempo = args.Transport.Tempo,
            Playing = args.Transport.IsPlaying ? 1u : 0u,
        };
        var ok = NativeMethods.Process(_handle, ref block, args.Input, args.Output, _events.AsSpan(0, eventCount), _changes.AsSpan(0, changeCount), _outputEvents.AsSpan(0, capacity));
        if (ok != 1)
        {
            throw new InvalidOperationException("The plugin did not process the block.");
        }

        for (var i = 0; i < block.OutputEventCount; i++)
        {
            var e = _outputEvents[i];
            if (ToPluginEvent(e, args.Frames) is { } converted)
            {
                output.Add(converted);
            }
        }
    }

    public PluginStateData SaveState()
    {
        if (NativeMethods.PluginSaveState(_handle, out var data, out var length, out var error) != 0)
        {
            throw new InvalidOperationException(NativeMethods.TakeString(error) ?? "The plugin did not save its state.");
        }

        var bytes = NativeMethods.TakeBytes(data, length);
        if (bytes.Length > ProtocolLimits.MaxStateBytes)
        {
            throw new InvalidOperationException($"The plugin's state is {bytes.Length} bytes, more than the {ProtocolLimits.MaxStateBytes} the protocol carries.");
        }

        return new PluginStateData(StateFormat, [.. bytes]);
    }

    /// <summary>Loads state. Rejected state throws <see cref="InvalidDataException"/>; what the plugin then holds is up to the plugin.</summary>
    public void LoadState(PluginStateData state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Format != StateFormat)
        {
            throw new InvalidDataException($"Unknown state format '{state.Format}'; expected '{StateFormat}'.");
        }

        if (NativeMethods.LoadState(_handle, state.Data.AsSpan(), out var error) != 0)
        {
            throw new InvalidDataException(NativeMethods.TakeString(error) ?? "The plugin rejected the state.");
        }
    }

    /// <summary>Does the plugin's main-thread work and reports what it did on its own. Main thread.</summary>
    public void Idle(Action<NotificationKind, uint, double> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        int count;
        do
        {
            count = NativeMethods.Idle(_handle, _notifications);
            for (var i = 0; i < count; i++)
            {
                var n = _notifications[i];
                var kind = (NotificationKind)n.Kind;
                if (kind == NotificationKind.ParametersChanged)
                {
                    Parameters = ReadParameters(_handle);
                }

                if (Enum.IsDefined(kind) && (kind != NotificationKind.ParameterEdited || NormalizedValue.IsValid(n.Value)))
                {
                    report(kind, kind == NotificationKind.ParameterEdited ? n.Id : 0, kind is NotificationKind.ParameterEdited or NotificationKind.LatencyChanged ? n.Value : 0);
                }
            }
        }
        while (count == _notifications.Length);
    }

    /// <summary>Opens the editor; null on success, else why not. Main thread.</summary>
    public string? OpenEditor(string title)
    {
        if (NativeMethods.PluginOpenEditor(_handle, title, out var error) == 0)
        {
            return null;
        }

        return NativeMethods.TakeString(error) ?? "The editor could not be opened.";
    }

    public void CloseEditor() => NativeMethods.PluginCloseEditor(_handle);

    public bool IsEditorOpen => NativeMethods.PluginIsEditorOpen(_handle) == 1;

    public void Dispose()
    {
        if (_handle != 0)
        {
            NativeMethods.PluginDestroy(_handle);
            _handle = 0;
        }
    }

    private static PluginEvent? ToPluginEvent(NativeEvent e, int frames)
    {
        var offset = Math.Clamp(e.Offset, 0, Math.Max(0, frames - 1));
        if (e.Channel > 15 || e.Data1 > 127 || e.Data2 > 127)
        {
            return null;
        }

        return (PluginEventKind)e.Kind switch
        {
            PluginEventKind.NoteOn => PluginEvent.NoteOn(offset, e.Channel, e.Data1, e.Data2),
            PluginEventKind.NoteOff => PluginEvent.NoteOff(offset, e.Channel, e.Data1, e.Data2),
            PluginEventKind.PolyPressure => PluginEvent.PolyPressure(offset, e.Channel, e.Data1, e.Data2),
            PluginEventKind.ControlChange => PluginEvent.ControlChange(offset, e.Channel, e.Data1, e.Data2),
            PluginEventKind.ProgramChange => PluginEvent.ProgramChange(offset, e.Channel, e.Data1),
            PluginEventKind.ChannelPressure => PluginEvent.ChannelPressure(offset, e.Channel, e.Data1),
            PluginEventKind.PitchBend => PluginEvent.PitchBend(offset, e.Channel, e.Data1 | (e.Data2 << 7)),
            _ => null,
        };
    }

    private static ImmutableArray<ParameterDescriptor> ReadParameters(nint handle)
    {
        var json = NativeMethods.TakeString(NativeMethods.PluginParameters(handle)) ?? "[]";
        using var document = JsonDocument.Parse(json);
        var all = new List<ParameterDescriptor>();
        foreach (var p in document.RootElement.EnumerateArray())
        {
            var id = p.GetProperty("id").GetUInt32();
            var name = p.GetProperty("name").GetString();
            var units = p.GetProperty("units").GetString() ?? string.Empty;
            var defaultValue = Math.Clamp(p.GetProperty("default").GetDouble(), 0, 1);
            var steps = Math.Max(0, p.GetProperty("steps").GetInt32());
            var traits = (ParameterTraits)(p.GetProperty("flags").GetInt32() & (int)ParameterDescriptor.AllTraits);
            all.Add(new ParameterDescriptor(id, string.IsNullOrWhiteSpace(name) ? $"Parameter {id}" : Truncate(name), double.IsFinite(defaultValue) ? defaultValue : 0, steps, Truncate(units), traits));
        }

        if (all.Count <= ProtocolLimits.MaxParameters)
        {
            return [.. all];
        }

        // A plugin may list more parameters than the protocol carries: the ones Bluestone shows are kept first.
        return [.. all.Select((p, i) => (Parameter: p, Index: i))
            .OrderBy(x => x.Parameter.IsUserFacing ? 0 : 1)
            .Take(ProtocolLimits.MaxParameters)
            .OrderBy(x => x.Index)
            .Select(x => x.Parameter)];
    }

    private static string Truncate(string text) => text.Length <= 512 ? text : text[..512];
}
