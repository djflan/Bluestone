using System.Numerics;

namespace Bluestone.Audio;

/// <summary>
/// Two channels of planar audio for one block: <see cref="Left"/> and <see cref="Right"/> have the same length (the
/// block's frame count). The engine's internal format is stereo float throughout.
/// </summary>
public readonly ref struct StereoBuffer
{
    public StereoBuffer(Span<float> left, Span<float> right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Both channels must have the same length.", nameof(right));
        }

        Left = left;
        Right = right;
    }

    public Span<float> Left { get; }

    public Span<float> Right { get; }

    public int Frames => Left.Length;

    /// <summary>A block of <paramref name="frames"/> from a buffer laid out as [left: stride][right: stride].</summary>
    public static StereoBuffer Of(float[] storage, int stride, int frames) =>
        new(storage.AsSpan(0, frames), storage.AsSpan(stride, frames));

    public void Clear()
    {
        Left.Clear();
        Right.Clear();
    }

    public void CopyFrom(StereoBuffer source)
    {
        source.Left.CopyTo(Left);
        source.Right.CopyTo(Right);
    }

    public void Add(StereoBuffer source)
    {
        Mix.Add(source.Left, Left);
        Mix.Add(source.Right, Right);
    }
}

/// <summary>Small vectorized loops used by every node.</summary>
internal static class Mix
{
    public static void Add(ReadOnlySpan<float> source, Span<float> destination)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= source.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                (new Vector<float>(source[i..]) + new Vector<float>(destination[i..])).CopyTo(destination[i..]);
            }
        }

        for (; i < source.Length; i++)
        {
            destination[i] += source[i];
        }
    }

    public static void Scale(Span<float> samples, float gain)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= samples.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                (new Vector<float>(samples[i..]) * gain).CopyTo(samples[i..]);
            }
        }

        for (; i < samples.Length; i++)
        {
            samples[i] *= gain;
        }
    }

    /// <summary>Adds <paramref name="source"/> times <paramref name="gain"/> to <paramref name="destination"/>.</summary>
    public static void AddScaled(ReadOnlySpan<float> source, Span<float> destination, float gain)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= source.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                ((new Vector<float>(source[i..]) * gain) + new Vector<float>(destination[i..])).CopyTo(destination[i..]);
            }
        }

        for (; i < source.Length; i++)
        {
            destination[i] += source[i] * gain;
        }
    }

    /// <summary>
    /// Adds <paramref name="source"/> to <paramref name="destination"/> at a gain that moves linearly from
    /// <paramref name="from"/> to <paramref name="to"/> across the block, as <see cref="Ramp"/> does.
    /// </summary>
    public static void AddRamped(ReadOnlySpan<float> source, Span<float> destination, float from, float to)
    {
        if (from == to)
        {
            if (to != 0f)
            {
                AddScaled(source, destination, to);
            }

            return;
        }

        var step = (to - from) / source.Length;
        for (var i = 0; i < source.Length; i++)
        {
            destination[i] += source[i] * (from + (step * (i + 1)));
        }
    }

    /// <summary>Multiplies by a gain that moves linearly from <paramref name="from"/> to <paramref name="to"/> across the block.</summary>
    public static void Ramp(Span<float> samples, float from, float to)
    {
        if (from == to)
        {
            if (to != 1f)
            {
                Scale(samples, to);
            }

            return;
        }

        var step = (to - from) / samples.Length;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] *= from + (step * (i + 1));
        }
    }
}
