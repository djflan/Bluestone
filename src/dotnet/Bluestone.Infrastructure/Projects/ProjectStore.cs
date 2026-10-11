using System.Globalization;

namespace Bluestone.Infrastructure.Projects;

/// <summary>The outcome of opening a project.</summary>
/// <param name="Document">The project that was read.</param>
/// <param name="RecoveredFromBackup">True when the main file was unreadable and the backup was used instead.</param>
/// <param name="Problem">Why the main file could not be read, when a backup was used.</param>
/// <param name="RecoveryAvailable">True when an autosaved recovery file is newer than the project file.</param>
public sealed record ProjectLoadResult(ProjectDocument Document, bool RecoveredFromBackup, string? Problem, bool RecoveryAvailable);

/// <summary>
/// Saves and opens project files safely.
/// </summary>
/// <remarks>
/// <para>
/// Saving writes the complete file to a temporary sibling, flushes it to disk, reads it back to
/// verify it, copies the previous file to <c>.bak</c>, and only then renames the temporary file over
/// the project. The project file therefore always holds either the previous or the new version.
/// </para>
/// <para>
/// Opening falls back to the backup when the main file is damaged, and reports that it did. A file
/// from a newer Bluestone is never replaced by its backup.
/// </para>
/// </remarks>
public sealed class ProjectStore(ProjectSerializer? serializer = null)
{
    private readonly ProjectSerializer _serializer = serializer ?? ProjectSerializer.Default;

    /// <summary>Test hook invoked with the verified temporary file just before it replaces the project.</summary>
    internal Func<string, Task>? BeforeCommit { get; set; }

    public static string BackupPath(string path) => path + ".bak";

    public static string RecoveryPath(string path) => path + ".recovery";

    public Task SaveAsync(ProjectDocument document, string path, CancellationToken cancellationToken = default) =>
        WriteAtomicallyAsync(document, path, keepBackup: true, cancellationToken);

    /// <summary>Writes an autosave next to the project without touching the project or its backup.</summary>
    public Task SaveRecoveryAsync(ProjectDocument document, string path, CancellationToken cancellationToken = default) =>
        WriteAtomicallyAsync(document, RecoveryPath(path), keepBackup: false, cancellationToken);

    /// <exception cref="ProjectFormatException">Neither the project nor its backup could be read.</exception>
    /// <exception cref="ProjectVersionException">The project was saved by a newer version of Bluestone.</exception>
    /// <exception cref="FileNotFoundException">Neither the project nor a backup exists.</exception>
    public async Task<ProjectLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var recoveryAvailable = File.Exists(RecoveryPath(path))
            && (!File.Exists(path) || File.GetLastWriteTimeUtc(RecoveryPath(path)) > File.GetLastWriteTimeUtc(path));

        try
        {
            var document = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            return new ProjectLoadResult(document, false, null, recoveryAvailable);
        }
        catch (Exception ex) when (ex is (ProjectFormatException and not ProjectVersionException) or IOException && File.Exists(BackupPath(path)))
        {
            ProjectDocument backup;
            try
            {
                backup = await ReadAsync(BackupPath(path), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception backupError) when (backupError is ProjectFormatException or IOException)
            {
                throw new ProjectFormatException($"The project could not be opened ({ex.Message}), and neither could its backup ({backupError.Message}).", ex);
            }

            return new ProjectLoadResult(backup, true, ex.Message, recoveryAvailable);
        }
    }

    public Task<ProjectDocument> LoadRecoveryAsync(string path, CancellationToken cancellationToken = default) =>
        ReadAsync(RecoveryPath(path), cancellationToken);

    public static void DiscardRecovery(string path)
    {
        if (File.Exists(RecoveryPath(path)))
        {
            File.Delete(RecoveryPath(path));
        }
    }

    private async Task<ProjectDocument> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The project file does not exist.", path);
        }

        if (info.Length > ProjectSerializer.MaxBytes)
        {
            throw new ProjectFormatException("$", string.Create(CultureInfo.InvariantCulture, $"the project file is larger than {ProjectSerializer.MaxBytes} bytes."));
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return _serializer.Deserialize(bytes);
    }

    private async Task WriteAtomicallyAsync(ProjectDocument document, string path, bool keepBackup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var bytes = _serializer.Serialize(document);

        try
        {
            var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            // Verify what actually reached the disk before trusting it.
            _serializer.Deserialize(await File.ReadAllBytesAsync(temp, cancellationToken).ConfigureAwait(false));

            if (BeforeCommit is { } hook)
            {
                await hook(temp).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (keepBackup && File.Exists(fullPath))
            {
                File.Copy(fullPath, BackupPath(fullPath), overwrite: true);
            }

            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
