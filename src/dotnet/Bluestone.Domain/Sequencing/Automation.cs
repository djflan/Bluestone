using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Time;

namespace Bluestone.Domain.Sequencing;

/// <summary>Stable identity of an automation lane within a project.</summary>
public readonly record struct AutomationLaneId(Guid Value)
{
    public static AutomationLaneId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public enum AutomationParameter
{
    Controller,
    PitchBend,
    ChannelPressure,

    /// <summary>A parameter of one device instance. It is delivered to the device, never sent as MIDI (ADR 0024).</summary>
    DeviceParameter,

    /// <summary>
    /// A parameter of an external instrument's part, named by its key in the instrument's profile. It is
    /// translated into the MIDI the profile maps it to (CC, RPN, NRPN, or SysEx) when the plan is compiled (ADR 0031).
    /// </summary>
    InstrumentParameter,
}

/// <summary>
/// What an automation lane controls. Either MIDI performance data on one channel: a controller, pitch
/// bend, or channel pressure (ADR 0020); or one parameter of one device instance (ADR 0024); or one
/// parameter of an external instrument's part (ADR 0031).
/// </summary>
/// <remarks>
/// <para>
/// MIDI controllers whose lone values mean something other than a level are refused: bank select, the
/// LSB controllers (32-63), data entry and increment/decrement, RPN/NRPN selectors, and channel mode
/// messages.
/// </para>
/// <para>
/// A device target names the device by its stable <see cref="DeviceId"/> and the parameter by its own
/// ID, so moving the device within or between chains retargets nothing. Its values are normalized
/// (0 to 1, see <see cref="ControlValue.FromFraction"/>).
/// </para>
/// <para>
/// An instrument target names the part: the external instrument, its port, and the channel, and the
/// parameter by its key in the instrument's profile (its parameter map). Its values are normalized too,
/// and scaled to the parameter's range when they are translated. It is not tied to the track's
/// connections: a lane on any track can set any part's parameter.
/// </para>
/// </remarks>
public readonly record struct AutomationTarget
{
    /// <summary>The longest port ID or parameter key an instrument target holds.</summary>
    public const int MaxKeyLength = 64;

    private AutomationTarget(AutomationParameter parameter, MidiChannel channel, ControllerNumber controller, DeviceId device = default, ParameterId deviceParameter = default)
    {
        Parameter = parameter;
        Channel = channel;
        Controller = controller;
        Device = device;
        DeviceParameter = deviceParameter;
    }

    private AutomationTarget(ExternalInstrumentId instrument, string port, MidiChannel channel, string parameter)
    {
        Parameter = AutomationParameter.InstrumentParameter;
        Channel = channel;
        Instrument = instrument;
        Port = port;
        InstrumentParameter = parameter;
    }

    public AutomationParameter Parameter { get; }

    /// <summary>The channel, for MIDI and instrument targets. Meaningless for device targets.</summary>
    public MidiChannel Channel { get; }

    /// <summary>The controller, for <see cref="AutomationParameter.Controller"/> targets.</summary>
    public ControllerNumber Controller { get; }

    /// <summary>The device, for <see cref="AutomationParameter.DeviceParameter"/> targets.</summary>
    public DeviceId Device { get; }

    /// <summary>The device's parameter, for <see cref="AutomationParameter.DeviceParameter"/> targets.</summary>
    public ParameterId DeviceParameter { get; }

    /// <summary>The external instrument, for <see cref="AutomationParameter.InstrumentParameter"/> targets.</summary>
    public ExternalInstrumentId Instrument { get; }

    /// <summary>The instrument's port ID, for <see cref="AutomationParameter.InstrumentParameter"/> targets.</summary>
    public string? Port { get; }

    /// <summary>The parameter's key in the instrument's profile, for <see cref="AutomationParameter.InstrumentParameter"/> targets.</summary>
    public string? InstrumentParameter { get; }

    /// <summary>True for targets that become MIDI events of the track; false for device and instrument parameters.</summary>
    public bool IsMidi => Parameter is AutomationParameter.Controller or AutomationParameter.PitchBend or AutomationParameter.ChannelPressure;

    /// <summary>True for a device parameter target.</summary>
    public bool IsDevice => Parameter == AutomationParameter.DeviceParameter;

    /// <summary>True for an external instrument parameter target.</summary>
    public bool IsInstrument => Parameter == AutomationParameter.InstrumentParameter;

    /// <summary>False for a target not made by one of the factories, such as <c>default</c>.</summary>
    public bool IsValid => Parameter switch
    {
        AutomationParameter.Controller => IsAutomatable(Controller),
        AutomationParameter.DeviceParameter => Device.Value != Guid.Empty,
        AutomationParameter.InstrumentParameter => Instrument.Value != Guid.Empty && !string.IsNullOrEmpty(Port) && !string.IsNullOrEmpty(InstrumentParameter),
        _ => true,
    };

    /// <exception cref="ArgumentException">The controller cannot be automated (see <see cref="IsAutomatable"/>).</exception>
    public static AutomationTarget ForController(MidiChannel channel, ControllerNumber controller) =>
        IsAutomatable(controller)
            ? new(AutomationParameter.Controller, channel, controller)
            : throw new ArgumentException($"Controller {controller} cannot be automated.", nameof(controller));

    public static AutomationTarget ForPitchBend(MidiChannel channel) => new(AutomationParameter.PitchBend, channel, default);

    public static AutomationTarget ForChannelPressure(MidiChannel channel) => new(AutomationParameter.ChannelPressure, channel, default);

    /// <exception cref="ArgumentException">The device ID is empty.</exception>
    public static AutomationTarget ForDevice(DeviceId device, ParameterId parameter) =>
        device.Value != Guid.Empty
            ? new(AutomationParameter.DeviceParameter, default, default, device, parameter)
            : throw new ArgumentException("A device target needs a device.", nameof(device));

    /// <summary>A parameter of the part of <paramref name="instrument"/> on <paramref name="port"/> and <paramref name="channel"/>.</summary>
    /// <param name="instrument">The external instrument.</param>
    /// <param name="port">The instrument's port ID, such as "A".</param>
    /// <param name="channel">The part's channel.</param>
    /// <param name="parameter">The parameter's key in the instrument's profile.</param>
    /// <exception cref="ArgumentException">The instrument ID is empty, or the port or key is empty or too long.</exception>
    public static AutomationTarget ForInstrument(ExternalInstrumentId instrument, string port, MidiChannel channel, string parameter)
    {
        if (instrument.Value == Guid.Empty)
        {
            throw new ArgumentException("An instrument target needs an instrument.", nameof(instrument));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(port);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);
        if (port.Length > MaxKeyLength)
        {
            throw new ArgumentException($"Port IDs are at most {MaxKeyLength} characters.", nameof(port));
        }

        if (parameter.Length > MaxKeyLength)
        {
            throw new ArgumentException($"Parameter keys are at most {MaxKeyLength} characters.", nameof(parameter));
        }

        return new(instrument, port, channel, parameter);
    }

    public static bool IsAutomatable(ControllerNumber controller) =>
        !(controller.IsBankSelect || controller.Value is >= 32 and <= 63 or 6 or 96 or 97 || controller.IsParameterNumberSelector || controller.IsChannelMode);

    /// <summary>
    /// The target <paramref name="e"/> sets, or null if no lane could control it. An LSB controller
    /// (32-63) belongs to its MSB controller's target, since it sets the fine part of the same value.
    /// </summary>
    public static AutomationTarget? Of(ChannelEvent e) => e switch
    {
        ControllerEvent c when IsAutomatable(c.Controller) => new(AutomationParameter.Controller, c.Channel, c.Controller),
        ControllerEvent c when c.Controller.Value is >= 32 and <= 63 && IsAutomatable(new ControllerNumber(c.Controller.Value - 32)) =>
            new(AutomationParameter.Controller, c.Channel, new ControllerNumber(c.Controller.Value - 32)),
        PitchBendEvent p => ForPitchBend(p.Channel),
        ChannelPressureEvent p => ForChannelPressure(p.Channel),
        _ => null,
    };

    /// <summary>The same target on <paramref name="channel"/>. Device and instrument targets are returned unchanged.</summary>
    public AutomationTarget WithChannel(MidiChannel channel) => IsMidi ? new(Parameter, channel, Controller) : this;

    /// <summary>
    /// <paramref name="value"/> as the target is sent, scaled back up: as MIDI 1.0 sends a MIDI target, and
    /// unchanged for a device or instrument parameter, which is delivered (or translated) at full resolution.
    /// </summary>
    public ControlValue AtMidi1Resolution(ControlValue value) => Parameter switch
    {
        AutomationParameter.PitchBend => ControlValue.FromFourteenBit(value.ToFourteenBit()),
        AutomationParameter.DeviceParameter or AutomationParameter.InstrumentParameter => value,
        _ => ControlValue.FromSevenBit(value.ToSevenBit()),
    };

    /// <summary>An event setting this target to <paramref name="value"/>. It is rendered, not stored, so it has no ID.</summary>
    /// <exception cref="InvalidOperationException">The target is a device or instrument parameter, which is not a track event.</exception>
    public ChannelEvent CreateEvent(Tick position, ControlValue value) => Parameter switch
    {
        AutomationParameter.PitchBend => new PitchBendEvent(default, position, Channel, value),
        AutomationParameter.ChannelPressure => new ChannelPressureEvent(default, position, Channel, value),
        AutomationParameter.DeviceParameter => throw new InvalidOperationException("A device parameter is not a MIDI event."),
        AutomationParameter.InstrumentParameter => throw new InvalidOperationException("An instrument parameter is translated through its profile, not rendered as one event."),
        _ => new ControllerEvent(default, position, Channel, Controller, value),
    };
}

/// <summary>How a lane moves from one point to the next.</summary>
public enum AutomationCurve
{
    /// <summary>Stays at the point's value until the next point.</summary>
    Hold,

    /// <summary>Moves in a straight line to the next point's value.</summary>
    Linear,
}

/// <summary>A breakpoint: the value at <paramref name="Position"/>, and how the lane leaves it.</summary>
public readonly record struct AutomationPoint(Tick Position, ControlValue Value, AutomationCurve Curve = AutomationCurve.Linear);

/// <summary>
/// An immutable automation lane: a target and breakpoints in arrangement time. Before its first point
/// the lane holds the first value; after its last, the last value.
/// </summary>
public sealed class AutomationLane
{
    public const int MaxPoints = 1_000_000;

    private AutomationLane(AutomationLaneId id, AutomationTarget target, ImmutableArray<AutomationPoint> points)
    {
        Id = id;
        Target = target;
        Points = points;
    }

    /// <exception cref="ArgumentException">The target is not valid, two points share a position, or there are too many.</exception>
    public AutomationLane(AutomationLaneId id, AutomationTarget target, IEnumerable<AutomationPoint> points)
        : this(id, Validate(target), Sort(points))
    {
    }

    public static AutomationLane Create(AutomationTarget target) => new(AutomationLaneId.New(), Validate(target), ImmutableArray<AutomationPoint>.Empty);

    public AutomationLaneId Id { get; }

    public AutomationTarget Target { get; }

    /// <summary>Breakpoints in position order, at most one per tick.</summary>
    public ImmutableArray<AutomationPoint> Points { get; }

    /// <summary>The position of the last point, or zero if there are none.</summary>
    public Tick EndPosition => Points.IsEmpty ? Tick.Zero : Points[^1].Position;

    /// <summary>The lane's value at <paramref name="position"/>, or null if it has no points.</summary>
    public ControlValue? ValueAt(Tick position)
    {
        if (Points.IsEmpty)
        {
            return null;
        }

        var index = LastAtOrBefore(position);
        if (index < 0)
        {
            return Points[0].Value;
        }

        var point = Points[index];
        return index + 1 < Points.Length && point.Curve == AutomationCurve.Linear
            ? Interpolate(point, Points[index + 1], position.Value)
            : point.Value;
    }

    /// <summary>Adds <paramref name="point"/>, replacing any point at the same position.</summary>
    public AutomationLane WithPoint(AutomationPoint point) =>
        new(Id, Target, Sort(Points.Where(p => p.Position != point.Position).Append(point)));

    /// <summary>Removes the points in [<paramref name="from"/>, <paramref name="to"/>].</summary>
    public AutomationLane WithoutPoints(Tick from, Tick to)
    {
        var kept = Points.Where(p => p.Position < from || p.Position > to).ToImmutableArray();
        return kept.Length == Points.Length ? this : new AutomationLane(Id, Target, kept);
    }

    /// <summary>Replaces the points in [<paramref name="from"/>, <paramref name="to"/>] with <paramref name="points"/>.</summary>
    /// <exception cref="ArgumentException">Two of the new points share a position.</exception>
    public AutomationLane ReplaceRange(Tick from, Tick to, IEnumerable<AutomationPoint> points) =>
        new(Id, Target, Sort(Points.Where(p => p.Position < from || p.Position > to).Concat(points)));


    /// <exception cref="ArgumentException">Two points share a position.</exception>
    public AutomationLane WithPoints(IEnumerable<AutomationPoint> points) => new(Id, Target, Sort(points));

    internal static ControlValue Interpolate(AutomationPoint from, AutomationPoint to, long position)
    {
        var span = to.Position.Value - from.Position.Value;
        var offset = Math.Clamp(position - from.Position.Value, 0, span);
        var value = (Int128)from.Value.Value + (((Int128)to.Value.Value - from.Value.Value) * offset / span);
        return new ControlValue((uint)value);
    }

    private int LastAtOrBefore(Tick position) => Search.LastAtOrBefore(Points, position, p => p.Position);

    private static AutomationTarget Validate(AutomationTarget target) =>
        target.IsValid
            ? target
            : throw new ArgumentException(
                target.Parameter switch
                {
                    AutomationParameter.DeviceParameter => "A device target needs a device.",
                    AutomationParameter.InstrumentParameter => "An instrument target needs an instrument, a port, and a parameter.",
                    _ => $"Controller {target.Controller} cannot be automated.",
                },
                nameof(target));

    private static ImmutableArray<AutomationPoint> Sort(IEnumerable<AutomationPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var sorted = points.OrderBy(p => p.Position).ToImmutableArray();
        if (sorted.Length > MaxPoints)
        {
            throw new ArgumentException($"A lane holds at most {MaxPoints} points.", nameof(points));
        }

        for (var i = 1; i < sorted.Length; i++)
        {
            if (sorted[i].Position == sorted[i - 1].Position)
            {
                throw new ArgumentException($"Two points are at tick {sorted[i].Position}; use a hold curve for a jump.", nameof(points));
            }
        }

        return sorted;
    }
}

/// <summary>Turns automation lanes into the values a target is sent: MIDI 1.0 values, or full-resolution device parameter values.</summary>
public static class AutomationRenderer
{
    /// <summary>How often linear segments are sampled: every 1/32 of a quarter note, at least every tick.</summary>
    public static TickSpan DefaultInterval(Ppqn ppqn) => new(Math.Max(1, ppqn.TicksPerQuarterNote / 32));

    /// <summary>
    /// The values to send for <paramref name="lane"/>, at its target's MIDI 1.0 resolution: the first value
    /// at tick 0, each point's value at its position, and linear segments every <paramref name="interval"/>.
    /// A value is sent only when it differs from the last one sent.
    /// </summary>
    /// <remarks>
    /// A linear segment only ever moves one way, so the next sample to send is found by binary search
    /// over its sampling grid. The cost follows the number of values sent, not the segment's length.
    /// </remarks>
    public static IEnumerable<(Tick Position, ControlValue Value)> Sample(AutomationLane lane, TickSpan interval)
    {
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentOutOfRangeException.ThrowIfLessThan(interval.Value, 1, nameof(interval));
        var points = lane.Points;
        if (points.IsEmpty)
        {
            yield break;
        }

        ControlValue? last = null;
        var target = lane.Target;
        if (points[0].Position > Tick.Zero && Emit(points[0].Value, out var first))
        {
            yield return (Tick.Zero, first);
        }

        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if (Emit(point.Value, out var value))
            {
                yield return (point.Position, value);
            }

            if (i + 1 == points.Length || point.Curve != AutomationCurve.Linear)
            {
                continue;
            }

            // Grid ticks strictly inside the segment are point + k * interval for k in 1..steps.
            var next = points[i + 1];
            var steps = (next.Position.Value - point.Position.Value - 1) / interval.Value;
            long k = 0;
            while (k < steps && FirstChange(point, next, k, steps) is { } change)
            {
                k = change;
                var tick = point.Position.Value + (k * interval.Value);
                Emit(AutomationLane.Interpolate(point, next, tick), out value);
                yield return (new Tick(tick), value);
            }
        }

        bool Emit(ControlValue raw, out ControlValue sent)
        {
            sent = target.AtMidi1Resolution(raw);
            if (sent == last)
            {
                return false;
            }

            last = sent;
            return true;
        }

        // The first grid step after `from` whose value differs from the last one sent, or null.
        long? FirstChange(AutomationPoint start, AutomationPoint end, long from, long steps)
        {
            ControlValue At(long step) => target.AtMidi1Resolution(AutomationLane.Interpolate(start, end, start.Position.Value + (step * interval.Value)));
            if (At(steps) == last)
            {
                return null;
            }

            long low = from + 1, high = steps;
            while (low < high)
            {
                var mid = low + ((high - low) / 2);
                if (At(mid) == last)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            return low;
        }
    }
}
