using Bluestone.Domain.Time;

namespace Bluestone.Domain.Sequencing;

/// <summary>
/// Where an audio clip's sound comes from: a file, and what is known about it. Bluestone stores the
/// reference, not the sound, so a project stays small and a moved file can be found again.
/// </summary>
public sealed record AudioSource
{
    public const int MaxLocationLength = 4096;

    /// <param name="location">A path, relative to the project file when it can be, or an absolute path.</param>
    /// <param name="sampleRate">Samples per second of the source.</param>
    /// <param name="frames">Length of the source in sample frames.</param>
    /// <param name="channels">Channel count of the source.</param>
    public AudioSource(string location, int sampleRate, long frames, int channels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        Location = location.Length <= MaxLocationLength ? location : throw new ArgumentException($"Locations are at most {MaxLocationLength} characters.", nameof(location));
        SampleRate = sampleRate is >= 1000 and <= 768_000 ? sampleRate : throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rates are 1000 to 768000 Hz.");
        Frames = frames >= 0 ? frames : throw new ArgumentOutOfRangeException(nameof(frames), frames, "A source cannot have negative length.");
        Channels = channels is >= 1 and <= 64 ? channels : throw new ArgumentOutOfRangeException(nameof(channels), channels, "Sources have 1 to 64 channels.");
    }

    public string Location { get; }

    public int SampleRate { get; }

    public long Frames { get; }

    public int Channels { get; }
}

/// <summary>The curve of an audio clip's fades.</summary>
public enum FadeShape
{
    /// <summary>Gain moves in a straight line: half way through a fade the gain is 0.5 (−6 dB).</summary>
    Linear,

    /// <summary>
    /// Gain follows a quarter sine (a fade-in is sin(x·π/2), a fade-out cos(x·π/2)): half way the gain is 0.707
    /// (−3 dB), so a fade-out overlapping a fade-in of uncorrelated material keeps its power.
    /// </summary>
    EqualPower,
}

/// <summary>
/// A clip of recorded or imported audio. Content tick 0 is where the source starts; trimming only moves
/// the window onto it, like a note clip. The audio engine plays the source from disk at its own rate from the
/// clip's origin, at the clip's gain, with its fades (ADR 0032).
/// </summary>
/// <remarks>
/// Fades are measured from the clip's edges in ticks, so they follow the tempo like the clip does. Stored fades
/// may add up to more than the clip (after a trim); <see cref="EffectiveFades"/> are what plays. Fades belong to the
/// clip's outer edges: a loop repeats the audio, not the fades.
/// </remarks>
public sealed record AudioClip : Clip
{
    public const double MinGainDecibels = -120;
    public const double MaxGainDecibels = 24;

    private readonly double _gain;

    public AudioClip(ClipId id, Tick start, TickSpan length, TickSpan contentOffset, AudioSource source, string name = "")
        : base(id, start, length, contentOffset, name) => Source = source ?? throw new ArgumentNullException(nameof(source));

    public AudioSource Source { get; init; }

    /// <summary>Gain applied to the clip's audio, -120 (silence) to 24 dB.</summary>
    public double GainDecibels
    {
        get => _gain;
        init => _gain = value is >= MinGainDecibels and <= MaxGainDecibels
            ? value
            : throw new ArgumentOutOfRangeException(nameof(GainDecibels), value, "Clip gain must be between -120 and 24 dB.");
    }

    /// <summary>The fade-in's length from the clip's start; zero for none.</summary>
    public TickSpan FadeIn { get; init; }

    /// <summary>The fade-out's length before the clip's end; zero for none.</summary>
    public TickSpan FadeOut { get; init; }

    public FadeShape FadeShape
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(FadeShape), value, "Unknown fade shape.");
    }

    /// <summary>The fades as played: the fade-in at most the clip's length, the fade-out at most what remains.</summary>
    public (TickSpan In, TickSpan Out) EffectiveFades
    {
        get
        {
            var fadeIn = Math.Min(FadeIn.Value, Length.Value);
            return (new TickSpan(fadeIn), new TickSpan(Math.Min(FadeOut.Value, Length.Value - fadeIn)));
        }
    }

    /// <remarks>Audio cannot start before its source does, so the clip's left edge stops at the source's start. Fades keep their lengths, within the new clip.</remarks>
    public override AudioClip WithBounds(Tick start, Tick until)
    {
        EnsureBounds(start, until);
        var edge = Tick.Max(start, new Tick(Math.Max(0, Origin)));
        if (until <= edge)
        {
            throw new ArgumentException("A clip must end after it starts.", nameof(until));
        }

        return (this with { Start = edge, Length = until - edge, ContentOffset = new TickSpan(edge.Value - Origin) }).WithFadesInside();
    }

    /// <remarks>
    /// The left part keeps the fade-in, the right part the fade-out; no fade is added at the cut. A looping clip's right
    /// part starts at the audio heard at <paramref name="at"/>, with the same loop.
    /// </remarks>
    public override (Clip Left, Clip Right) SplitAt(Tick at, ClipId rightId)
    {
        EnsureInside(at);
        var offset = IsLooping ? PhaseOffsetAt(at.Value - Start.Value) : new TickSpan(at.Value - Origin);
        var left = (this with { Length = at - Start, FadeOut = TickSpan.Zero }).WithFadesInside();
        var right = (this with { Id = rightId, Start = at, Length = End - at, ContentOffset = offset, FadeIn = TickSpan.Zero }).WithFadesInside();
        return (left, right);
    }

    public override AudioClip CopyTo(Tick start) => this with { Id = ClipId.New(), Start = start };

    private AudioClip WithFadesInside()
    {
        var (fadeIn, fadeOut) = EffectiveFades;
        return fadeIn == FadeIn && fadeOut == FadeOut ? this : this with { FadeIn = fadeIn, FadeOut = fadeOut };
    }
}
