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
    [InlineData("files")]
    [InlineData("whitespace-formatting")]
    [InlineData("named-templates")]
    [InlineData("dependencies")]
    [InlineData("dependency-values")]
    [InlineData("control-flow")]
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

    [HelmCliFact]
    public async Task Render_DependencyConditionOverride_MatchesHelmTemplate()
    {
        var chartPath = TestFixtures.ChartPath("dependency-values");
        var valuesFile = Path.Combine(Path.GetTempPath(), $"helmsharp-dependency-values-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(valuesFile, "child:\n  enabled: false\n");
        try
        {
            var expected = await HelmCliRunner.TemplateAsync(
                chartPath,
                "golden-release",
                "golden-namespace",
                valuesFile,
                CancellationToken.None);
            Assert.True(expected.ExitCode == 0, expected.Stderr);

            var chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);
            var values = await HelmValues.BuildAsync(
                chart,
                new[] { valuesFile },
                null,
                null,
                null,
                null,
                null,
                CancellationToken.None);
            var renderer = new HelmTemplateRenderer(chart, "golden-release", "golden-namespace", values);

            Assert.Equal(NormalizeManifest(expected.Stdout), NormalizeManifest(renderer.Render()));
            Assert.False(values.ContainsKey("exportedOnly"));
        }
        finally
        {
            File.Delete(valuesFile);
        }
    }

    [Fact]
    public async Task RenderNotes_WhitespaceFormattingFixture_EmitsTrimmedNotes()
    {
        var chartPath = TestFixtures.ChartPath("whitespace-formatting");
        var chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);
        var values = await HelmValues.BuildAsync(chart, (IEnumerable<string>?)null, null, null, null, null, null, CancellationToken.None);
        var renderer = new HelmTemplateRenderer(chart, "golden-release", "golden-namespace", values);

        var notes = HelmCliRunner.NormalizeLineEndings(renderer.RenderNotes());

        Assert.Equal(
            "Release golden-release rendered whitespace-formatting.\n\n"
            + "Ports:\n"
            + "  8080\n"
            + "Lines:\n"
            + "    first\n"
            + "    second",
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
