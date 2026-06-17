using System.Text.RegularExpressions;
using HelmSharp.Chart;
using HelmSharp.Engine;

namespace HelmSharp.Tests;

public class HelmTemplateGoldenTests
{
    [HelmCliFact]
    public async Task Render_MinimalChart_MatchesHelmTemplate()
    {
        var chartPath = TestFixtures.ChartPath("minimal");
        var expected = await HelmCliRunner.TemplateAsync(
            chartPath,
            "golden-release",
            "golden-namespace",
            CancellationToken.None);

        Assert.True(expected.ExitCode == 0, expected.Stderr);

        var chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);
        var values = await HelmValues.BuildAsync(chart, null, null, null, null, null, null, CancellationToken.None);
        var renderer = new HelmTemplateRenderer(chart, "golden-release", "golden-namespace", values);

        Assert.Equal(
            NormalizeManifest(expected.Stdout),
            NormalizeManifest(renderer.Render()));
    }

    private static string NormalizeManifest(string manifest)
    {
        var normalized = HelmCliRunner.NormalizeLineEndings(manifest);

        // Helm adds source comments to CLI output; HelmSharp does not emit them yet.
        normalized = Regex.Replace(normalized, @"(?m)^# Source: .+\n", string.Empty);
        return normalized.TrimEnd() + "\n";
    }
}
