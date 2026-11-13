namespace Bluestone.Application.Audio;

/// <summary>
/// What plugin instances must be prepared for to play in the open stream: its sample rate, and the engine's block size
/// (the largest block they are given, and the block their pipeline delays by).
/// </summary>
public readonly record struct AudioFormat(int SampleRate, int BlockFrames);
