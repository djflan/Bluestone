using Bluestone.Profiles;

namespace Bluestone.Tests.Integration.Profiles;

/// <summary>Every profile shipped in /profiles must load cleanly and meet the contribution rules.</summary>
public sealed class ShippedProfileTests
{
    public static TheoryData<string> ProfileFiles() =>
        [.. Directory.EnumerateFiles(RepositoryPaths.Profiles, "*" + ProfileLoader.FileExtension).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(ProfileFiles))]
    public void Profile_LoadsWithoutErrorsOrWarnings(string file)
    {
        var result = ProfileLoader.LoadFile(Path.Combine(RepositoryPaths.Profiles, file));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [MemberData(nameof(ProfileFiles))]
    public void Profile_MeetsContributionRules(string file)
    {
        var profile = ProfileLoader.LoadFile(Path.Combine(RepositoryPaths.Profiles, file)).Profile!;

        Assert.True(profile.Provenance.RedistributionConfirmed);
        Assert.NotEmpty(profile.Provenance.Sources);
        Assert.All(profile.Initialization, step => Assert.NotNull(profile.FindTemplate(step.TemplateId)));
        Assert.All(profile.Templates.Where(t => t.Name.Contains("System On", StringComparison.Ordinal)), t => Assert.True(t.RequiresConfirmation));

        // Profiles that name a vendor format must carry a non-affiliation notice.
        if (profile.Protocols.Any(p => p is ProtocolFamily.Xg or ProtocolFamily.Gs))
        {
            Assert.Contains(profile.Notices, n => n.Contains("not affiliated", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Catalog_LoadsEveryShippedProfile()
    {
        var catalog = ProfileCatalog.LoadDirectory(RepositoryPaths.Profiles);

        Assert.Empty(catalog.Failures);
        Assert.NotNull(catalog.Find("bluestone.generic.gm1"));
        Assert.NotNull(catalog.Find("bluestone.generic.xg"));
        Assert.Equal(128, catalog.Find("bluestone.generic.gm1")!.Banks.Single().Programs.Length);
        Assert.Equal("F0 43 10 4C 00 00 7E 00 F7", Convert.ToHexString(catalog.Find("bluestone.generic.xg")!.FindTemplate("xg-system-on")!.Render().Bytes.Span).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + " " + b));
    }
}
