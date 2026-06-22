using HelmSharp.Chart;
using HelmSharp.Engine;
using Xunit.Abstractions;

namespace HelmSharp.Tests;

/// <summary>
/// Focused tests for else-if chain reconstruction.
/// </summary>
public class ElseIfReconstructionTests
{
    private readonly ITestOutputHelper _output;

    public ElseIfReconstructionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ElseIf_Chain_WithDefaultValues()
    {
        // Reproduce the controller-poddisruptionbudget template structure
        var chart = new HelmChart
        {
            Name = "test", Version = "1.0.0", ValuesYaml = ""
        };
        chart.Templates["templates/test.yaml"] = """
            {{- if eq .Values.controller.kind "Deployment" }}
            {{- $replicas := .Values.controller.replicaCount }}
            {{- if and .Values.controller.autoscaling.enabled (not .Values.controller.keda.enabled) }}
            {{- $replicas = "autoscaling" }}
            {{- else if and .Values.controller.keda.enabled (not .Values.controller.autoscaling.enabled) }}
            {{- $replicas = "keda" }}
            {{- end }}
            replicas: {{ $replicas }}
            {{- end }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "rel", "ns",
            new Dictionary<string, object?>
            {
                ["controller"] = new Dictionary<string, object?>
                {
                    ["kind"] = "Deployment",
                    ["replicaCount"] = 3L,
                    ["autoscaling"] = new Dictionary<string, object?> { ["enabled"] = false },
                    ["keda"] = new Dictionary<string, object?> { ["enabled"] = false }
                }
            });

        var result = renderer.Render();
        _output.WriteLine($"Result ({result.Length} chars):");
        _output.WriteLine(result);

        // Should render "replicas: 3" (the else-if branch is false, so original value stays)
        Assert.Contains("replicas: 3", result);
    }

    [Fact]
    public void ElseIf_Taken_WithCorrectValues()
    {
        var chart = new HelmChart
        {
            Name = "test", Version = "1.0.0", ValuesYaml = ""
        };
        chart.Templates["templates/test.yaml"] = """
            {{- if and .Values.controller.autoscaling.enabled (not .Values.controller.keda.enabled) }}
            scaling: autoscaling
            {{- else if and .Values.controller.keda.enabled (not .Values.controller.autoscaling.enabled) }}
            scaling: keda
            {{- else }}
            scaling: none
            {{- end }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "rel", "ns",
            new Dictionary<string, object?>
            {
                ["controller"] = new Dictionary<string, object?>
                {
                    ["autoscaling"] = new Dictionary<string, object?> { ["enabled"] = false },
                    ["keda"] = new Dictionary<string, object?> { ["enabled"] = true }
                }
            });

        var result = renderer.Render();
        _output.WriteLine($"Result: {result}");

        Assert.Contains("scaling: keda", result);
    }
}
