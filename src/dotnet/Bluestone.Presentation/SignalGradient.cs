using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bluestone.Presentation;

/// <summary>One colour stop of a <see cref="SignalGradient"/>: <paramref name="Colour"/> is "#RRGGBB" and <paramref name="Position"/> runs from 0 (silence) to 1 (full scale).</summary>
public sealed record SignalGradientStop(string Colour, double Position);

/// <summary>
/// The colours a signal indicator's level runs through, bottom to top. Two to eight stops, sorted by position; two stops at
/// the same position make a hard edge. The clip lamp is a single colour of its own and is not part of the gradient.
/// </summary>
[JsonConverter(typeof(SignalGradientJsonConverter))]
public sealed class SignalGradient : IEquatable<SignalGradient>
{
    public const int MinStops = 2;

    public const int MaxStops = 8;

    private readonly double[] _positions;
    private readonly byte[] _red;
    private readonly byte[] _green;
    private readonly byte[] _blue;

    private SignalGradient(ImmutableArray<SignalGradientStop> stops)
    {
        Stops = stops;
        _positions = new double[stops.Length];
        _red = new byte[stops.Length];
        _green = new byte[stops.Length];
        _blue = new byte[stops.Length];
        for (var i = 0; i < stops.Length; i++)
        {
            _positions[i] = stops[i].Position;
            (_red[i], _green[i], _blue[i]) = ParseColour(stops[i].Colour);
        }
    }

    /// <summary>The R4-C meter colours: cyan through teal and violet to purple.</summary>
    public static SignalGradient Default { get; } = Create(
    [
        new("#34C6F4", 0),
        new("#34C6F4", 0.40),
        new("#3FE0D8", 0.62),
        new("#7C6BFF", 0.78),
        new("#B65CFF", 0.86),
        new("#B65CFF", 1.0),
    ]);

    /// <summary>The stops, sorted by position, with colours as upper-case "#RRGGBB".</summary>
    public ImmutableArray<SignalGradientStop> Stops { get; }

    /// <summary>Builds a gradient: positions are clamped to [0, 1] and sorted (stably, so equal positions keep their order).</summary>
    /// <exception cref="ArgumentException">Fewer than two or more than eight stops, a colour that is not "#RRGGBB", or a position that is not a number.</exception>
    public static SignalGradient Create(IEnumerable<SignalGradientStop> stops) =>
        TryCreate(stops, out var gradient, out var error) ? gradient : throw new ArgumentException(error, nameof(stops));

    /// <summary>Like <see cref="Create"/>, but reports an invalid set of stops by returning false.</summary>
    public static bool TryCreate(IEnumerable<SignalGradientStop>? stops, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SignalGradient? gradient) =>
        TryCreate(stops, out gradient, out _);

    private static bool TryCreate(IEnumerable<SignalGradientStop>? stops, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SignalGradient? gradient, out string error)
    {
        gradient = null;
        if (stops is null)
        {
            error = "A gradient needs stops.";
            return false;
        }

        var normalized = new List<SignalGradientStop>();
        foreach (var stop in stops)
        {
            if (stop is null || stop.Colour is null || !IsColour(stop.Colour))
            {
                error = $"A gradient stop's colour must be \"#RRGGBB\" (got \"{stop?.Colour}\").";
                return false;
            }

            if (double.IsNaN(stop.Position))
            {
                error = "A gradient stop's position must be a number.";
                return false;
            }

            normalized.Add(new SignalGradientStop(stop.Colour.ToUpperInvariant(), Math.Clamp(stop.Position, 0, 1)));
        }

        if (normalized.Count is < MinStops or > MaxStops)
        {
            error = $"A gradient has {MinStops} to {MaxStops} stops (got {normalized.Count}).";
            return false;
        }

        gradient = new SignalGradient([.. normalized.OrderBy(s => s.Position)]);
        error = string.Empty;
        return true;
    }

    /// <summary>Whether <paramref name="colour"/> is "#RRGGBB" (either case).</summary>
    public static bool IsColour(string? colour)
    {
        if (colour is not { Length: 7 } || colour[0] != '#')
        {
            return false;
        }

        for (var i = 1; i < 7; i++)
        {
            if (!char.IsAsciiHexDigit(colour[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The colour at <paramref name="fraction"/> of full scale (clamped to [0, 1]), interpolated linearly in sRGB between
    /// the stops either side. At a hard edge the upper stop's colour wins. Allocation-free: callable per segment.
    /// </summary>
    public (byte R, byte G, byte B) Evaluate(double fraction)
    {
        var f = double.IsNaN(fraction) ? 0 : Math.Clamp(fraction, 0, 1);
        var last = _positions.Length - 1;
        if (f < _positions[0])
        {
            return (_red[0], _green[0], _blue[0]);
        }

        for (var i = 0; i < last; i++)
        {
            var upper = _positions[i + 1];
            if (f < upper)
            {
                // f >= _positions[i] here, so the span is positive.
                var t = (f - _positions[i]) / (upper - _positions[i]);
                return (Lerp(_red[i], _red[i + 1], t), Lerp(_green[i], _green[i + 1], t), Lerp(_blue[i], _blue[i + 1], t));
            }
        }

        return (_red[last], _green[last], _blue[last]);
    }

    public bool Equals(SignalGradient? other) =>
        other is not null && (ReferenceEquals(this, other) || Stops.SequenceEqual(other.Stops));

    public override bool Equals(object? obj) => Equals(obj as SignalGradient);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var stop in Stops)
        {
            hash.Add(stop);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(SignalGradient? left, SignalGradient? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(SignalGradient? left, SignalGradient? right) => !(left == right);

    private static byte Lerp(byte from, byte to, double t) => (byte)Math.Round(from + ((to - from) * t), MidpointRounding.AwayFromZero);

    private static (byte R, byte G, byte B) ParseColour(string colour) =>
        (byte.Parse(colour.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
         byte.Parse(colour.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
         byte.Parse(colour.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}

/// <summary>
/// Writes a <see cref="SignalGradient"/> as an array of <c>{ "colour", "position" }</c>. Reads anything that is not a valid
/// gradient as null, which <see cref="DisplaySettingsStore.Load"/> replaces with <see cref="SignalGradient.Default"/>.
/// </summary>
internal sealed class SignalGradientJsonConverter : JsonConverter<SignalGradient>
{
    public override SignalGradient? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var stops = new List<SignalGradientStop>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("colour", out var colour) || colour.ValueKind != JsonValueKind.String
                || !element.TryGetProperty("position", out var position) || position.ValueKind != JsonValueKind.Number || !position.TryGetDouble(out var value))
            {
                return null;
            }

            stops.Add(new SignalGradientStop(colour.GetString()!, value));
        }

        return SignalGradient.TryCreate(stops, out var gradient) ? gradient : null;
    }

    public override void Write(Utf8JsonWriter writer, SignalGradient value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var stop in value.Stops)
        {
            writer.WriteStartObject();
            writer.WriteString("colour", stop.Colour);
            writer.WriteNumber("position", stop.Position);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
