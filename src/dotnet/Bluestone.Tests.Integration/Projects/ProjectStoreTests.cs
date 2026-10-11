using System.Text;
using System.Text.Json.Nodes;
using Bluestone.Domain.Projects;
using Bluestone.Infrastructure.Projects;

namespace Bluestone.Tests.Integration.Projects;

public sealed class ProjectStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bluestone-tests-").FullName;
    private readonly ProjectStore _store = new();

    private string ProjectPath => Path.Combine(_directory, "song.bluestone");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static ProjectDocument Document(string name) => new(Project.CreateNew(name));

    [Fact]
    public async Task SaveThenLoad_RoundTrips()
    {
        await _store.SaveAsync(Document("First"), ProjectPath, Ct);

        var result = await _store.LoadAsync(ProjectPath, Ct);

        Assert.Equal("First", result.Document.Project.Name);
        Assert.False(result.RecoveredFromBackup);
        Assert.False(File.Exists(ProjectStore.BackupPath(ProjectPath)));
        Assert.Equal(["song.bluestone"], Directory.GetFiles(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Save_KeepsThePreviousVersionAsBackup()
    {
        await _store.SaveAsync(Document("First"), ProjectPath, Ct);
        await _store.SaveAsync(Document("Second"), ProjectPath, Ct);

        var backup = ProjectSerializer.Default.Deserialize(await File.ReadAllBytesAsync(ProjectStore.BackupPath(ProjectPath), Ct));

        Assert.Equal("First", backup.Project.Name);
        Assert.Equal("Second", (await _store.LoadAsync(ProjectPath, Ct)).Document.Project.Name);
    }

    [Fact]
    public async Task FailedSave_LeavesTheProjectUntouchedAndCleansUp()
    {
        await _store.SaveAsync(Document("Safe"), ProjectPath, Ct);
        var before = await File.ReadAllBytesAsync(ProjectPath, Ct);
        _store.BeforeCommit = _ => throw new IOException("Simulated failure just before commit.");

        await Assert.ThrowsAsync<IOException>(() => _store.SaveAsync(Document("Doomed"), ProjectPath, Ct));

        Assert.Equal(before, await File.ReadAllBytesAsync(ProjectPath, Ct));
        Assert.Equal(["song.bluestone"], Directory.GetFiles(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task CancelledSave_LeavesTheProjectUntouched()
    {
        await _store.SaveAsync(Document("Safe"), ProjectPath, Ct);
        using var cancellation = new CancellationTokenSource();
        _store.BeforeCommit = async _ => await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.SaveAsync(Document("Cancelled"), ProjectPath, cancellation.Token));

        Assert.Equal("Safe", (await _store.LoadAsync(ProjectPath, Ct)).Document.Project.Name);
    }

    [Fact]
    public async Task CorruptProject_IsRecoveredFromBackupAndReported()
    {
        await _store.SaveAsync(Document("Good"), ProjectPath, Ct);
        await _store.SaveAsync(Document("Newer"), ProjectPath, Ct);
        await File.WriteAllTextAsync(ProjectPath, "{ \"format\": \"bluestone-project\", \"formatVersion\": 1, \"proj", Ct);

        var result = await _store.LoadAsync(ProjectPath, Ct);

        Assert.True(result.RecoveredFromBackup);
        Assert.Equal("Good", result.Document.Project.Name);
        Assert.Contains("not valid JSON", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingProject_IsRecoveredFromBackup()
    {
        await _store.SaveAsync(Document("Good"), ProjectPath, Ct);
        await _store.SaveAsync(Document("Newer"), ProjectPath, Ct);
        File.Delete(ProjectPath);

        Assert.True((await _store.LoadAsync(ProjectPath, Ct)).RecoveredFromBackup);
    }

    [Fact]
    public async Task CorruptProjectAndBackup_ExplainsBoth()
    {
        await File.WriteAllTextAsync(ProjectPath, "garbage", Ct);
        await File.WriteAllTextAsync(ProjectStore.BackupPath(ProjectPath), "{}", Ct);

        var error = await Assert.ThrowsAsync<ProjectFormatException>(() => _store.LoadAsync(ProjectPath, Ct));

        Assert.Contains("neither could its backup", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewerProject_IsNeverReplacedByItsBackup()
    {
        await _store.SaveAsync(Document("Old"), ProjectPath, Ct);
        await _store.SaveAsync(Document("Current"), ProjectPath, Ct);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(ProjectPath, Ct))!.AsObject();
        json["formatVersion"] = 99;
        await File.WriteAllTextAsync(ProjectPath, json.ToJsonString(), Ct);
        var before = await File.ReadAllBytesAsync(ProjectPath, Ct);

        await Assert.ThrowsAsync<ProjectVersionException>(() => _store.LoadAsync(ProjectPath, Ct));

        Assert.Equal(before, await File.ReadAllBytesAsync(ProjectPath, Ct));
    }

    [Fact]
    public async Task RecoveryFile_IsOfferedWhenNewerThanTheProject()
    {
        await _store.SaveAsync(Document("Saved"), ProjectPath, Ct);
        File.SetLastWriteTimeUtc(ProjectPath, DateTime.UtcNow.AddMinutes(-5));
        await _store.SaveRecoveryAsync(Document("Unsaved work"), ProjectPath, Ct);

        var result = await _store.LoadAsync(ProjectPath, Ct);

        Assert.True(result.RecoveryAvailable);
        Assert.Equal("Saved", result.Document.Project.Name);
        Assert.Equal("Unsaved work", (await _store.LoadRecoveryAsync(ProjectPath, Ct)).Project.Name);
        Assert.False(File.Exists(ProjectStore.BackupPath(ProjectPath)));

        ProjectStore.DiscardRecovery(ProjectPath);
        Assert.False((await _store.LoadAsync(ProjectPath, Ct)).RecoveryAvailable);
    }

    [Fact]
    public async Task SaveIntoMissingDirectory_FailsWithoutSideEffects()
    {
        var path = Path.Combine(_directory, "missing", "song.bluestone");

        await Assert.ThrowsAnyAsync<IOException>(() => _store.SaveAsync(Document("x"), path, Ct));

        Assert.Empty(Directory.GetFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SavedFile_IsPlainUtf8Json()
    {
        await _store.SaveAsync(Document("Ünïcödé"), ProjectPath, Ct);

        var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(ProjectPath, Ct));

        Assert.Contains("\"bluestone-project\"", text, StringComparison.Ordinal);
        Assert.False(text.StartsWith('﻿'));
    }
}
