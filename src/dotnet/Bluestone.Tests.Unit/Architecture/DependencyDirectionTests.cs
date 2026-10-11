using System.Reflection;
using Bluestone.Tests.Unit.Playback;

namespace Bluestone.Tests.Unit.Architecture;

/// <summary>Project references must follow the documented dependency direction (README, ADR 0002).</summary>
public sealed class DependencyDirectionTests
{
    public static TheoryData<string, string[]> AllowedReferences() => new()
    {
        { "Bluestone.Domain", [] },
        { "Bluestone.Midi", ["Bluestone.Domain"] },
        { "Bluestone.Profiles", ["Bluestone.Domain"] },
        { "Bluestone.Signal", ["Bluestone.Domain"] },
        { "Bluestone.Playback", ["Bluestone.Domain", "Bluestone.Midi", "Bluestone.Signal"] },
        { "Bluestone.Application", ["Bluestone.Audio", "Bluestone.Domain", "Bluestone.Infrastructure", "Bluestone.Midi", "Bluestone.Playback", "Bluestone.Plugins", "Bluestone.Plugins.Protocol", "Bluestone.Profiles", "Bluestone.Signal"] },
        { "Bluestone.Infrastructure", ["Bluestone.Domain"] },
        { "Bluestone.Presentation", ["Bluestone.Application", "Bluestone.Audio", "Bluestone.Domain", "Bluestone.Infrastructure", "Bluestone.Midi", "Bluestone.Playback", "Bluestone.Plugins", "Bluestone.Profiles", "Bluestone.Signal"] },
        { "Bluestone.Plugins.Protocol", [] },
        { "Bluestone.Audio", ["Bluestone.Plugins", "Bluestone.Plugins.Protocol"] },
        { "Bluestone.Plugins", ["Bluestone.Plugins.Protocol"] },
        { "Bluestone.PluginWorker", ["Bluestone.Plugins.Protocol"] },
    };

    [Theory]
    [MemberData(nameof(AllowedReferences))]
    public void Project_ReferencesOnlyAllowedBluestoneAssemblies(string assembly, string[] allowed)
    {
        var references = Assembly.Load(assembly).GetReferencedAssemblies().Select(r => r.Name ?? string.Empty).ToList();

        var bluestone = references.Where(name => name.StartsWith("Bluestone", StringComparison.Ordinal));
        Assert.All(bluestone, name => Assert.Contains(name, allowed));
    }

    [Fact]
    public void Presentation_DoesNotDependOnAUiFramework() =>
        Assert.DoesNotContain(
            Assembly.Load("Bluestone.Presentation").GetReferencedAssemblies(),
            r => r.Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true);

    [Fact]
    public void Domain_ReferencesOnlyTheBaseClassLibrary()
    {
        var references = Assembly.Load("Bluestone.Domain").GetReferencedAssemblies().Select(r => r.Name ?? string.Empty);

        Assert.All(references, name => Assert.True(
            name is "System" or "mscorlib" or "netstandard" || name.StartsWith("System.", StringComparison.Ordinal),
            $"Bluestone.Domain must not reference {name}."));
    }

    [Fact]
    public void PluginsProtocol_ReferencesOnlyTheBaseClassLibrary()
    {
        var references = Assembly.Load("Bluestone.Plugins.Protocol").GetReferencedAssemblies().Select(r => r.Name ?? string.Empty);

        Assert.All(references, name => Assert.True(
            name is "System" or "mscorlib" or "netstandard" || name.StartsWith("System.", StringComparison.Ordinal),
            $"Bluestone.Plugins.Protocol must not reference {name}."));
    }

    [Fact]
    public void Plugins_DoesNotReferenceTheWorker_SoNoPluginCanRunInTheHostProcess() =>
        Assert.DoesNotContain(
            Assembly.Load("Bluestone.Plugins").GetReferencedAssemblies(),
            r => r.Name == "Bluestone.PluginWorker");

    [Fact]
    public void OnlyTheWorker_BuildsOrImportsTheNativePluginHost_SoNoPluginCodeLoadsInBluestonesProcess()
    {
        var source = Path.Combine(PlanDump.RepositoryRoot, "src");
        var projects = Directory.EnumerateFiles(source, "*.csproj", SearchOption.AllDirectories)
            .Where(p => File.ReadAllText(p).Contains("bluestone_plugin_host", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension);
        var importers = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => File.ReadAllText(p).Contains("\"bluestone_plugin_host\"", StringComparison.Ordinal))
            .Select(p => Path.GetRelativePath(source, p).Split(Path.DirectorySeparatorChar)[0])
            .Where(project => !project.StartsWith("Bluestone.Tests.", StringComparison.Ordinal))
            .Distinct();

        Assert.Equal(["Bluestone.PluginWorker"], projects);
        Assert.Equal(["Bluestone.PluginWorker"], importers);
    }
}
