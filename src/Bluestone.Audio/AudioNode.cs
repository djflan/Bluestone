namespace Bluestone.Audio;

/// <summary>
/// What a node needs to know about the block it renders. Positions are in frames at the engine's sample rate on the
/// song timeline: frame 0 is the song's start, so frame f is song time f / <see cref="SampleRate"/>. Every block has
/// the engine's block size. When the loop end falls inside the block, the song position jumps there: frames
/// [0, <see cref="WrapOffset"/>) play from <see cref="SongFrame"/> and frames [WrapOffset, Frames) from
/// <see cref="WrapFrame"/> (the loop start).
/// </summary>
/// <param name="Frames">Frames in this block (the engine's block size).</param>
/// <param name="SampleRate">The engine's rate in Hz.</param>
/// <param name="Playing">Whether the transport is playing. When stopped, <paramref name="SongFrame"/> is the stopped position.</param>
/// <param name="SongFrame">Song position of the block's first frame.</param>
/// <param name="Jumped">
/// The song position at the block's start is not the previous block's end: playback started, stopped, sought, or
/// wrapped exactly at the block boundary. Nodes that follow the song must relocate; sounding notes must be released.
/// </param>
/// <param name="Tempo">Beats per minute at the block's start, for plugins that ask (informational).</param>
/// <param name="WrapOffset">Where in the block the loop wraps, or 0 when it does not.</param>
/// <param name="WrapFrame">The song frame playing from <paramref name="WrapOffset"/> on.</param>
/// <param name="HeardTimestamp">
/// When the master puts out the block's first frame at the device, in <see cref="System.Diagnostics.Stopwatch"/> ticks
/// (0 when unknown). Live input is measured against it.
/// </param>
public readonly record struct AudioRenderContext(int Frames, int SampleRate, bool Playing, long SongFrame, bool Jumped, double Tempo = 120, int WrapOffset = 0, long WrapFrame = 0, long HeardTimestamp = 0)
{
    /// <summary>The loop wraps inside this block.</summary>
    public bool Wraps => WrapOffset > 0 && WrapOffset < Frames;

    /// <summary>Frames played from <see cref="SongFrame"/>.</summary>
    public int FirstLength => Wraps ? WrapOffset : Frames;

    /// <summary>The song frame after the block's last frame.</summary>
    public long EndFrame => Wraps ? WrapFrame + (Frames - WrapOffset) : SongFrame + Frames;
}

/// <summary>
/// A source or processor in the render graph (a test tone, a track's audio clips, a plugin). Output is stereo.
/// <see cref="Render"/> runs on the audio thread: no blocking, allocation, I/O, logging, or throwing.
/// A node object lives across graph snapshots, so state such as phase or a read position survives edits.
/// </summary>
public abstract class AudioNode
{
    /// <summary>A name for diagnostics.</summary>
    public virtual string Name => GetType().Name;

    /// <summary>
    /// Frames the node's output lags its input or the song (plugin pipelining plus the plugin's own latency). The graph
    /// compensates for it when it is built (ADR 0035); a change shows in <see cref="AudioGraph.IsLatencyStale"/>.
    /// </summary>
    public virtual int LatencyFrames => 0;

    /// <summary>
    /// Renders one block into <paramref name="output"/> (which holds silence on entry). <paramref name="input"/> is
    /// the sum of the nodes connected into this one, or silence.
    /// </summary>
    public abstract void Render(in AudioRenderContext context, StereoBuffer input, StereoBuffer output);
}
