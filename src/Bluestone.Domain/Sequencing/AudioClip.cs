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

/// <summary>
/// A clip of recorded or imported audio. Content tick 0 is where the source starts; trimming only moves
/// the window onto it, like a note clip. The audio engine plays the source from disk at its own rate from the
/// clip's origin (ADR 0032).
/// </summary>
public sealed record AudioClip : Clip
{
    public AudioClip(ClipId id, Tick start, TickSpan length, TickSpan contentOffset, AudioSource source, string name = "")
        : base(id, start, length, contentOffset, name) => Source = source ?? throw new ArgumentNullException(nameof(source));

    public AudioSource Source { get; init; }

    /// <remarks>Audio cannot start before its source does, so the clip's left edge stops at the source's start.</remarks>
    public override AudioClip WithBounds(Tick start, Tick until)
    {
        EnsureBounds(start, until);
        var edge = Tick.Max(start, new Tick(Math.Max(0, Origin)));
        if (until <= edge)
        {
            throw new ArgumentException("A clip must end after it starts.", nameof(until));
        }

        return this with { Start = edge, Length = until - edge, ContentOffset = new TickSpan(edge.Value - Origin) };
    }

    public override (Clip Left, Clip Right) SplitAt(Tick at, ClipId rightId)
    {
        EnsureInside(at);
        var left = this with { Length = at - Start };
        var right = this with { Id = rightId, Start = at, Length = End - at, ContentOffset = new TickSpan(at.Value - Origin) };
        return (left, right);
    }

    public override AudioClip CopyTo(Tick start) => this with { Id = ClipId.New(), Start = start };
}
