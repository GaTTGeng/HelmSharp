using HelmSharp.Chart;
using HelmSharp.Engine;
using Xunit.Abstractions;

namespace HelmSharp.Tests;

/// <summary>
/// Focused diagnostic tests for real-chart template rendering failures.
/// Prints detailed error information to help identify parser gaps.
/// </summary>
public class RealChartDiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public RealChartDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [HelmCliFact]
    public async Task Diagnose_IngressNginx_ControllerPodDisruptionBudget()
    {
        var chartPath = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Charts", "real", "ingress-nginx");
        var chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);
        var values = await HelmValues.BuildAsync(chart, null, null, null, null, null, null, CancellationToken.None);
        var renderer = new HelmTemplateRenderer(chart, "rel", "ns", values);

        // List all defined templates
        _output.WriteLine($"Defined templates: {renderer.DefinedTemplates.Count}");
        foreach (var (name, body) in renderer.DefinedTemplates.OrderBy(kv => kv.Key))
        {
            _output.WriteLine($"  [{name}] ({body.Length} chars)");
        }

        // Check each define body for balanced if/end pairs
        _output.WriteLine("");
        _output.WriteLine("Checking define body balance...");
        var regex = new System.Text.RegularExpressions.Regex(
            "{{-?\\s*(?<expr>.*?)\\s*-?}}",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (var (name, body) in renderer.DefinedTemplates)
        {
            var depth = 0;
            var matches = regex.Matches(body);
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                var expr = m.Groups["expr"].Value.Trim();
                if (expr.StartsWith("if ", StringComparison.Ordinal) || expr == "if" ||
                    expr.StartsWith("with ", StringComparison.Ordinal) ||
                    expr.StartsWith("range ", StringComparison.Ordinal) ||
                    expr.StartsWith("define ", StringComparison.Ordinal))
                    depth++;
                else if (expr == "end")
                    depth--;
            }
            if (depth != 0)
                _output.WriteLine($"  UNBALANCED: [{name}] depth={depth} (should be 0)");
            else
                _output.WriteLine($"  OK: [{name}]");
        }

        // Try rendering each template individually and report errors
        _output.WriteLine("");
        _output.WriteLine("Rendering individual templates...");
        var targetPath = "templates/controller-poddisruptionbudget.yaml";

        // Create single-template chart
        var single = new HelmChart
        {
            Name = chart.Name, Version = chart.Version,
            ApiVersion = chart.ApiVersion, AppVersion = chart.AppVersion,
            ValuesYaml = chart.ValuesYaml
        };
        foreach (var dep in chart.Dependencies) single.Dependencies.Add(dep);
        foreach (var (p, c) in chart.Templates)
        {
            var fn = Path.GetFileName(p);
            if (fn.StartsWith('_') || fn.Equals("NOTES.txt", StringComparison.OrdinalIgnoreCase))
                single.Templates[p] = c;
        }
        single.Templates[targetPath] = chart.Templates[targetPath];

        try
        {
            var singleValues = await HelmValues.BuildAsync(single, null, null, null, null, null, null, CancellationToken.None);
            var singleRenderer = new HelmTemplateRenderer(single, "rel", "ns", singleValues);
            var output = singleRenderer.Render();
            _output.WriteLine($"  RENDER OK: {output.Length} chars");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"  RENDER FAILED: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                _output.WriteLine($"  INNER: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }

        Assert.True(true); // diagnostic only
    }
}
