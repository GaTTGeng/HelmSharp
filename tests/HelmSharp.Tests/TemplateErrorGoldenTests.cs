using HelmSharp.Chart;
using HelmSharp.Engine;

namespace HelmSharp.Tests;

public class TemplateErrorGoldenTests
{
    [HelmCliTheory]
    [InlineData("error-required", "missing value for name", "missing value for name")]
    [InlineData("error-fail", "render failed: broken-input", "render failed: broken-input")]
    [InlineData("error-missing-function", "missingFunction", "missingFunction")]
    [InlineData("error-malformed", "unexpected EOF", "Missing 'end'")]
    public async Task Render_ErrorFixture_FailsWhenHelmTemplateFails(
        string fixtureName,
        string helmErrorNeedle,
        string managedErrorNeedle)
    {
        var chartPath = TestFixtures.ChartPath(fixtureName);
        var expected = await HelmCliRunner.TemplateAsync(
            chartPath,
            "golden-release",
            "golden-namespace",
            CancellationToken.None);

        Assert.NotEqual(0, expected.ExitCode);
        Assert.Contains(helmErrorNeedle, expected.Stderr);

        var chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);
        var values = await HelmValues.BuildAsync(
            chart,
            (IEnumerable<string>?)null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);
        var renderer = new HelmTemplateRenderer(chart, "golden-release", "golden-namespace", values);

        var exception = Assert.Throws<InvalidOperationException>(() => renderer.Render());
        Assert.Contains(managedErrorNeedle, exception.ToString());
    }
}
