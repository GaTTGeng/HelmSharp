using HelmSharp.Repo;

namespace HelmSharp.Tests;

public sealed class HelmChartVersionResolverTests
{
    [Fact]
    public void Resolve_WithoutConstraintSelectsLatestStableVersion()
    {
        var selected = HelmChartVersionResolver.Resolve(CreateVersions(
            "1.2.0",
            "1.10.0",
            "2.0.0-beta.1"), constraint: null);

        Assert.NotNull(selected);
        Assert.Equal("1.10.0", selected.Version);
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("1.2", "1.2.5+build.7")]
    [InlineData("1", "1.10.0")]
    [InlineData("1.2.x", "1.2.5+build.7")]
    [InlineData("*", "1.10.0")]
    [InlineData("~1.2.0", "1.2.5+build.7")]
    [InlineData(">=1.0.0 <2.0.0", "1.10.0")]
    [InlineData(">=2.0.0-beta.1 <2.0.0", "2.0.0-beta.1")]
    public void Resolve_WithConstraintSelectsNewestMatchingVersion(string constraint, string expectedVersion)
    {
        var selected = HelmChartVersionResolver.Resolve(CreateVersions(
            "1.2.0",
            "1.2.5+build.7",
            "1.10.0",
            "2.0.0-beta.1"), constraint);

        Assert.NotNull(selected);
        Assert.Equal(expectedVersion, selected.Version);
    }

    [Fact]
    public void Resolve_ExcludesPrereleaseWhenConstraintDoesNotOptIn()
    {
        var selected = HelmChartVersionResolver.Resolve(CreateVersions(
            "2.0.0-beta.1"), ">=2.0.0 <3.0.0");

        Assert.Null(selected);
    }

    [Fact]
    public void CompareVersions_UsesSemanticPrecedenceAndIgnoresBuildMetadata()
    {
        Assert.True(HelmChartVersionResolver.CompareVersions("1.10.0", "1.2.0") > 0);
        Assert.True(HelmChartVersionResolver.CompareVersions("2.0.0", "2.0.0-beta.1") > 0);
        Assert.Equal(0, HelmChartVersionResolver.CompareVersions("1.2.0+build.1", "1.2.0+build.2"));
    }

    private static List<HelmChartVersion> CreateVersions(params string[] versions)
        => versions.Select(version => new HelmChartVersion
        {
            Name = "example",
            Version = version,
            Urls = [$"example-{version}.tgz"]
        }).ToList();
}
