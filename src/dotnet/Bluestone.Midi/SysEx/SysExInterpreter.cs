using Bluestone.Domain.Midi;
using Bluestone.Midi.Wire;

namespace Bluestone.Midi.SysEx;

/// <summary>
/// What Bluestone recognized in a SysEx message. Interpretations are derived on demand and never
/// replace the message: its bytes remain the only thing that is stored, played, and exported.
/// </summary>
public abstract record SysExInterpretation
{
    /// <summary>A short description for display. Never includes bulk data.</summary>
    public abstract string Summary { get; }
}

/// <summary>
/// Recognizes Universal SysEx and the manufacturer dialects Bluestone knows (Yamaha XG, Roland GS) by
/// dispatching on the manufacturer ID. Each dialect keeps its own model; nothing is shared that is
/// not genuinely common.
/// </summary>
/// <remarks>
/// Unrecognized SysEx is not invalid SysEx. <see cref="Interpret(ReadOnlySpan{byte})"/> returns
/// <see langword="null"/> for it, and it stays an opaque message that is preserved and sent
/// unchanged. To add a dialect, add its file beside this one and a case to the dispatch.
/// </remarks>
public static class SysExInterpreter
{
    public static SysExInterpretation? Interpret(SysExMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Interpret(message.Bytes.Span);
    }

    /// <summary>Interprets one complete message, <c>F0</c> through <c>F7</c>; anything else returns <see langword="null"/>.</summary>
    public static SysExInterpretation? Interpret(ReadOnlySpan<byte> message)
    {
        if (MidiWire.Classify(message) != MidiMessageClass.SystemExclusive || message.Length < 3)
        {
            return null;
        }

        var body = message[1..^1];
        return body[0] switch
        {
            UniversalDialect.NonRealtime or UniversalDialect.Realtime => UniversalDialect.Interpret(body),
            XgDialect.Yamaha => XgDialect.Interpret(body),
            GsDialect.Roland => GsDialect.Interpret(body),
            _ => null,
        };
    }
}
