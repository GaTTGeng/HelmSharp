using HelmSharp.Chart;
using HelmSharp.Engine;
using Xunit.Abstractions;

namespace HelmSharp.Tests;

public class ReconstructedBodyTests
{
    private readonly ITestOutputHelper _output;

    public ReconstructedBodyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ManualReconstructedBody_RendersCorrectly()
    {
        // This is exactly what the else-if reconstruction should produce
        var chart = new HelmChart
        {
            Name = "test", Version = "1.0.0", ValuesYaml = ""
        };
        chart.Templates["templates/test.yaml"] = """
            {{- if and .Values.x (not .Values.y) }}
            branch: first
            {{- else }}
            branch: second
            {{- end }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "rel", "ns",
            new Dictionary<string, object?>
            {
                ["x"] = true,
                ["y"] = false
            });

        var result = renderer.Render();
        _output.WriteLine($"Result: {result}");
        Assert.Contains("branch: first", result);
    }

    [Fact]
    public void SimpleElseIf_RendersCorrectly()
    {
        // Simplest possible else-if pattern
        var chart = new HelmChart
        {
            Name = "test", Version = "1.0.0", ValuesYaml = ""
        };
        chart.Templates["templates/test.yaml"] = """
            {{- if .Values.a }}
            A
            {{- else if .Values.b }}
            B
            {{- end }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "rel", "ns",
            new Dictionary<string, object?>
            {
                ["a"] = false,
                ["b"] = true
            });

        var result = renderer.Render();
        _output.WriteLine($"Result: '{result}'");
        Assert.Contains("B", result);
    }

    [Fact]
    public void ElseIf_WithParenthesizedCondition_RendersCorrectly()
    {
        // Same as the cert-manager/external-dns pattern
        var chart = new HelmChart
        {
            Name = "test", Version = "1.0.0", ValuesYaml = ""
        };
        chart.Templates["templates/test.yaml"] = """
            {{- if and .Values.a (not .Values.b) }}
            BRANCH1
            {{- else if and .Values.c (not .Values.d) }}
            BRANCH2
            {{- end }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "rel", "ns",
            new Dictionary<string, object?>
            {
                ["a"] = false,
                ["b"] = false,
                ["c"] = true,
                ["d"] = false
            });

        var result = renderer.Render();
        _output.WriteLine($"Result: '{result}'");
        Assert.Contains("BRANCH2", result);
    }
}
