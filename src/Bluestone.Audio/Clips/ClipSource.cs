using Bluestone.Audio.Files;

namespace Bluestone.Audio.Clips;

/// <summary>
/// Where one clip sounds: the song frames it covers at the engine's rate, and the file and source position that
/// plays at its first frame. The source plays at its own rate from there (no time stretching).
/// </summary>
/// <param name="Path">The audio file.</param>
/// <param name="SongStartFrame">First song frame of the clip, at the engine's rate.</param>
/// <param name="SongEndFrame">Song frame after the clip's last.</param>
/// <param name="SourceSecondsAtStart">Position in the file, in seconds, heard at <paramref name="SongStartFrame"/>.</param>
public sealed record AudioClipRegion(string Path, long SongStartFrame, long SongEndFrame, double SourceSecondsAtStart)
{
    /// <summary>The clip's gain and fades. Changing only these does not reopen the file (see <see cref="ClipPlayerNode.UpdateEnvelopes"/>).</summary>
    public AudioClipEnvelope Envelope { get; init; } = AudioClipEnvelope.Unity;

    /// <summary>Whether <paramref name="other"/> plays the same file the same way, whatever its envelope.</summary>
    public bool SameStream(AudioClipRegion other) => other is not null && this with { Envelope = other.Envelope } == other;
}

/// <summary>
/// A clip's gain and fades, in frames at the engine's rate from the clip's first frame and before its end. A frame
/// <c>p</c> frames into a fade of <c>n</c> frames plays at <c>curve(p / n)</c>: the first frame of a fade-in and the
/// last frame of a fade-out are silent. The curve is linear (<c>x</c>) or equal power (<c>sin(x·π/2)</c>).
/// </summary>
/// <param name="Gain">Linear gain (1 is unity).</param>
/// <param name="FadeInFrames">Frames from the clip's start.</param>
/// <param name="FadeOutFrames">Frames before the clip's end.</param>
/// <param name="EqualPower">Quarter-sine curves instead of straight lines.</param>
public sealed record AudioClipEnvelope(float Gain, long FadeInFrames, long FadeOutFrames, bool EqualPower)
{
    public static AudioClipEnvelope Unity { get; } = new(1f, 0, 0, false);

    public bool IsUnity => Gain == 1f && FadeInFrames <= 0 && FadeOutFrames <= 0;

    /// <summary>The gain of the frame <paramref name="frame"/> frames into a clip of <paramref name="clipFrames"/>.</summary>
    public float GainAt(long frame, long clipFrames)
    {
        double gain = Gain;
        if (frame < FadeInFrames)
        {
            gain *= Curve((double)frame / FadeInFrames);
        }

        var beforeEnd = clipFrames - 1 - frame;
        if (beforeEnd < FadeOutFrames)
        {
            gain *= Curve((double)beforeEnd / FadeOutFrames);
        }

        return (float)gain;
    }

    private double Curve(double x) => EqualPower ? Math.Sin(x * Math.PI / 2) : x;
}

/// <summary>
/// One clip's audio, ready for the audio thread: the first part of the region decoded in memory (so starting at the
/// clip's beginning never waits for the disk), the same at the loop start when the loop starts inside the clip (so a
/// wrap never waits either), and a ring the disk thread keeps filled ahead of the play position. On a jump or wrap the
/// ring is asked for the first frame a preload does not hold. Output frames are computed from their song position
/// alone (see <see cref="ResamplingKernel"/>), so a jump needs no history. Data that has not arrived in time is played
/// as silence and counted, never as other audio.
/// </summary>
internal sealed class ClipSource : IDisposable
{
    private const int ReadChunkFrames = 4096;
    private const long NoLoop = long.MinValue;
    private readonly AudioFileReader _reader;
    private readonly ResamplingKernel _kernel;
    private readonly bool _direct;
    private readonly int _halo;
    private readonly double _sourceStart;
    private readonly long _preloadFrames;
    private readonly Preload _start;
    private readonly SourceRing _ring;
    private readonly float[] _scratchLeft;
    private readonly float[] _scratchRight;
    private readonly float[] _readInterleaved;
    private readonly float[] _readLeft;
    private readonly float[] _readRight;
    private readonly float[] _outLeft;
    private readonly float[] _outRight;
    private AudioClipEnvelope _envelope;
    private long _underrunFrames;

    // Audio thread: the song frame after the last one rendered (a different next frame is a jump or wrap).
    private long _nextSongFrame = long.MinValue;

    // The loop start to preload, as a song frame (written by the audio thread), and its preload (written by the disk
    // thread, replaced whole). _loopLoadedFor is the disk thread's own record of what it last loaded.
    private long _loopWanted = NoLoop;
    private long _loopLoadedFor = NoLoop;
    private Preload? _loop;

    private ClipSource(AudioClipRegion region, AudioFileReader reader, int outputRate, int blockFrames, TimeSpan preload, TimeSpan ringLength)
    {
        Region = region;
        _reader = reader;
        var format = reader.Format;
        SourceFrames = format.Frames;
        _kernel = ResamplingKernel.For(format.SampleRate, outputRate);
        var start = region.SourceSecondsAtStart * format.SampleRate;

        // A start within a millionth of a frame of a whole frame is that frame (seconds do not hold frames exactly).
        _sourceStart = Math.Abs(start - Math.Round(start)) < 1e-6 ? Math.Round(start) : start;
        _direct = format.SampleRate == outputRate && _sourceStart == Math.Floor(_sourceStart);
        _halo = _direct ? 0 : _kernel.HalfTaps;
        var scratch = (int)Math.Ceiling(blockFrames * _kernel.Ratio) + (2 * _halo) + 2;
        _scratchLeft = new float[scratch];
        _scratchRight = new float[scratch];
        _readInterleaved = new float[ReadChunkFrames * format.Channels];
        _readLeft = new float[ReadChunkFrames];
        _readRight = new float[ReadChunkFrames];
        _outLeft = new float[blockFrames];
        _outRight = new float[blockFrames];
        _envelope = region.Envelope;

        _preloadFrames = (long)(preload.TotalSeconds * format.SampleRate);
        _start = Load(Math.Clamp((long)Math.Floor(_sourceStart) - _halo, 0, SourceFrames));
        _ring = new SourceRing(Math.Max(scratch * 4, (int)(ringLength.TotalSeconds * format.SampleRate)));
        _ring.Request(_start.End);
    }

    public AudioClipRegion Region { get; }

    /// <summary>Gain and fades; replaced whole by the control thread, read once per range by the audio thread.</summary>
    public AudioClipEnvelope Envelope
    {
        get => Volatile.Read(ref _envelope);
        set => Volatile.Write(ref _envelope, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public long SourceFrames { get; }

    /// <summary>Output frames played as silence because the disk had not delivered them yet.</summary>
    public long UnderrunFrames => Interlocked.Read(ref _underrunFrames);

    /// <summary>For tests: at most this many frames per <see cref="Fill"/> (a slow disk).</summary>
    internal int MaxFramesPerFill { get; set; } = int.MaxValue;

    /// <summary>Opens and preloads. Control thread.</summary>
    /// <exception cref="FileNotFoundException">The file is missing.</exception>
    /// <exception cref="AudioFileException">The file is not a supported audio file.</exception>
    public static ClipSource Open(AudioClipRegion region, int outputRate, int blockFrames, TimeSpan preload, TimeSpan ringLength)
    {
        var reader = AudioFileReader.Open(region.Path);
        try
        {
            return new ClipSource(region, reader, outputRate, blockFrames, preload, ringLength);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>Adds this clip's part of the block to <paramref name="output"/>, on both sides of a loop wrap. Audio thread.</summary>
    public void Render(in AudioRenderContext context, StereoBuffer output)
    {
        var loop = context.LoopEnd > context.LoopStart && context.LoopStart > Region.SongStartFrame && context.LoopStart < Region.SongEndFrame ? context.LoopStart : NoLoop;
        if (loop != _loopWanted)
        {
            Volatile.Write(ref _loopWanted, loop);
        }

        RenderRange(context.SongFrame, context.FirstLength, 0, output);
        if (context.Wraps)
        {
            RenderRange(context.WrapFrame, context.Frames - context.WrapOffset, context.WrapOffset, output);
        }
    }

    // Song frames [songFrame, songFrame + frames) into output frames from outputOffset.
    private void RenderRange(long songFrame, int frames, int outputOffset, StereoBuffer output)
    {
        if (songFrame != _nextSongFrame)
        {
            Reposition(songFrame);
        }

        _nextSongFrame = songFrame + frames;
        var blockStart = songFrame - outputOffset;
        var from = Math.Max(songFrame, Region.SongStartFrame);
        var to = Math.Min(songFrame + frames, Region.SongEndFrame);
        if (from >= to)
        {
            return;
        }

        var ratio = _kernel.Ratio;
        var x0 = _sourceStart + ((from - Region.SongStartFrame) * ratio);
        var xLast = x0 + ((to - from - 1) * ratio);
        var halo = _halo;
        var first = FirstSourceFrame(x0);
        var end = (long)Math.Floor(xLast) + halo + 1;
        var count = (int)(end - first);
        var left = _scratchLeft.AsSpan(0, count);
        var right = _scratchRight.AsSpan(0, count);
        if (!Fetch(first, left, right))
        {
            Interlocked.Add(ref _underrunFrames, to - from);
            return;
        }

        var offset = (int)(from - blockStart);
        var length = (int)(to - from);
        var envelope = Envelope;
        var outLeft = envelope.IsUnity ? output.Left.Slice(offset, length) : _outLeft.AsSpan(0, length);
        var outRight = envelope.IsUnity ? output.Right.Slice(offset, length) : _outRight.AsSpan(0, length);
        if (!envelope.IsUnity)
        {
            outLeft.Clear();
            outRight.Clear();
        }

        if (_direct)
        {
            Mix.Add(left[..outLeft.Length], outLeft);
            Mix.Add(right[..outRight.Length], outRight);
        }
        else
        {
            for (var i = 0; i < outLeft.Length; i++)
            {
                var x = x0 + (i * ratio);
                outLeft[i] += _kernel.Interpolate(left, first, x);
                outRight[i] += _kernel.Interpolate(right, first, x);
            }
        }

        if (!envelope.IsUnity)
        {
            ApplyEnvelope(envelope, from - Region.SongStartFrame, outLeft, outRight);
            Mix.Add(outLeft, output.Left.Slice(offset, length));
            Mix.Add(outRight, output.Right.Slice(offset, length));
        }

        // The next block needs nothing before the next frame's window.
        var next = x0 + (length * ratio);
        _ring.Consume((long)Math.Floor(next) - halo);
    }

    // After a jump or wrap to a song frame: the ring is asked for the first source frame no preload holds from there,
    // unless it already holds (or is reading) it.
    private void Reposition(long songFrame)
    {
        var at = Math.Max(songFrame, Region.SongStartFrame);
        if (at >= Region.SongEndFrame)
        {
            return;
        }

        var first = Math.Max(0, FirstSourceFrame(_sourceStart + ((at - Region.SongStartFrame) * _kernel.Ratio)));
        var target = first;
        if (_start.Holds(first))
        {
            target = _start.End;
        }
        else if (Volatile.Read(ref _loop) is { } loop && loop.Holds(first))
        {
            target = loop.End;
        }

        var read = _ring.ReadFrame;
        var available = _ring.AvailableEnd;
        if (target >= read && target <= (available ?? read))
        {
            _ring.Consume(target);
        }
        else
        {
            _ring.Request(target);
        }
    }

    // The first source frame the window of the output frame at source position x needs.
    private long FirstSourceFrame(double x) => (long)Math.Floor(x) - _halo + (_direct ? 0 : 1);

    // Frames [first, first + left.Length) of the clip, scaled by the gain and the fades they fall in.
    private void ApplyEnvelope(AudioClipEnvelope envelope, long first, Span<float> left, Span<float> right)
    {
        var clipFrames = Region.SongEndFrame - Region.SongStartFrame;
        var fadeOutStart = clipFrames - envelope.FadeOutFrames;
        if (first >= envelope.FadeInFrames && first + left.Length <= fadeOutStart)
        {
            Mix.Scale(left, envelope.Gain);
            Mix.Scale(right, envelope.Gain);
            return;
        }

        for (var i = 0; i < left.Length; i++)
        {
            var gain = envelope.GainAt(first + i, clipFrames);
            left[i] *= gain;
            right[i] *= gain;
        }
    }

    /// <summary>Loads the loop start's preload when the loop moved, and reads ahead into the ring. Disk thread.</summary>
    public void Fill()
    {
        var loop = Volatile.Read(ref _loopWanted);
        if (loop != _loopLoadedFor)
        {
            _loopLoadedFor = loop;
            Volatile.Write(ref _loop, LoadLoop(loop));
        }

        var budget = MaxFramesPerFill;
        while (budget > 0)
        {
            var (frame, space) = _ring.BeginWrite();
            var count = (int)Math.Min(Math.Min(space, ReadChunkFrames), Math.Min(budget, SourceFrames - frame));
            if (count <= 0)
            {
                return;
            }

            ReadStereo(frame, _readLeft.AsSpan(0, count), _readRight.AsSpan(0, count));
            if (!_ring.EndWrite(_readLeft.AsSpan(0, count), _readRight.AsSpan(0, count)))
            {
                continue;
            }

            budget -= count;
        }
    }

    public void Dispose() => _reader.Dispose();

    // The preload for a loop starting at a song frame, or null when there is no loop or the clip's own preload holds it.
    private Preload? LoadLoop(long songFrame)
    {
        if (songFrame == NoLoop)
        {
            return null;
        }

        var first = Math.Clamp(FirstSourceFrame(_sourceStart + ((songFrame - Region.SongStartFrame) * _kernel.Ratio)), 0, SourceFrames);
        return _start.Holds(first) ? null : Load(first);
    }

    // Decodes up to the preload length from a source frame.
    private Preload Load(long start)
    {
        var frames = (int)(Math.Min(SourceFrames, start + _preloadFrames) - start);
        var preload = new Preload(start, new float[frames], new float[frames]);
        ReadStereo(start, preload.Left, preload.Right);
        return preload;
    }

    // Copies source frames [first, first + count) into left/right: zeros outside the file, the preload where it has
    // them, the ring for the rest. False when the ring cannot supply its part yet (it is then asked to).
    private bool Fetch(long first, Span<float> left, Span<float> right)
    {
        var count = left.Length;
        left.Clear();
        right.Clear();
        var lo = Math.Max(first, 0);
        var hi = Math.Min(first + count, SourceFrames);
        if (lo >= hi)
        {
            return true;
        }

        lo = _start.Copy(lo, hi, first, left, right);
        if (Volatile.Read(ref _loop) is { } loop)
        {
            lo = loop.Copy(lo, hi, first, left, right);
        }

        if (lo >= hi)
        {
            return true;
        }

        // A position behind what the ring holds, or beyond what it has read so far, needs a new request; the data then
        // comes later. (Asking anew rather than waiting for the reader to get there keeps a late disk from falling further behind.)
        var read = _ring.ReadFrame;
        var available = _ring.AvailableEnd ?? read;
        if (lo < read || lo > available + left.Length)
        {
            _ring.Request(lo);
            return false;
        }

        _ring.Consume(lo);
        return _ring.TryRead(lo, left.Slice((int)(lo - first), (int)(hi - lo)), right.Slice((int)(lo - first), (int)(hi - lo)));
    }

    // Reads frames as stereo: mono to both sides, the first two channels of anything wider.
    private void ReadStereo(long frame, Span<float> left, Span<float> right)
    {
        var channels = _reader.Format.Channels;
        var done = 0;
        while (done < left.Length)
        {
            var want = Math.Min(ReadChunkFrames, left.Length - done);
            var got = _reader.Read(frame + done, want, _readInterleaved);
            if (got == 0)
            {
                left[done..].Clear();
                right[done..].Clear();
                return;
            }

            for (var i = 0; i < got; i++)
            {
                var l = _readInterleaved[i * channels];
                left[done + i] = l;
                right[done + i] = channels == 1 ? l : _readInterleaved[(i * channels) + 1];
            }

            done += got;
        }
    }

    /// <summary>Decoded source frames [Start, End), immutable once published.</summary>
    private sealed class Preload(long start, float[] left, float[] right)
    {
        public long Start { get; } = start;

        public long End => Start + Left.Length;

        public float[] Left { get; } = left;

        public float[] Right { get; } = right;

        public bool Holds(long frame) => frame >= Start && frame < End;

        // Copies what it holds of source frames [lo, hi) into left/right (indexed from `first`); returns where it stopped.
        public long Copy(long lo, long hi, long first, Span<float> left, Span<float> right)
        {
            if (!Holds(lo))
            {
                return lo;
            }

            var n = (int)(Math.Min(hi, End) - lo);
            Left.AsSpan((int)(lo - Start), n).CopyTo(left[(int)(lo - first)..]);
            Right.AsSpan((int)(lo - Start), n).CopyTo(right[(int)(lo - first)..]);
            return lo + n;
        }
    }
}
