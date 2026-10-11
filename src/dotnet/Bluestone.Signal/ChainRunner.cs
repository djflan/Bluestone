using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Signal;

/// <summary>Sees the signal at points inside a chain, for routing taps and instrument inputs.</summary>
public interface IChainObserver
{
    /// <summary>The events after <paramref name="device"/>: what a tap on it sends.</summary>
    void AfterDevice(DeviceId device, ReadOnlySpan<SignalEvent> events);

    /// <summary>The events an instrument with no in-process implementation takes in, and so removes from the chain.</summary>
    void InstrumentInput(DeviceId device, ReadOnlySpan<SignalEvent> events);
}

/// <summary>How the runner treats one device of a chain.</summary>
public enum StageKind
{
    /// <summary>Run by an in-process processor (a built-in device).</summary>
    Processor,

    /// <summary>
    /// An instrument (events in, no events out) that runs elsewhere: a plugin worker, or nowhere yet. The
    /// classes it handles are taken out of the chain and handed to <see cref="IChainObserver.InstrumentInput"/>.
    /// </summary>
    Instrument,

    /// <summary>Changes no events here: bypassed, audio only, not installed, or an event plugin nothing can run.</summary>
    Transparent,

    /// <summary>
    /// Run out of Bluestone's process by the catalog's <see cref="IOutOfProcessRenderer"/> (a plugin MIDI effect in its
    /// worker). If that fails, its events pass through unchanged and the failure is reported.
    /// </summary>
    OutOfProcess,
}

/// <summary>
/// Runs the event side of one device chain, first device to last (ADR 0022). Each device gets only the
/// event classes its definition handles; everything else (SysEx, controllers, whatever it does not
/// declare) goes around it and is merged back in canonical order. A bypassed device is skipped entirely.
/// </summary>
/// <remarks>
/// The runner owns its buffers and reuses them, so once they have grown to the size of the signal, a run
/// allocates nothing beyond the new events devices create. It is built from project state and is not
/// project state: rebuild it when the chain changes. Not thread-safe.
/// </remarks>
public sealed class ChainRunner
{
    private readonly Stage[] _stages;
    private readonly SignalBuffer _current = new();
    private readonly SignalBuffer _next = new();
    private readonly SignalBuffer _handled = new();
    private readonly SignalBuffer _produced = new();
    private readonly SignalBuffer _around = new();
    private readonly List<SignalDiagnostic> _outOfProcessReports = [];
    private readonly TempoMap _tempo;
    private readonly Ppqn _ppqn;

    /// <param name="chain">The chain to run.</param>
    /// <param name="catalog">The definitions and processors; its renderer, if any, runs out-of-process devices.</param>
    /// <param name="ppqn">The sequence's resolution.</param>
    /// <param name="tempo">The sequence's tempo map, which out-of-process devices need to place events in time.</param>
    public ChainRunner(DeviceChain chain, DeviceCatalog catalog, Ppqn ppqn, TempoMap? tempo = null)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(catalog);
        Chain = chain;
        _ppqn = ppqn;
        _tempo = tempo ?? TempoMap.Constant(ppqn, Tempo.Default);
        var stages = new Stage[chain.Devices.Length];
        var diagnostics = ImmutableArray.CreateBuilder<SignalDiagnostic>();
        for (var i = 0; i < stages.Length; i++)
        {
            stages[i] = CreateStage(chain.Devices[i], catalog, ppqn, diagnostics);
        }

        _stages = stages;
        Diagnostics = diagnostics.ToImmutable();
    }

    public DeviceChain Chain { get; }

    /// <summary>What was found while building the runner: devices that are not installed or that run only in a worker.</summary>
    public ImmutableArray<SignalDiagnostic> Diagnostics { get; }

    public StageKind KindOf(DeviceId device)
    {
        foreach (var stage in _stages)
        {
            if (stage.Device.Id == device)
            {
                return stage.Kind;
            }
        }

        throw new KeyNotFoundException($"Device {device} is not in this chain.");
    }

    /// <summary>Whether parameter changes for <paramref name="device"/> are applied here (it has an in-process processor).</summary>
    public bool Processes(DeviceId device)
    {
        foreach (var stage in _stages)
        {
            if (stage.Device.Id == device)
            {
                return stage.Processor is not null;
            }
        }

        return false;
    }

    /// <summary>
    /// Runs <paramref name="input"/> (in canonical order, inside <paramref name="block"/>) through the chain
    /// and appends the result to <paramref name="output"/>. <paramref name="changes"/> (in position order)
    /// are applied to their processors at their positions, before the events at the same tick; changes for
    /// other devices are ignored, and changes before the block apply at its start.
    /// </summary>
    public void Run(in SignalBlock block, ReadOnlySpan<SignalEvent> input, SignalBuffer output, ReadOnlySpan<ParameterChange> changes = default, IChainObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        _current.Clear();
        _current.AddRange(input);
        foreach (var stage in _stages)
        {
            if (stage.Device.IsBypassed || stage.Kind == StageKind.Transparent)
            {
                if (stage.Processor is not null)
                {
                    ApplyAll(stage, block, changes);
                }

                observer?.AfterDevice(stage.Device.Id, _current.Events);
                continue;
            }

            _handled.Clear();
            _around.Clear();
            foreach (var e in _current.Events)
            {
                if ((EventClassification.Of(e.Event) & stage.Handles) != EventClass.None)
                {
                    _handled.Add(e);
                }
                else
                {
                    _around.Add(e);
                }
            }

            if (stage.Kind == StageKind.Instrument)
            {
                observer?.InstrumentInput(stage.Device.Id, _handled.Events);
                SignalBuffer.Swap(_current, _around);
            }
            else
            {
                _produced.Clear();
                if (stage.Kind == StageKind.OutOfProcess)
                {
                    RunOutOfProcess(stage, block, changes);
                }
                else
                {
                    RunProcessor(stage, block, changes);
                }

                if (!SignalOrder.IsOrdered(_produced.Events))
                {
                    SignalOrder.StableSort(_produced.Writable);
                }

                _next.Clear();
                SignalOrder.Merge(_produced.Events, _around.Events, _next);
                SignalBuffer.Swap(_current, _next);
            }

            observer?.AfterDevice(stage.Device.Id, _current.Events);
        }

        output.AddRange(_current.Events);
    }

    /// <summary>Forgets every processor's state, for a jump in the timeline.</summary>
    public void Reset()
    {
        foreach (var stage in _stages)
        {
            stage.Processor?.Reset();
        }
    }

    /// <summary>What the processors have to report since the last call (notes dropped, for example). Not for the processing path.</summary>
    public ImmutableArray<SignalDiagnostic> TakeReports()
    {
        var reports = ImmutableArray.CreateBuilder<SignalDiagnostic>();
        reports.AddRange(_outOfProcessReports);
        _outOfProcessReports.Clear();
        foreach (var stage in _stages)
        {
            if (stage.Processor is IReportingProcessor reporting)
            {
                foreach (var message in reporting.TakeReport())
                {
                    reports.Add(new SignalDiagnostic(SignalDiagnosticCode.DeviceReport, $"{stage.Device.DisplayName}: {message}") { Device = stage.Device.Id });
                }
            }
        }

        return reports.ToImmutable();
    }

    // The renderer gets this device's changes and its handled events; on failure they pass through unchanged.
    private void RunOutOfProcess(Stage stage, in SignalBlock block, ReadOnlySpan<ParameterChange> changes)
    {
        var own = new List<ParameterChange>();
        foreach (var change in changes)
        {
            if (change.Device == stage.Device.Id && change.Position < block.End)
            {
                own.Add(change);
            }
        }

        var request = new OutOfProcessBlock(stage.Device, stage.Definition!, block, _tempo, _ppqn);
        OutOfProcessResult result;
        try
        {
            result = stage.Renderer!.Render(request, _handled.Events, own.ToArray());
        }
#pragma warning disable CA1031 // The renderer's contract is not to throw; if it does, the chain still must not fail.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            result = OutOfProcessResult.Failed($"could not run ({ex.Message}).");
        }

        if (result.Events is { } events)
        {
            _produced.AddRange(events.AsSpan());
        }
        else
        {
            _produced.AddRange(_handled.Events);
        }

        foreach (var message in result.Messages)
        {
            var text = result.Events is null ? $"{stage.Device.DisplayName}: {message} Events pass through it unchanged." : $"{stage.Device.DisplayName}: {message}";
            _outOfProcessReports.Add(new SignalDiagnostic(result.Events is null ? SignalDiagnosticCode.NotProcessedHere : SignalDiagnosticCode.DeviceReport, text) { Device = stage.Device.Id });
        }
    }

    // Splits the block at each change for this device, so a change applies from its own tick onwards.
    private void RunProcessor(Stage stage, in SignalBlock block, ReadOnlySpan<ParameterChange> changes)
    {
        var processor = stage.Processor!;
        var events = _handled.Events;
        var start = block.Start;
        var from = 0;
        foreach (var change in changes)
        {
            if (change.Device != stage.Device.Id || change.Position >= block.End)
            {
                continue;
            }

            if (change.Position > start)
            {
                var until = from;
                while (until < events.Length && events[until].Event.Position < change.Position)
                {
                    until++;
                }

                processor.Process(new SignalBlock(start, change.Position), events[from..until], _produced);
                start = change.Position;
                from = until;
            }

            processor.SetParameter(change.Parameter, change.Value);
        }

        processor.Process(new SignalBlock(start, block.End), events[from..], _produced);
    }

    private static void ApplyAll(Stage stage, in SignalBlock block, ReadOnlySpan<ParameterChange> changes)
    {
        foreach (var change in changes)
        {
            if (change.Device == stage.Device.Id && change.Position < block.End)
            {
                stage.Processor!.SetParameter(change.Parameter, change.Value);
            }
        }
    }

    private static Stage CreateStage(DeviceInstance device, DeviceCatalog catalog, Ppqn ppqn, ImmutableArray<SignalDiagnostic>.Builder diagnostics)
    {
        if (catalog.Find(device.Definition.Id) is not { } definition)
        {
            diagnostics.Add(new SignalDiagnostic(SignalDiagnosticCode.DeviceUnavailable, $"{device.DisplayName} is not installed; events pass through it unchanged.") { Device = device.Id });
            return new Stage(device, StageKind.Transparent, EventClass.None, null, null, null);
        }

        var consumesEvents = definition.Consumes.HasFlag(SignalKinds.Events) && definition.Handles != EventClass.None;
        if (catalog.CreateProcessor(definition.Id, ppqn) is { } processor)
        {
            foreach (var parameter in definition.Parameters)
            {
                processor.SetParameter(parameter.Id, device.ValueOf(parameter.Id) ?? parameter.DefaultValue);
            }

            return new Stage(device, consumesEvents ? StageKind.Processor : StageKind.Transparent, definition.Handles, processor, null, null);
        }

        if (consumesEvents && !definition.Produces.HasFlag(SignalKinds.Events))
        {
            return new Stage(device, StageKind.Instrument, definition.Handles, null, null, null);
        }

        if (consumesEvents && catalog.Renderer is { } renderer && renderer.CanRender(device, definition))
        {
            return new Stage(device, StageKind.OutOfProcess, definition.Handles, null, renderer, definition);
        }

        if (consumesEvents && !device.IsBypassed)
        {
            var message = definition.Origin == DeviceOrigin.Plugin
                ? $"{device.DisplayName} is not running in a plugin worker, so plan compilation cannot use it; events pass through it unchanged."
                : $"{device.DisplayName} has no processor in this version of Bluestone; events pass through it unchanged.";
            diagnostics.Add(new SignalDiagnostic(SignalDiagnosticCode.NotProcessedHere, message) { Device = device.Id });
        }

        return new Stage(device, StageKind.Transparent, EventClass.None, null, null, null);
    }

    private sealed record Stage(DeviceInstance Device, StageKind Kind, EventClass Handles, ISignalProcessor? Processor, IOutOfProcessRenderer? Renderer, DeviceDefinition? Definition);
}
