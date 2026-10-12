using Bluestone.Application.Editing;
using Bluestone.Audio.Files;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Application.Audio;

/// <summary>
/// Consolidates a looping audio clip: writes the audio it plays (its file, through its loop, for the clip's length) to
/// a new WAVE file at the file's own rate, and makes the edit that has the clip play that file once, without a loop.
/// The clip keeps its position, length, gain, and fades (which belong to its outer edges and so are unchanged).
/// </summary>
/// <remarks>
/// Frames are copied, never resampled. A clip whose start falls between two of its file's frames plays the new file
/// from the nearest frame (less than half a sample from where it played), which is as close as a frame-based file can
/// hold it. The new file is not deleted by Undo: undo restores the clip, and the file stays for a redo.
/// </remarks>
public static class AudioConsolidation
{
    private const int ChunkFrames = 16_384;

    /// <summary>The folder consolidated files go into, beside the project file (or beside the source file while the project is unsaved).</summary>
    public const string FolderName = "Consolidated Audio";

    /// <summary>
    /// Writes the clip's audio and returns the command that switches the clip to it. <paramref name="projectDirectory"/>
    /// is the project file's folder (null while unsaved): sources are resolved against it and the new file's location
    /// is stored relative to it.
    /// </summary>
    /// <exception cref="FileNotFoundException">The clip's file is missing.</exception>
    /// <exception cref="AudioFileException">The clip's file cannot be read.</exception>
    public static async Task<IProjectCommand> ConsolidateAsync(Project project, TrackId trackId, ClipId clipId, string? projectDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Sequence.FindTrack(trackId)?.FindClip(clipId) is not AudioClip clip)
        {
            throw new ArgumentException("No such audio clip.", nameof(clipId));
        }

        var sourcePath = Path.IsPathRooted(clip.Source.Location) || projectDirectory is null
            ? clip.Source.Location
            : Path.GetFullPath(Path.Combine(projectDirectory, clip.Source.Location));
        var folder = Path.Combine(projectDirectory ?? Path.GetDirectoryName(sourcePath) ?? ".", FolderName);
        Directory.CreateDirectory(folder);
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var path = Path.Combine(folder, $"{name} {clip.Id.Value.ToString("N")[..8]} {DateTime.Now:yyyyMMdd-HHmmss}.wav");

        var tempo = project.Sequence.TempoMap;
        var (frames, channels, rate) = await Task.Run(() => Write(clip, tempo, sourcePath, path, cancellationToken), cancellationToken).ConfigureAwait(false);

        var location = projectDirectory is null ? path : Path.GetRelativePath(projectDirectory, path);
        var source = new AudioSource(location, rate, frames, channels);
        var consolidated = clip with { Source = source, ContentOffset = TickSpan.Zero, Loop = null };
        return ProjectCommands.EditTrack("Consolidate Clip", trackId, t => t.FindClip(clipId) is AudioClip ? t.WithClip(consolidated) : t);
    }

    private static (long Frames, int Channels, int Rate) Write(AudioClip clip, TempoMap tempo, string sourcePath, string path, CancellationToken cancellationToken)
    {
        using var reader = AudioFileReader.Open(sourcePath);
        var format = reader.Format;
        var rate = format.SampleRate;
        var channels = format.Channels;
        double Seconds(long tick) => tempo.TimeAt(new Tick(tick)).TotalSeconds;

        // As the engine plays it (ProjectAudioGraph, ClipSource): the unwrapped position at the clip's start, and the loop
        // in whole frames measured where the content starts.
        var origin = Seconds(clip.Origin);
        var first = (long)Math.Round((Seconds(clip.Start.Value) - origin) * rate);
        var total = (long)Math.Round((Seconds(clip.End.Value) - Seconds(clip.Start.Value)) * rate);
        long loopStart = -1, loopLength = 1;
        if (clip.Loop is { IsEnabled: true } loop)
        {
            loopStart = Math.Max(0, (long)Math.Round((Seconds(clip.Origin + loop.Start.Value) - origin) * rate));
            loopLength = Math.Max(1, (long)Math.Round((Seconds(clip.Origin + loop.End.Value) - Seconds(clip.Origin + loop.Start.Value)) * rate));
        }

        long Map(long played) => loopStart < 0 || played < loopStart + loopLength ? played : loopStart + ((played - loopStart - loopLength) % loopLength);

        var buffer = new float[ChunkFrames * channels];
        using (var writer = new WaveFileWriter(path, rate, channels))
        {
            var done = 0L;
            while (done < total)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var played = first + done;
                var file = Map(played);
                var span = (int)Math.Min(ChunkFrames, total - done);
                if (loopStart >= 0)
                {
                    span = (int)Math.Min(span, played < loopStart + loopLength ? loopStart + loopLength - played : loopStart + loopLength - file);
                }

                var chunk = buffer.AsSpan(0, span * channels);
                chunk.Clear();
                if (file >= 0)
                {
                    reader.Read(file, span, chunk);
                }

                writer.Write(chunk);
                done += span;
            }
        }

        return (total, channels, rate);
    }
}
