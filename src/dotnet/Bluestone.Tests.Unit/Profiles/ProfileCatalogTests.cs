using System.Text;
using Bluestone.Profiles;

namespace Bluestone.Tests.Unit.Profiles;

public sealed class ProfileCatalogTests
{
    private static ProfileLoadResult Profile(string id, string name) => ProfileLoader.Load(Encoding.UTF8.GetBytes($$"""
        { "format": "bluestone-device-profile", "schemaVersion": 1, "id": "{{id}}", "version": "1", "name": "{{name}}",
          "provenance": { "sources": ["t"], "contributors": ["t"], "license": "MIT", "redistributionConfirmed": true, "verification": "unverified" } }
        """));

    [Fact]
    public void Add_KeepsValidProfilesAndRecordsFailures()
    {
        var catalog = ProfileCatalog.Empty
            .Add("b.json", Profile("b.b", "Bravo"))
            .Add("a.json", Profile("a.a", "Alpha"))
            .Add("bad.json", ProfileLoader.Load("{}"u8));

        Assert.Equal(["Alpha", "Bravo"], catalog.Profiles.Select(p => p.Name));
        Assert.Equal("bad.json", Assert.Single(catalog.Failures).Source);
        Assert.NotNull(catalog.Find("a.a"));
        Assert.Null(catalog.Find("missing"));
    }

    [Fact]
    public void Add_RejectsDuplicateIds()
    {
        var catalog = ProfileCatalog.Empty.Add("one.json", Profile("same.id", "One")).Add("two.json", Profile("same.id", "Two"));

        Assert.Equal("One", Assert.Single(catalog.Profiles).Name);
        Assert.Contains("already loaded", Assert.Single(catalog.Failures).Diagnostics[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadDirectory_ToleratesMissingDirectory() =>
        Assert.Empty(ProfileCatalog.LoadDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).Profiles);
}
