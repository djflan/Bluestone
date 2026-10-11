using System.Collections.Immutable;
using Bluestone.Application.Editing;
using Bluestone.Application.Routing;
using Bluestone.Domain.Projects;
using Bluestone.Infrastructure.Projects;
using Bluestone.Midi.Files;
using Bluestone.Profiles;

namespace Bluestone.Application.Sessions;

/// <summary>What happened when a project or MIDI file was opened, for the UI to explain.</summary>
public sealed record OpenReport(
    bool RecoveredFromBackup,
    string? Problem,
    bool RecoveryAvailable,
    ImmutableArray<SmfDiagnostic> ImportDiagnostics);

/// <summary>
/// The project being edited: its undo history, where it is saved, and whether it has unsaved changes.
/// The UI drives the session; the session never touches UI types.
/// </summary>
public sealed class ProjectSession
{
    private readonly ProjectStore _store;
    private ProjectDocument _document;
    private Project? _saved;

    public ProjectSession(ProjectStore? store = null)
    {
        _store = store ?? new ProjectStore();
        var project = Project.CreateNew();
        _document = new ProjectDocument(project);
        _saved = project;
        History = new EditHistory(project);
        History.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public EditHistory History { get; }

    public Project Project => History.Current;

    /// <summary>The file the project was opened from or last saved to, if any.</summary>
    public string? FilePath { get; private set; }

    public bool IsDirty => !ReferenceEquals(Project, _saved);

    /// <summary>Raised after any change to the project, file path, or saved state.</summary>
    public event EventHandler? Changed;

    public bool Execute(IProjectCommand command) => History.Execute(command);

    public void New(string name = "Untitled")
    {
        var project = Project.CreateNew(name);
        Replace(project, new ProjectDocument(project), null);
    }

    public async Task<OpenReport> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await _store.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        Replace(result.Document.Project, result.Document, Path.GetFullPath(path));
        if (result.RecoveredFromBackup)
        {
            // What is in memory differs from the damaged file; saving should write it back.
            _saved = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new OpenReport(result.RecoveredFromBackup, result.Problem, result.RecoveryAvailable, []);
    }

    /// <summary>Replaces the session with the autosaved recovery copy of the open project.</summary>
    public async Task RestoreRecoveryAsync(CancellationToken cancellationToken = default)
    {
        var path = FilePath ?? throw new InvalidOperationException("The project has not been saved to a file.");
        var document = await _store.LoadRecoveryAsync(path, cancellationToken).ConfigureAwait(false);
        Replace(document.Project, document, path);
        _saved = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves to <paramref name="path"/>, or to <see cref="FilePath"/> when omitted, and clears any autosave.</summary>
    public async Task SaveAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        var target = path ?? FilePath ?? throw new InvalidOperationException("Choose where to save the project.");
        var project = Project;
        var document = _document.With(project);
        await _store.SaveAsync(document, target, cancellationToken).ConfigureAwait(false);
        ProjectStore.DiscardRecovery(target);
        _document = document;
        _saved = project;
        FilePath = Path.GetFullPath(target);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes an autosave beside the project file without saving it.</summary>
    public Task AutosaveAsync(CancellationToken cancellationToken = default) =>
        FilePath is { } path && IsDirty ? _store.SaveRecoveryAsync(_document.With(Project), path, cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// Starts a new, unsaved project from a Standard MIDI File. The file is only read, never modified.
    /// </summary>
    public async Task<OpenReport> ImportMidiAsync(string path, CancellationToken cancellationToken = default)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            var read = SmfReader.Read(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            var imported = SmfImporter.Import(read.File);
            var name = string.IsNullOrWhiteSpace(imported.Title) ? Path.GetFileNameWithoutExtension(path) : imported.Title;
            var project = Project.CreateNew(name.Length > Project.MaxNameLength ? name[..Project.MaxNameLength] : name) with { Sequence = imported.Sequence };
            Replace(project, new ProjectDocument(project), null);
            _saved = null;
            Changed?.Invoke(this, EventArgs.Empty);
            return new OpenReport(false, null, false, [.. read.Diagnostics, .. imported.Diagnostics]);
        }
    }

    /// <summary>Exports the sequence as a format 1 MIDI file and returns what could not be carried over.</summary>
    public Task<IReadOnlyList<SmfDiagnostic>> ExportMidiAsync(string path, CancellationToken cancellationToken = default) =>
        ExportMidiAsync(path, null, cancellationToken);

    /// <summary>
    /// Exports the sequence as a format 1 MIDI file and returns what could not be carried over. With
    /// <paramref name="profiles"/>, external instrument parameter automation is written as the MIDI it sends (ADR 0031).
    /// </summary>
    public async Task<IReadOnlyList<SmfDiagnostic>> ExportMidiAsync(string path, ProfileCatalog? profiles, CancellationToken cancellationToken = default)
    {
        var project = Project;
        var export = SmfExporter.Export(project.Sequence, project.Name, profiles is null ? null : HardwareAutomation.ForExport(project, profiles));
        var bytes = SmfWriter.Write(export.File);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        return export.Diagnostics;
    }

    private void Replace(Project project, ProjectDocument document, string? path)
    {
        _document = document;
        _saved = project;
        FilePath = path;
        History.Reset(project);
    }
}
