namespace Bluestone.Audio;

/// <summary>A node that reads from disk. Its file work happens on the engine's disk-streamer thread, never on the audio thread.</summary>
internal interface IStreamedSource
{
    /// <summary>Reads ahead of the audio thread. Disk-streamer thread only.</summary>
    void Service();

    /// <summary>Closes files once the node has left the current graph. Idempotent; the node must tolerate a close from its builder too.</summary>
    void Close();
}
