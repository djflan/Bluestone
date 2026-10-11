using System.Globalization;

namespace Bluestone.Midi.Files;

public enum SmfDiagnosticSeverity
{
    /// <summary>Nothing was lost; the file used an unusual but understood construct.</summary>
    Info,

    /// <summary>Data was repaired, approximated, skipped, or will not round-trip exactly.</summary>
    Warning,
}

/// <summary>
/// A finding from reading, importing, or exporting a MIDI file. <see cref="Code"/> is stable and
/// suitable for documentation and tests; <see cref="Message"/> is for people.
/// </summary>
public sealed record SmfDiagnostic(SmfDiagnosticSeverity Severity, string Code, string Message, int? TrackIndex = null, int Count = 1)
{
    public override string ToString()
    {
        var where = TrackIndex is { } track ? string.Create(CultureInfo.InvariantCulture, $" (track {track + 1})") : string.Empty;
        var times = Count > 1 ? string.Create(CultureInfo.InvariantCulture, $" ×{Count}") : string.Empty;
        return $"{Severity} {Code}{where}: {Message}{times}";
    }
}

/// <summary>Collects diagnostics, folding repeats of the same code and track into one counted entry.</summary>
internal sealed class DiagnosticBag
{
    private readonly List<SmfDiagnostic> _items = [];

    public void Add(SmfDiagnosticSeverity severity, string code, string message, int? trackIndex = null)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            var existing = _items[i];
            if (existing.Code == code && existing.TrackIndex == trackIndex)
            {
                _items[i] = existing with { Count = existing.Count + 1 };
                return;
            }
        }

        _items.Add(new SmfDiagnostic(severity, code, message, trackIndex));
    }

    public void Info(string code, string message, int? trackIndex = null) => Add(SmfDiagnosticSeverity.Info, code, message, trackIndex);

    public void Warn(string code, string message, int? trackIndex = null) => Add(SmfDiagnosticSeverity.Warning, code, message, trackIndex);

    public IReadOnlyList<SmfDiagnostic> ToList() => [.. _items];
}

/// <summary>A MIDI file that cannot be read at all: missing header, unsupported format, or a limit exceeded.</summary>
public sealed class SmfFormatException : Exception
{
    public SmfFormatException()
    {
    }

    public SmfFormatException(string message)
        : base(message)
    {
    }

    public SmfFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
