namespace Bluestone.Audio;

/// <summary>
/// What a node needs to know about the block it renders. Positions are in frames at the engine's sample rate on the
/// song timeline: frame 0 is the song's start, so frame f is song time f / <see cref="SampleRate"/>.
/// </summary>
/// <param name="Frames">Frames in this block (at most the engine's block size).</param>
/// <param name="SampleRate">The engine's rate in Hz.</param>
/// <param name="Playing">Whether the transport is playing. When stopped, <paramref name="SongFrame"/> is the stopped position.</param>
/// <param name="SongFrame">Song position of the block's first frame.</param>
/// <param name="Jumped">
/// The song position is not the previous block's end: playback started, stopped, sought, or wrapped at the loop end.
/// Nodes that follow the song (clips, scheduled events) must relocate; sounding notes must be released.
/// </param>
/// <param name="Tempo">Beats per minute at the block's start, for plugins that ask (informational).</param>
public readonly record struct AudioRenderContext(int Frames, int SampleRate, bool Playing, long SongFrame, bool Jumped, double Tempo = 120);

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
    /// Frames the node's output lags its input or the song (plugin pipelining plus the plugin's own latency).
    /// Reported for later delay compensation; nothing compensates yet.
    /// </summary>
    public virtual int LatencyFrames => 0;

    /// <summary>
    /// Renders one block into <paramref name="output"/> (which holds silence on entry). <paramref name="input"/> is
    /// the sum of the nodes connected into this one, or silence.
    /// </summary>
    public abstract void Render(in AudioRenderContext context, StereoBuffer input, StereoBuffer output);
}
