namespace Bluestone.Audio;

/// <summary>A node that reads from disk. Its file work happens on the engine's disk-streamer thread, never on the audio thread.</summary>
internal interface IStreamedSource
{
    /// <summary>Reads ahead of the audio thread. Disk-streamer thread only.</summary>
    void Service();

    /// <summary>Closes files: by whoever built the node once no graph uses it, or by the engine when it is disposed. Idempotent.</summary>
    void Close();
}
