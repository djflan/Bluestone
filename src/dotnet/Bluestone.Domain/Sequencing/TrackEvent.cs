using Bluestone.Domain.Midi;
using Bluestone.Domain.Time;

namespace Bluestone.Domain.Sequencing;

/// <summary>An event placed at a tick on a track. Concrete events are immutable records.</summary>
public abstract record TrackEvent
{
    protected TrackEvent(EventId id, Tick position)
    {
        Id = id;
        Position = position;
    }

    public EventId Id { get; init; }

    public Tick Position { get; init; }

    /// <summary>Where this event falls among others at the same tick.</summary>
    public abstract EventPhase Phase { get; }

    /// <summary>The last tick this event affects; for notes, the release position.</summary>
    public virtual Tick EndPosition => Position;
}

/// <summary>
/// An event addressed to one channel: notes, controllers, program selections, pitch bend, and
/// pressure. These model what is played, not how it is transmitted; values are kept at MIDI 2.0
/// resolution where it differs, and the protocol encoders decide the wire form.
/// </summary>
public abstract record ChannelEvent : TrackEvent
{
    protected ChannelEvent(EventId id, Tick position, MidiChannel channel)
        : base(id, position) => Channel = channel;

    public MidiChannel Channel { get; init; }
}

/// <summary>
/// A paired note with a duration. Duration is at least one tick so a note's release can never be
/// ordered before its own start.
/// </summary>
public sealed record NoteEvent : ChannelEvent
{
    private readonly TickSpan _duration;
    private readonly Velocity _velocity;

    public NoteEvent(EventId id, Tick position, TickSpan duration, MidiChannel channel, NoteNumber note, Velocity velocity, Velocity releaseVelocity)
        : base(id, position, channel)
    {
        Duration = duration;
        Note = note;
        Velocity = velocity;
        ReleaseVelocity = releaseVelocity;
    }

    public NoteEvent(Tick position, TickSpan duration, MidiChannel channel, NoteNumber note, Velocity velocity)
        : this(EventId.New(), position, duration, channel, note, velocity, Velocity.DefaultRelease)
    {
    }

    public TickSpan Duration
    {
        get => _duration;
        init => _duration = value.Value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Duration), value, "A note must last at least one tick.");
    }

    public NoteNumber Note { get; init; }

    /// <summary>Attack velocity, 1-127. Velocity 0 would be read as a release on the wire.</summary>
    public Velocity Velocity
    {
        get => _velocity;
        init => _velocity = value.IsAudible
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Velocity), value, "A note's velocity must be 1-127.");
    }

    public Velocity ReleaseVelocity { get; init; }

    public override Tick EndPosition => Position + Duration;

    public override EventPhase Phase => EventPhase.NoteOn;
}

/// <summary>A release with no matching start, such as an unpaired note-off in an imported file. Kept for fidelity.</summary>
public sealed record NoteOffEvent : ChannelEvent
{
    public NoteOffEvent(EventId id, Tick position, MidiChannel channel, NoteNumber note, Velocity releaseVelocity)
        : base(id, position, channel)
    {
        Note = note;
        ReleaseVelocity = releaseVelocity;
    }

    public NoteNumber Note { get; init; }

    public Velocity ReleaseVelocity { get; init; }

    public override EventPhase Phase => EventPhase.NoteOff;
}

/// <summary>
/// A controller change. Bank select controllers appear here only when they are not part of a
/// <see cref="ProgramEvent"/>, for example when a bank is selected without a program change.
/// </summary>
public sealed record ControllerEvent : ChannelEvent
{
    public ControllerEvent(EventId id, Tick position, MidiChannel channel, ControllerNumber controller, ControlValue value)
        : base(id, position, channel)
    {
        Controller = controller;
        Value = value;
    }

    public ControllerEvent(Tick position, MidiChannel channel, ControllerNumber controller, ControlValue value)
        : this(EventId.New(), position, channel, controller, value)
    {
    }

    public ControllerNumber Controller { get; init; }

    public ControlValue Value { get; init; }

    public override EventPhase Phase => Controller.IsBankSelect ? EventPhase.BankSelect : EventPhase.Control;
}

/// <summary>Selects a program, and optionally its bank, as one operation.</summary>
public sealed record ProgramEvent : ChannelEvent
{
    public ProgramEvent(EventId id, Tick position, MidiChannel channel, ProgramSelection selection)
        : base(id, position, channel) => Selection = selection;

    public ProgramEvent(Tick position, MidiChannel channel, ProgramSelection selection)
        : this(EventId.New(), position, channel, selection)
    {
    }

    public ProgramSelection Selection { get; init; }

    public override EventPhase Phase => EventPhase.ProgramChange;
}

/// <summary>A pitch bend position; <see cref="ControlValue.Center"/> is at rest.</summary>
public sealed record PitchBendEvent : ChannelEvent
{
    public PitchBendEvent(EventId id, Tick position, MidiChannel channel, ControlValue value)
        : base(id, position, channel) => Value = value;

    public PitchBendEvent(Tick position, MidiChannel channel, ControlValue value)
        : this(EventId.New(), position, channel, value)
    {
    }

    public ControlValue Value { get; init; }

    public override EventPhase Phase => EventPhase.Control;
}

/// <summary>Pressure (aftertouch) applied to the whole channel.</summary>
public sealed record ChannelPressureEvent : ChannelEvent
{
    public ChannelPressureEvent(EventId id, Tick position, MidiChannel channel, ControlValue pressure)
        : base(id, position, channel) => Pressure = pressure;

    public ChannelPressureEvent(Tick position, MidiChannel channel, ControlValue pressure)
        : this(EventId.New(), position, channel, pressure)
    {
    }

    public ControlValue Pressure { get; init; }

    public override EventPhase Phase => EventPhase.Control;
}

/// <summary>Pressure (aftertouch) applied to one note.</summary>
public sealed record PolyPressureEvent : ChannelEvent
{
    public PolyPressureEvent(EventId id, Tick position, MidiChannel channel, NoteNumber note, ControlValue pressure)
        : base(id, position, channel)
    {
        Note = note;
        Pressure = pressure;
    }

    public NoteNumber Note { get; init; }

    public ControlValue Pressure { get; init; }

    public override EventPhase Phase => EventPhase.Control;
}

/// <summary>A complete System Exclusive message.</summary>
public sealed record SysExEvent : TrackEvent
{
    public SysExEvent(EventId id, Tick position, SysExMessage message)
        : base(id, position) => Message = message ?? throw new ArgumentNullException(nameof(message));

    public SysExEvent(Tick position, SysExMessage message)
        : this(EventId.New(), position, message)
    {
    }

    public SysExMessage Message { get; init; }

    public override EventPhase Phase => EventPhase.SystemExclusive;
}

/// <summary>
/// Bytes transmitted verbatim, such as a Standard MIDI File "escape" (<c>F7</c>) event or a SysEx
/// packet that was split across several events. Preserved for round-trip fidelity; not interpreted.
/// </summary>
public sealed record RawMidiEvent : TrackEvent
{
    public const int MaxLength = SysExMessage.MaxLength;

    public RawMidiEvent(EventId id, Tick position, ByteBlock bytes)
        : base(id, position)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Bytes = bytes.Length <= MaxLength
            ? bytes
            : throw new ArgumentOutOfRangeException(nameof(bytes), bytes.Length, "Raw event payload is too large.");
    }

    public ByteBlock Bytes { get; init; }

    public override EventPhase Phase => EventPhase.SystemExclusive;
}

/// <summary>
/// A meta event that Bluestone does not model directly (text, lyrics, cue points, sequencer-specific
/// data, or unknown types), preserved for round-trip fidelity. Tempo and time signature live in the
/// sequence's <see cref="TempoMap"/> and <see cref="MeterMap"/>, not here.
/// </summary>
public sealed record MetaEvent : TrackEvent
{
    public const int MaxLength = SysExMessage.MaxLength;

    public MetaEvent(EventId id, Tick position, byte type, ByteBlock data)
        : base(id, position)
    {
        ArgumentNullException.ThrowIfNull(data);
        Type = type <= 0x7F ? type : throw new ArgumentOutOfRangeException(nameof(type), type, "Meta event types are 0-127.");
        Data = data.Length <= MaxLength
            ? data
            : throw new ArgumentOutOfRangeException(nameof(data), data.Length, "Meta event payload is too large.");
    }

    public byte Type { get; init; }

    public ByteBlock Data { get; init; }

    public override EventPhase Phase => EventPhase.Meta;
}
