using System.Collections.Immutable;
using System.Globalization;
using Bluestone.Domain.Midi;

namespace Bluestone.Profiles;

/// <summary>
/// A SysEx message with named 7-bit placeholders, written in profiles as space-separated tokens such
/// as <c>F0 43 {device} 4C 00 00 7E 00 F7</c>. Validated on load: it starts with <c>F0</c>, ends with
/// <c>F7</c>, every literal between is a data byte, and every placeholder is a declared parameter.
/// </summary>
public sealed record SysExTemplate
{
    public const int MaxTokens = 512;

    internal SysExTemplate(string id, string name, SysExEffect effect, string? description, ImmutableArray<TemplateToken> tokens, ImmutableArray<TemplateParameter> parameters)
    {
        Id = id;
        Name = name;
        Effect = effect;
        Description = description;
        Tokens = tokens;
        Parameters = parameters;
    }

    public string Id { get; }

    public string Name { get; }

    public SysExEffect Effect { get; }

    public string? Description { get; }

    public ImmutableArray<TemplateParameter> Parameters { get; }

    /// <summary>True for resets and bulk transfers, which the UI must confirm before sending.</summary>
    public bool RequiresConfirmation => Effect != SysExEffect.Parameter;

    internal ImmutableArray<TemplateToken> Tokens { get; }

    /// <summary>The template as written, for display.</summary>
    public string Pattern => string.Join(' ', Tokens.Select(t => t.Parameter is { } p ? $"{{{p}}}" : t.Literal.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>Renders the message. Unspecified parameters take their defaults.</summary>
    /// <exception cref="ArgumentException">A value is unknown or outside its declared range.</exception>
    public SysExMessage Render(IReadOnlyDictionary<string, int>? values = null)
    {
        values ??= ImmutableDictionary<string, int>.Empty;
        foreach (var name in values.Keys)
        {
            if (!Parameters.Any(p => p.Name == name))
            {
                throw new ArgumentException($"Template '{Id}' has no parameter '{name}'.", nameof(values));
            }
        }

        var bytes = new byte[Tokens.Length];
        for (var i = 0; i < Tokens.Length; i++)
        {
            var token = Tokens[i];
            if (token.Parameter is not { } name)
            {
                bytes[i] = token.Literal;
                continue;
            }

            var parameter = Parameters.First(p => p.Name == name);
            var value = values.TryGetValue(name, out var v) ? v : parameter.Default;
            if (value < parameter.Min || value > parameter.Max)
            {
                throw new ArgumentException($"Parameter '{name}' must be {parameter.Min}-{parameter.Max}; got {value}.", nameof(values));
            }

            bytes[i] = (byte)value;
        }

        return SysExMessage.Create(bytes);
    }
}

/// <summary>A literal byte, or a named placeholder for a 7-bit value.</summary>
internal readonly record struct TemplateToken(byte Literal, string? Parameter);
