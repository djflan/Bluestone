using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Bluestone.Domain.Projects;

namespace Bluestone.Infrastructure.Projects;

/// <summary>
/// A project plus file-level data Bluestone does not interpret: the <c>extensions</c> object and any
/// unknown top-level properties. Both are written back unchanged on save.
/// </summary>
public sealed class ProjectDocument
{
    private readonly JsonObject _preserved;

    public ProjectDocument(Project project, JsonObject? preserved = null)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        _preserved = preserved is null ? [] : (JsonObject)preserved.DeepClone();
    }

    public Project Project { get; }

    /// <summary>What reading changed to make the project consistent, such as track roles widened to fit their content. Not saved.</summary>
    public ImmutableArray<string> Notes { get; init; } = [];

    /// <summary>A copy of the preserved top-level properties, keyed by name.</summary>
    public JsonObject Preserved => (JsonObject)_preserved.DeepClone();

    public ProjectDocument With(Project project) => new(project, _preserved);
}

/// <summary>A project file that cannot be read, with the JSON path of the problem when there is one.</summary>
public class ProjectFormatException : Exception
{
    public ProjectFormatException()
    {
    }

    public ProjectFormatException(string message)
        : base(message)
    {
    }

    public ProjectFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ProjectFormatException(string path, string message, Exception? innerException = null)
        : base($"{path}: {message}", innerException) => JsonPath = path;

    public string? JsonPath { get; }
}

/// <summary>
/// The file was written by a newer Bluestone. It must never be replaced by an older backup or
/// overwritten, or the newer data would be lost.
/// </summary>
public sealed class ProjectVersionException : ProjectFormatException
{
    public ProjectVersionException()
    {
    }

    public ProjectVersionException(string message)
        : base(message)
    {
    }

    public ProjectVersionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ProjectVersionException(int fileVersion, int supportedVersion)
        : base("$.formatVersion", $"the project was saved by a newer version of Bluestone (format {fileVersion}); this version reads formats up to {supportedVersion}.")
    {
        FileVersion = fileVersion;
    }

    public int FileVersion { get; }
}

/// <summary>Upgrades a project file from <see cref="FromVersion"/> to the next version, in place.</summary>
public interface IProjectMigration
{
    int FromVersion { get; }

    void Migrate(JsonObject root);
}
