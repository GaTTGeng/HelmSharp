using System.Text.RegularExpressions;
using HelmSharp.Action;
using HelmSharp.Chart;
using HelmSharp.Engine;

namespace HelmSharp.Tests;

/// <summary>
/// Golden tests that compare HelmSharp template rendering against real <c>helm template</c>
/// output for the fixture charts under <c>Fixtures/Charts</c>. Also pins NOTES.txt
/// rendering and dependency-condition values behavior without invoking the CLI.
/// </summary>
public class HelmTemplateGoldenTests
{
    // Per-template golden comparison for each fixture chart; the chart name is the test data.
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
    [InlineData("nested-default-dig")]
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

    [HelmCliFact]
    public async Task Render_NestedDefaultDigWithOverrides_MatchesHelmTemplate()
    {
        var chartPath = TestFixtures.ChartPath("nested-default-dig");
        var overrides = Path.Combine(chartPath, "overrides.yaml");
        var expected = await HelmCliRunner.TemplateAsync(
            chartPath, "golden-release", "golden-namespace", overrides, CancellationToken.None);
        Assert.True(expected.ExitCode == 0, expected.Stderr);

        var chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);
        var values = await HelmValues.BuildAsync(chart, [overrides], null, null, null, null, null, CancellationToken.None);
        var renderer = new HelmTemplateRenderer(chart, "golden-release", "golden-namespace", values);

        Assert.Equal(NormalizeManifest(expected.Stdout), NormalizeManifest(renderer.Render()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemplateAsync_IncludeDictRoot_PreservesOverriddenValues(bool useSetString)
    {
        var request = new HelmTemplateRequest
        {
            Chart = TestFixtures.ChartPath("named-templates"),
            ReleaseName = "demo",
            Namespace = "default"
        };
        if (useSetString)
            request.SetStringValues = new Dictionary<string, string> { ["target.name"] = "override" };
        else
            request.ValuesContent = "target:\n  name: override\n";

        var result = await new HelmClient(new StaticHelmOptionsProvider()).TemplateAsync(request);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("includeRoot: \"override:demo:named-templates:named-templates/templates/configmap.yaml\"", result.StandardOutput);
        Assert.Contains("includeScopedRoot: \"override:override\"", result.StandardOutput);
    }

    private sealed class StaticHelmOptionsProvider : IHelmOptionsProvider
    {
        public ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new HelmExecutionOptions());
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

    // Verifies a values-file condition can disable a subchart, and that the child's
    // exported-only values do not leak into the parent values map.
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
                [valuesFile],
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
        // Only line endings and those source comments are normalized — nothing else.
        normalized = Regex.Replace(normalized, @"(?m)^# Source: .+\n", string.Empty);
        return normalized.TrimEnd() + "\n";
    }
}
