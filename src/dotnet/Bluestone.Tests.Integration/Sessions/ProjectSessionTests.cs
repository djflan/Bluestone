using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Files;

namespace Bluestone.Tests.Integration.Sessions;

public sealed class ProjectSessionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bluestone-session-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string PathOf(string name) => Path.Combine(_directory, name);

    [Fact]
    public async Task SaveAndReopen_TracksDirtyState()
    {
        var session = new ProjectSession();
        Assert.False(session.IsDirty);

        session.Execute(ProjectCommands.RenameProject("Song"));
        Assert.True(session.IsDirty);

        await session.SaveAsync(PathOf("song.bluestone"), Ct);
        Assert.False(session.IsDirty);

        var reopened = new ProjectSession();
        var report = await reopened.OpenAsync(PathOf("song.bluestone"), Ct);
        Assert.Equal("Song", reopened.Project.Name);
        Assert.False(reopened.IsDirty);
        Assert.False(report.RecoveredFromBackup);
        Assert.False(reopened.History.CanUndo);
    }

    [Fact]
    public async Task ImportMidi_CreatesAnUnsavedProjectAndNeverTouchesTheSource()
    {
        var sequence = Sequence.CreateEmpty(new Ppqn(96)).WithTrack(Track.FromEvents(TrackId.New(), "Piano", [new NoteEvent(Tick.Zero, new TickSpan(96), MidiChannel.FromIndex(0), NoteNumber.MiddleC, Velocity.Max)]));
        var midiPath = PathOf("tune.mid");
        await File.WriteAllBytesAsync(midiPath, SmfWriter.Write(SmfExporter.Export(sequence).File), Ct);
        var before = await File.ReadAllBytesAsync(midiPath, Ct);
        var writeTime = File.GetLastWriteTimeUtc(midiPath);

        var session = new ProjectSession();
        var report = await session.ImportMidiAsync(midiPath, Ct);

        Assert.Equal("tune", session.Project.Name);
        Assert.Equal("Piano", Assert.Single(session.Project.Sequence.Tracks).Name);
        Assert.True(session.IsDirty);
        Assert.Null(session.FilePath);
        Assert.Empty(report.ImportDiagnostics);
        Assert.Equal(before, await File.ReadAllBytesAsync(midiPath, Ct));
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(midiPath));
    }

    [Fact]
    public async Task ExportMidi_ReportsLossyChoices()
    {
        var session = new ProjectSession();
        var track = Track.Create("Muted");
        session.Execute(ProjectCommands.AddTrack(track));
        session.Execute(ProjectCommands.SetMuted(track.Id, true));

        var diagnostics = await session.ExportMidiAsync(PathOf("out.mid"), Ct);

        Assert.Contains(diagnostics, d => d.Code == SmfDiagnosticCodes.MixerStateNotStored);
        Assert.True(File.Exists(PathOf("out.mid")));
    }

    [Fact]
    public async Task Autosave_CanBeRestoredAndIsClearedBySaving()
    {
        var session = new ProjectSession();
        await session.SaveAsync(PathOf("song.bluestone"), Ct);
        session.Execute(ProjectCommands.RenameProject("Unsaved idea"));
        await session.AutosaveAsync(Ct);

        var crashed = new ProjectSession();
        var report = await crashed.OpenAsync(PathOf("song.bluestone"), Ct);
        Assert.True(report.RecoveryAvailable);
        await crashed.RestoreRecoveryAsync(Ct);
        Assert.Equal("Unsaved idea", crashed.Project.Name);
        Assert.True(crashed.IsDirty);

        await crashed.SaveAsync(cancellationToken: Ct);
        Assert.False((await new ProjectSession().OpenAsync(PathOf("song.bluestone"), Ct)).RecoveryAvailable);
    }
}
