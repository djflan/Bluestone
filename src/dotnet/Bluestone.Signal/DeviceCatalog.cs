using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Time;
using Bluestone.Signal.BuiltIn;
using Bluestone.Signal.Live;

namespace Bluestone.Signal;

/// <summary>Creates the processor for one device instance, at the sequence's resolution.</summary>
public delegate ISignalProcessor SignalProcessorFactory(Ppqn ppqn);

/// <summary>
/// The device definitions Bluestone knows about, and, for built-in devices, how to run them. Plugin
/// definitions (from a scan) are data only: a plugin never gets an in-process factory, because plugins
/// run in worker processes (ADR 0025). An <see cref="IOutOfProcessRenderer"/> can be attached to run them there
/// at plan-compile time. Immutable; <see cref="With"/> and <see cref="WithRenderer"/> return a new catalog.
/// </summary>
public sealed class DeviceCatalog
{
    private readonly ImmutableDictionary<DeviceDefinitionId, DeviceDefinition> _definitions;
    private readonly ImmutableDictionary<DeviceDefinitionId, SignalProcessorFactory> _factories;
    private readonly ImmutableDictionary<DeviceDefinitionId, LiveProcessorFactory> _liveFactories;

    private DeviceCatalog(
        ImmutableDictionary<DeviceDefinitionId, DeviceDefinition> definitions,
        ImmutableDictionary<DeviceDefinitionId, SignalProcessorFactory> factories,
        ImmutableDictionary<DeviceDefinitionId, LiveProcessorFactory> liveFactories,
        IOutOfProcessRenderer? renderer)
    {
        _definitions = definitions;
        _factories = factories;
        _liveFactories = liveFactories;
        Renderer = renderer;
    }

    public static DeviceCatalog Empty { get; } = new(
        ImmutableDictionary<DeviceDefinitionId, DeviceDefinition>.Empty,
        ImmutableDictionary<DeviceDefinitionId, SignalProcessorFactory>.Empty,
        ImmutableDictionary<DeviceDefinitionId, LiveProcessorFactory>.Empty,
        null);

    /// <summary>The devices that ship with Bluestone: Transpose, Event Filter, and Arpeggiator, each with its plan-time and live processor.</summary>
    public static DeviceCatalog BuiltIn { get; } = Empty
        .With(BuiltInDevices.Transpose, _ => new TransposeProcessor(), _ => new LiveTransposeProcessor())
        .With(BuiltInDevices.EventFilter, _ => new EventFilterProcessor(), _ => new LiveEventFilterProcessor())
        .With(BuiltInDevices.Arpeggiator, ppqn => new ArpeggiatorProcessor(ppqn), ppqn => new LiveArpeggiatorProcessor(ppqn));

    /// <summary>Every definition, in ID order.</summary>
    public ImmutableArray<DeviceDefinition> Definitions => [.. _definitions.Values.OrderBy(d => d.Id.Value, StringComparer.Ordinal)];

    /// <summary>What runs devices that have no in-process processor at plan-compile time, if anything.</summary>
    public IOutOfProcessRenderer? Renderer { get; }

    /// <summary>Adds or replaces a definition and, for a built-in device, the factories that run it at plan time and live.</summary>
    /// <exception cref="ArgumentException">A factory was given for a plugin definition.</exception>
    public DeviceCatalog With(DeviceDefinition definition, SignalProcessorFactory? factory = null, LiveProcessorFactory? liveFactory = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if ((factory is not null || liveFactory is not null) && definition.Origin != DeviceOrigin.BuiltIn)
        {
            throw new ArgumentException($"{definition.Name} is a plugin; plugins run in worker processes, never in Bluestone's own process.", nameof(factory));
        }

        var factories = factory is null ? _factories.Remove(definition.Id) : _factories.SetItem(definition.Id, factory);
        var liveFactories = liveFactory is null ? _liveFactories.Remove(definition.Id) : _liveFactories.SetItem(definition.Id, liveFactory);
        return new DeviceCatalog(_definitions.SetItem(definition.Id, definition), factories, liveFactories, Renderer);
    }

    /// <summary>This catalog with <paramref name="renderer"/> running out-of-process devices at plan-compile time (null for none).</summary>
    public DeviceCatalog WithRenderer(IOutOfProcessRenderer? renderer) => new(_definitions, _factories, _liveFactories, renderer);

    public DeviceDefinition? Find(DeviceDefinitionId id) => _definitions.GetValueOrDefault(id);

    /// <summary>This catalog as the lookup the domain's routing checks take.</summary>
    public DeviceDefinitionLookup Lookup => Find;

    public bool CanProcess(DeviceDefinitionId id) => _factories.ContainsKey(id);

    /// <summary>A new processor for one device instance, or null when the device has no in-process implementation.</summary>
    public ISignalProcessor? CreateProcessor(DeviceDefinitionId id, Ppqn ppqn) =>
        _factories.TryGetValue(id, out var factory) ? factory(ppqn) : null;

    /// <summary>A new live (real-time) processor for one device instance, or null when the device has none in Bluestone's process.</summary>
    public ILiveProcessor? CreateLiveProcessor(DeviceDefinitionId id, Ppqn ppqn) =>
        _liveFactories.TryGetValue(id, out var factory) ? factory(ppqn) : null;

    /// <summary>
    /// The live processor for <paramref name="device"/> with its stored parameter values applied, and the classes it
    /// handles; null when it has none in Bluestone's process. Bypass is the caller's business.
    /// </summary>
    public (ILiveProcessor Processor, Domain.Sequencing.EventClass Handles)? CreateLiveStage(DeviceInstance device, Ppqn ppqn)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (Find(device.Definition.Id) is not { } definition || CreateLiveProcessor(definition.Id, ppqn) is not { } processor)
        {
            return null;
        }

        foreach (var parameter in definition.Parameters)
        {
            processor.SetParameter(parameter.Id, device.ValueOf(parameter.Id) ?? parameter.DefaultValue);
        }

        return (processor, definition.Handles);
    }
}
