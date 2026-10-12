using System.Text.Json.Nodes;
using static Bluestone.Infrastructure.Projects.NodeReader;

namespace Bluestone.Infrastructure.Projects;

/// <summary>
/// Format 5 separates the arranger loop's range from whether it loops, and adds clip loops. A format 4 project had a
/// loop only while looping was on, so its loop becomes an enabled one. Clips had no loops, so they play as before.
/// </summary>
internal sealed class Format4To5Migration : IProjectMigration
{
    public int FromVersion => 4;

    public void Migrate(JsonObject root)
    {
        var project = Object(root, "project", "$");
        if (OptionalObject(project, "loop", "$.project") is { } loop)
        {
            loop["enabled"] = true;
        }
    }
}
