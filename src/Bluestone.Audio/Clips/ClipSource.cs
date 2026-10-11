using Bluestone.Audio.Files;

namespace Bluestone.Audio.Clips;

/// <summary>
/// Where one clip sounds: the song frames it covers at the engine's rate, and the file and source position that
/// plays at its first frame. The source plays at its own rate from there (no time stretching).
/// </summary>
/// <param name="Path">The audio file.</param>
/// <param name="SongStartFrame">First song frame of the clip, at the engine's rate.</param>
/// <param name="SongEndFrame">Song frame after the clip's last.</param>
/// <param name="SourceFrameAtStart">Source frame (at the file's rate, may be fractional) heard at <paramref name="SongStartFrame"/>.</param>
public sealed record AudioClipRegion(string Path, long SongStartFrame, long SongEndFrame, double SourceFrameAtStart);

/// <summary>
/// One clip's audio, ready for the audio thread: the first part of the region decoded in memory (so starting at the
/// clip's beginning never waits for the disk), and a ring the disk thread keeps filled ahead of the play position.
/// Output frames are computed from their song position alone (see <see cref="ResamplingKernel"/>), so a jump needs no
/// history. Data that has not arrived in time is played as silence and counted, never as other audio.
/// </summary>
internal sealed class ClipSource : IDisposable
{
    private const int ReadChunkFrames = 4096;
    private readonly AudioFileReader _reader;
    private readonly ResamplingKernel _kernel;
    private readonly bool _direct;
    private readonly float[] _preloadLeft;
    private readonly float[] _preloadRight;
    private readonly long _preloadStart;
    private readonly long _preloadEnd;
    private readonly SourceRing _ring;
    private readonly float[] _scratchLeft;
    private readonly float[] _scratchRight;
    private readonly float[] _readInterleaved;
    private readonly float[] _readLeft;
    private readonly float[] _readRight;
    private long _underrunFrames;

    private ClipSource(AudioClipRegion region, AudioFileReader reader, int outputRate, int blockFrames, TimeSpan preload, TimeSpan ringLength)
    {
        Region = region;
        _reader = reader;
        var format = reader.Format;
        SourceFrames = format.Frames;
        _kernel = ResamplingKernel.For(format.SampleRate, outputRate);
        _direct = format.SampleRate == outputRate && region.SourceFrameAtStart == Math.Floor(region.SourceFrameAtStart);
        var halo = _direct ? 0 : _kernel.HalfTaps;
        var scratch = (int)Math.Ceiling(blockFrames * _kernel.Ratio) + (2 * halo) + 2;
        _scratchLeft = new float[scratch];
        _scratchRight = new float[scratch];
        _readInterleaved = new float[ReadChunkFrames * format.Channels];
        _readLeft = new float[ReadChunkFrames];
        _readRight = new float[ReadChunkFrames];

        _preloadStart = Math.Clamp((long)Math.Floor(region.SourceFrameAtStart) - halo, 0, SourceFrames);
        _preloadEnd = Math.Min(SourceFrames, _preloadStart + (long)(preload.TotalSeconds * format.SampleRate));
        var preloadFrames = (int)(_preloadEnd - _preloadStart);
        _preloadLeft = new float[preloadFrames];
        _preloadRight = new float[preloadFrames];
        ReadStereo(_preloadStart, _preloadLeft, _preloadRight);

        _ring = new SourceRing(Math.Max(scratch * 4, (int)(ringLength.TotalSeconds * format.SampleRate)));
        _ring.Request(_preloadEnd);
    }

    public AudioClipRegion Region { get; }

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

    /// <summary>Adds this clip's part of the block to <paramref name="output"/>. Audio thread.</summary>
    public void Render(in AudioRenderContext context, StereoBuffer output)
    {
        var blockStart = context.SongFrame;
        var from = Math.Max(blockStart, Region.SongStartFrame);
        var to = Math.Min(blockStart + context.Frames, Region.SongEndFrame);
        if (from >= to)
        {
            return;
        }

        var ratio = _kernel.Ratio;
        var x0 = Region.SourceFrameAtStart + ((from - Region.SongStartFrame) * ratio);
        var xLast = x0 + ((to - from - 1) * ratio);
        var halo = _direct ? 0 : _kernel.HalfTaps;
        var first = (long)Math.Floor(x0) - halo + (_direct ? 0 : 1);
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
        var outLeft = output.Left.Slice(offset, (int)(to - from));
        var outRight = output.Right.Slice(offset, (int)(to - from));
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

        // The next block needs nothing before the next frame's window.
        var next = x0 + (outLeft.Length * ratio);
        _ring.Consume(Math.Max(_preloadEnd, (long)Math.Floor(next) - halo));
    }

    /// <summary>Reads ahead into the ring. Disk thread.</summary>
    public void Fill()
    {
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

        var fromPreload = Math.Min(hi, _preloadEnd);
        if (lo < fromPreload && lo >= _preloadStart)
        {
            var n = (int)(fromPreload - lo);
            _preloadLeft.AsSpan((int)(lo - _preloadStart), n).CopyTo(left[(int)(lo - first)..]);
            _preloadRight.AsSpan((int)(lo - _preloadStart), n).CopyTo(right[(int)(lo - first)..]);
            lo = fromPreload;
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
}
