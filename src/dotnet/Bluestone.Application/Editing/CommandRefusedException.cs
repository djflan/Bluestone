using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;

namespace Bluestone.Application.Editing;

/// <summary>
/// An edit that was refused because it would break the project (a feedback loop, a connection that cannot
/// work) or hide content. Nothing was changed; <see cref="Exception.Message"/> says why in plain words.
/// </summary>
public sealed class CommandRefusedException : InvalidOperationException
{
    public CommandRefusedException()
    {
    }

    public CommandRefusedException(string message)
        : base(message)
    {
    }

    public CommandRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CommandRefusedException(string message, ImmutableArray<RoutingIssue> issues)
        : base(message) => Issues = issues;

    /// <summary>The routing errors the edit would have introduced, if that is why it was refused.</summary>
    public ImmutableArray<RoutingIssue> Issues { get; } = [];
}

/// <summary>Applies an edit only if it adds no routing error the project did not already have (ADR 0023).</summary>
internal static class RoutingGuard
{
    public static IProjectCommand Command(string label, DeviceDefinitionLookup definitions, Func<Project, Project> change, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return new ProjectCommand(label, before =>
        {
            var after = change(before);
            return ReferenceEquals(after, before) ? before : Check(before, after, definitions);
        })
        {
            MergeKey = mergeKey,
        };
    }

    /// <summary>Returns <paramref name="after"/>, or throws when it has errors <paramref name="before"/> did not.</summary>
    /// <exception cref="CommandRefusedException">The edit introduces a routing error.</exception>
    public static Project Check(Project before, Project after, DeviceDefinitionLookup definitions)
    {
        var existing = SignalRoutingValidator.Errors(SignalRoutingValidator.Validate(before, definitions)).Select(Key).ToList();
        var added = new List<RoutingIssue>();
        foreach (var issue in SignalRoutingValidator.Errors(SignalRoutingValidator.Validate(after, definitions)))
        {
            if (!existing.Remove(Key(issue)))
            {
                added.Add(issue);
            }
        }

        return added.Count == 0 ? after : throw new CommandRefusedException(added[0].Message, [.. added]);
    }

    private static (RoutingIssueCode, string) Key(RoutingIssue issue) => (issue.Code, issue.Message);
}
