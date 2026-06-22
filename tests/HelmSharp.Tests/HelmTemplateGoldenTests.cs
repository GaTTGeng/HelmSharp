using System.Text.RegularExpressions;
using HelmSharp.Chart;
using HelmSharp.Engine;

namespace HelmSharp.Tests;

public class HelmTemplateGoldenTests
{
    [HelmCliTheory]
    [InlineData("minimal")]
    [InlineData("helpers")]
    [InlineData("multi-doc")]
    [InlineData("notes")]
    [InlineData("builtins")]
    public async Task Render_FixtureChart_MatchesHelmTemplate(string fixtureName)
    {
        var chartPath = TestFixtures.ChartPath(fixtureName);
        var expected = await HelmCliRunner.TemplateAsync(
            chartPath,
            "golden-release",
            "golden-namespace",
            CancellationToken.None);

        Assert.True(expected.ExitCode == 0, expected.Stderr);

        var chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);
        var values = await HelmValues.BuildAsync(chart, (IEnumerable<string>?)null, null, null, null, null, null, CancellationToken.None);
        var renderer = new HelmTemplateRenderer(chart, "golden-release", "golden-namespace", values);

        Assert.Equal(
            NormalizeManifest(expected.Stdout),
            NormalizeManifest(renderer.Render()));
    }

    [Fact]
    public async Task RenderNotes_NotesFixture_EmitsTemplatedNotes()
    {
        var chartPath = TestFixtures.ChartPath("notes");
        var chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);
        var values = await HelmValues.BuildAsync(chart, (IEnumerable<string>?)null, null, null, null, null, null, CancellationToken.None);
        var renderer = new HelmTemplateRenderer(chart, "golden-release", "golden-namespace", values);

        var notes = HelmCliRunner.NormalizeLineEndings(renderer.RenderNotes());

        Assert.Equal(
            "Thank you for installing notes.\n\n"
            + "Release: golden-release\n"
            + "Namespace: golden-namespace\n"
            + "Service port: 8080",
            notes);
    }

    private static string NormalizeManifest(string manifest)
    {
        var normalized = HelmCliRunner.NormalizeLineEndings(manifest);

        // Helm adds source comments to CLI output; HelmSharp does not emit them yet.
        normalized = Regex.Replace(normalized, @"(?m)^# Source: .+\n", string.Empty);
        return normalized.TrimEnd() + "\n";
    }
}
