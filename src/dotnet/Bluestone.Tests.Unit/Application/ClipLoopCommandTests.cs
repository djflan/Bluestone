using Bluestone.Application.Audio;
using Bluestone.Application.Editing;
using Bluestone.Audio.Files;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Tests.Unit.Audio;

namespace Bluestone.Tests.Unit.Application;

/// <summary>Clip-loop commands: defaults, several clips at once, the loop handle, limits, and consolidation of both clip types.</summary>
public sealed class ClipLoopCommandTests : IDisposable
{
    private readonly string _directory = TestAudioFiles.Directory();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static NoteEvent Note(long at, long length) =>
        new(new Tick(at), new TickSpan(length), MidiChannel.FromNumber(1), new NoteNumber(60), new Velocity(100));

    private static (EditHistory History, TrackId Track, NoteClip A, NoteClip B) TwoClips()
    {
        var a = new NoteClip(ClipId.New(), Tick.Zero, new TickSpan(1920), new TickSpan(240), new EventList([Note(240, 100), Note(1000, 100)]));
        var b = new NoteClip(ClipId.New(), new Tick(4000), new TickSpan(960), TickSpan.Zero, new EventList([Note(0, 100)]))
        {
            Loop = new ClipLoop(Tick.Zero, new TickSpan(480), false),
        };
        var track = new Track(TrackId.New(), "t", [a, b]);
        var project = Project.CreateNew() with { Sequence = Project.CreateNew().Sequence.WithTrack(track) };
        return (new EditHistory(project), track.Id, a, b);
    }

    private static Clip Find(EditHistory history, TrackId track, ClipId clip) => history.Current.Sequence.FindTrack(track)!.FindClip(clip)!;

    [Fact]
    public void LoopingSeveralClips_IsOneStep_UsingEachClipsLoopOrWhatItPlaysNow()
    {
        var (history, track, a, b) = TwoClips();
        var before = history.Current.Sequence.FindTrack(track)!.ArrangedEvents;

        history.Execute(ClipLoopCommands.SetEnabled([(track, a.Id), (track, b.Id)], true));

        Assert.Equal(new ClipLoop(new Tick(240), new TickSpan(1920)), Find(history, track, a.Id).Loop);
        Assert.Equal(new ClipLoop(Tick.Zero, new TickSpan(480)), Find(history, track, b.Id).Loop);
        Assert.Equal("Loop Clips", history.UndoLabel);

        // Turning a loop on with the default changes nothing audible until the clip is made longer.
        Assert.Equal(before.Where(e => e.Position.Value < 4000), history.Current.Sequence.FindTrack(track)!.ArrangedEvents.Where(e => e.Position.Value < 4000));

        history.Execute(ClipLoopCommands.SetEnabled([(track, a.Id), (track, b.Id)], false));
        Assert.False(Find(history, track, a.Id).IsLooping);
        Assert.Equal(new TickSpan(1920), Find(history, track, a.Id).Loop!.Length);

        history.Undo();
        history.Undo();
        Assert.Null(Find(history, track, a.Id).Loop);
        Assert.False(Find(history, track, b.Id).Loop!.IsEnabled);
    }

    [Fact]
    public void TheLoopHandle_TurnsLoopingOn_AndStopsAtTheNextClip()
    {
        var (history, track, a, _) = TwoClips();

        history.Execute(ClipLoopCommands.ExtendLooping(track, a.Id, new Tick(9000)));

        var looped = Find(history, track, a.Id);
        Assert.True(looped.IsLooping);
        Assert.Equal(new Tick(4000), looped.End);
        Assert.Equal("Extend Clip Loop", history.UndoLabel);
    }

    [Fact]
    public void TheFirstRepeatMarker_ChangesTheLoopsLength()
    {
        var (history, track, _, b) = TwoClips();

        history.Execute(ClipLoopCommands.SetLength(track, b.Id, new TickSpan(240)));

        Assert.Equal(new ClipLoop(Tick.Zero, new TickSpan(240)), Find(history, track, b.Id).Loop);
    }

    [Fact]
    public void AnEditThatWouldRepeatTooManyEvents_IsRefused_AndChangesNothing()
    {
        var (history, track, _, b) = TwoClips();
        history.Execute(ClipLoopCommands.SetLength(track, b.Id, new TickSpan(1)));
        var before = history.Current;

        var ex = Assert.Throws<CommandRefusedException>(() => history.Execute(ClipLoopCommands.ExtendLooping(track, b.Id, new Tick(4000 + (960L * 4 * 400)))));

        Assert.Contains("loop", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(before, history.Current);
    }

    [Fact]
    public void ConsolidatingANoteClip_KeepsWhatPlays()
    {
        var (history, track, a, _) = TwoClips();
        history.Execute(ClipLoopCommands.SetLoop(track, a.Id, new ClipLoop(new Tick(240), new TickSpan(500))));
        var before = history.Current.Sequence.FindTrack(track)!.ArrangedEvents.Select(e => (e.Position, e.EndPosition)).ToList();

        history.Execute(ClipLoopCommands.ConsolidateNotes(track, [a.Id]));

        Assert.Null(Find(history, track, a.Id).Loop);
        Assert.Equal(before, history.Current.Sequence.FindTrack(track)!.ArrangedEvents.Select(e => (e.Position, e.EndPosition)));
    }

    [Fact]
    public async Task ConsolidatingAnAudioClip_WritesThePlayedAudio_AndThePlainClipPlaysIt()
    {
        // At 120 BPM and 960 PPQN a tick is 1/1920 s: 25 frames at 48 kHz.
        var samples = TestAudioFiles.Sine(48_000, 2, 997, 48_000);
        TestAudioFiles.Wave(_directory, "loop.wav", "float32", 48_000, 2, samples);
        var clip = new AudioClip(ClipId.New(), new Tick(960), new TickSpan(1920), new TickSpan(96), new AudioSource("loop.wav", 48_000, 48_000, 2))
        {
            Loop = new ClipLoop(new Tick(192), new TickSpan(288)),
            FadeOut = new TickSpan(48),
        };
        var track = new Track(TrackId.New(), "a", [clip], role: TrackRole.Audio);
        var project = Project.CreateNew() with { Sequence = Project.CreateNew().Sequence.WithTrack(track) };

        var command = await ClipLoopCommandTests.Consolidate(project, track.Id, clip.Id, _directory);
        var consolidated = (AudioClip)command.Apply(project).Sequence.FindTrack(track.Id)!.FindClip(clip.Id)!;

        Assert.Null(consolidated.Loop);
        Assert.Equal(TickSpan.Zero, consolidated.ContentOffset);
        Assert.Equal((clip.Start, clip.Length, clip.FadeOut), (consolidated.Start, consolidated.Length, consolidated.FadeOut));
        Assert.StartsWith(AudioConsolidation.FolderName, consolidated.Source.Location, StringComparison.Ordinal);

        using var reader = AudioFileReader.Open(Path.Combine(_directory, consolidated.Source.Location));
        Assert.Equal(48_000, reader.Format.Frames);
        var written = new float[reader.Format.Frames * 2];
        reader.Read(0, (int)reader.Format.Frames, written);
        for (var n = 0; n < reader.Format.Frames; n += 13)
        {
            // Offset 96 ticks = 2,400 frames; loop 192..480 ticks = 4,800..12,000 frames.
            var played = 2_400 + n;
            var file = played < 12_000 ? played : 4_800 + ((played - 12_000) % 7_200);
            Assert.Equal((float)samples[2 * file], written[2 * n]);
        }
    }

    private static Task<IProjectCommand> Consolidate(Project project, TrackId track, ClipId clip, string directory) =>
        AudioConsolidation.ConsolidateAsync(project, track, clip, directory);
}
