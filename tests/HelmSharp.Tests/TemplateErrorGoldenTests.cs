using HelmSharp.Chart;
using HelmSharp.Engine;

namespace HelmSharp.Tests;

public class TemplateErrorGoldenTests
{
    [HelmCliTheory]
    [InlineData("error-required", "missing value for name", "missing value for name")]
    [InlineData("error-fail", "render failed: broken-input", "render failed: broken-input")]
    [InlineData("error-missing-function", "missingFunction", "missingFunction")]
    [InlineData("error-missing-function-no-args", "missingFunction", "missingFunction")]
    [InlineData("error-substr", "slice bounds out of range [4:2]", "slice bounds out of range [4:2]")]
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

    [Fact]
    public void Render_MissingNoArgumentFunction_Throws()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/configmap.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: missing-function
            data:
              value: {{ missingFunction }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default", new Dictionary<string, object?>());

        var exception = Assert.Throws<InvalidOperationException>(() => renderer.Render());
        Assert.Contains("missingFunction", exception.ToString());
    }

    [Fact]
    public void Render_SubstrEndBeforeStart_Throws()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/configmap.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: substr-error
            data:
              value: {{ substr 4 2 "hello" | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default", new Dictionary<string, object?>());

        var exception = Assert.Throws<InvalidOperationException>(() => renderer.Render());
        Assert.Contains("slice bounds out of range [4:2]", exception.ToString());
    }
}
