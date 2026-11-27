using System.ComponentModel;
using Avalonia.Media;

namespace Bluestone.Desktop.Theme;

/// <summary>
/// The transport's lit glyphs' glow (a drop shadow with no offset), at the musician's <see cref="Glow.Strength"/>: styles
/// bind their <c>Effect</c> to <see cref="Accent"/> and <see cref="Record"/>, which are null while the glow is off, so an
/// unlit setting draws no blur at all. Rebuilt by <see cref="Glow.Use"/>. UI thread only.
/// </summary>
public sealed class GlowEffects : INotifyPropertyChanged
{
    private const double BlurRadius = 8;
    private const double AccentOpacity = 0.55;
    private const double RecordOpacity = 0.6;

    private GlowEffects() => Refresh();

    public static GlowEffects Current { get; } = new();

    /// <summary>The glow of a lit transport glyph in the accent.</summary>
    public IEffect? Accent { get; private set; }

    /// <summary>The glow of the lit record glyph.</summary>
    public IEffect? Record { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh()
    {
        Accent = Create(Palette.Accent, AccentOpacity);
        Record = Create(Palette.Record, RecordOpacity);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Accent)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Record)));
    }

    private static ImmutableDropShadowEffect? Create(Color color, double opacity)
    {
        if (!Glow.IsOn)
        {
            return null;
        }

        var s = Glow.Strength;
        return new ImmutableDropShadowEffect(0, 0, BlurRadius * (0.6 + (0.4 * s)), color, Math.Min(1, opacity * s));
    }
}
