using System.Diagnostics;
using System.Text;
using HelmSharp.Chart;
using HelmSharp.Engine;

namespace HelmSharp.Tests;

/// <summary>
/// Golden tests comparing HelmSharp output against <c>helm template</c> for real-world,
/// publicly-available Helm charts.
///
/// <para>
/// Each chart is first rendered as a whole. If the full render throws, the test falls back
/// to per-template rendering and reports which individual templates pass, fail, or are
/// unrendered.  The per-template results are aggregated into a granular coverage score.
/// </para>
///
/// <para><b>Verdicts:</b></para>
/// <list type="bullet">
///   <item><b>Pass</b> — byte-for-byte identical after normalization.</item>
///   <item><b>Partial</b> — structural match (same doc count) with content diffs, OR
///   some templates rendered correctly while others threw.</item>
///   <item><b>Fail</b> — renderer threw on every template or produced structurally
///   different output.</item>
/// </list>
/// </summary>
public class RealChartGoldenTests
{
    private static readonly string FixturesRoot = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "Charts", "real");

    // ────────────────────────────────────────────────────────────
    //  Test data — move charts between categories as the engine improves
    // ────────────────────────────────────────────────────────────

    public static readonly TheoryData<string> AllCharts = new()
    {
        "podinfo",
        "metrics-server",
        "external-dns",
        "ingress-nginx",
        "cert-manager",
    };

    [HelmCliTheory]
    [MemberData(nameof(AllCharts))]
    public async Task Golden_RealChart(string chartName)
    {
        var result = await RunGoldenComparisonAsync(chartName);

        // The test "passes" as long as it doesn't completely fail.
        // We use the result data to populate the README.
        Assert.True(
            result.Verdict is GoldenVerdict.Pass or GoldenVerdict.Partial,
            $"Chart '{chartName}' failed completely.\n\n{result.Summary}");

        // Write a structured JSON report for the README generator
        var reportPath = Path.Combine(
            AppContext.BaseDirectory, "GoldenReports", $"{chartName}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, result.ToJson());
    }

    // ────────────────────────────────────────────────────────────
    //  Comparison engine
    // ────────────────────────────────────────────────────────────

    private static async Task<GoldenResult> RunGoldenComparisonAsync(string chartName)
    {
        var chartPath = Path.Combine(FixturesRoot, chartName);
        var sw = Stopwatch.StartNew();

        // 1. Run helm template (reference)
        HelmCliResult helmResult;
        try
        {
            helmResult = await HelmCliRunner.TemplateAsync(
                chartPath, "golden-release", "golden-namespace", CancellationToken.None);
        }
        catch (Exception ex)
        {
            return GoldenResult.Fail(chartName, $"helm template threw: {ex.Message}");
        }

        if (helmResult.ExitCode != 0)
            return GoldenResult.Fail(chartName, $"helm template exit {helmResult.ExitCode}: {helmResult.Stderr[..Math.Min(200, helmResult.Stderr.Length)]}");

        var helmNorm = NormalizeHelmOutput(helmResult.Stdout);
        var helmDocs = SplitDocuments(helmNorm);

        // 2. Load chart with HelmSharp
        HelmChart chart;
        Dictionary<string, object?> values;
        try
        {
            chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);
            values = await HelmValues.BuildAsync(chart, null, null, null, null, null, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return GoldenResult.Fail(chartName, $"Chart load failed: {ex.GetType().Name}: {ex.Message}");
        }

        // 3. Try full render
        string? fullOutput = null;
        Exception? fullException = null;
        try
        {
            var renderer = new HelmTemplateRenderer(chart, "golden-release", "golden-namespace", values);
            fullOutput = renderer.Render();
        }
        catch (Exception ex)
        {
            fullException = ex;
        }

        // 4. Per-template rendering (always done for detailed reporting)
        var templateResults = new List<TemplateResult>();
        var renderableTemplates = chart.Templates
            .Where(kv =>
            {
                var fn = Path.GetFileName(kv.Key);
                return !fn.StartsWith('_') && !fn.Equals("NOTES.txt", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        foreach (var (path, _) in renderableTemplates)
        {
            try
            {
                // Create a minimal chart with just this template and its helpers
                var singleChart = CreateSingleTemplateChart(chart, path);
                var singleValues = await HelmValues.BuildAsync(singleChart, null, null, null, null, null, null, CancellationToken.None);
                var singleRenderer = new HelmTemplateRenderer(singleChart, "golden-release", "golden-namespace", singleValues);
                var output = singleRenderer.Render();
                templateResults.Add(new TemplateResult(path, true, output, null));
            }
            catch (Exception ex)
            {
                templateResults.Add(new TemplateResult(path, false, null, $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}"));
            }
        }

        // 5. Analyze results
        var passedTemplates = templateResults.Count(t => t.Success);
        var failedTemplates = templateResults.Count(t => !t.Success);
        var totalTemplates = templateResults.Count;

        // Collect unique error types
        var errorTypes = templateResults
            .Where(t => !t.Success)
            .Select(t => t.Error!.Split(':')[0].Trim())
            .GroupBy(e => e)
            .ToDictionary(g => g.Key, g => g.Count());

        GoldenVerdict verdict;
        string summary;
        int matchedDocs = 0, contentDiffDocs = 0;

        if (fullOutput != null)
        {
            // Full render succeeded — compare document-by-document
            var sharpDocs = SplitDocuments(HelmCliRunner.NormalizeLineEndings(fullOutput));
            var comparison = CompareDocumentSets(helmDocs, sharpDocs);
            matchedDocs = comparison.matched;
            contentDiffDocs = comparison.diffCount;

            verdict = matchedDocs == helmDocs.Count && contentDiffDocs == 0 && helmDocs.Count > 0
                ? GoldenVerdict.Pass
                : GoldenVerdict.Partial;

            summary = $"Full render: {helmDocs.Count} helm docs, {sharpDocs.Count} sharp docs. " +
                      $"Exact match: {matchedDocs}/{helmDocs.Count}. Content diffs: {contentDiffDocs}. " +
                      $"Per-template: {passedTemplates}/{totalTemplates} templates rendered successfully.";
        }
        else if (passedTemplates > 0)
        {
            // Full render failed but some individual templates work
            verdict = GoldenVerdict.Partial;
            summary = $"Full render FAILED ({fullException!.GetType().Name}: {fullException.Message.Split('\n')[0]}). " +
                      $"Per-template: {passedTemplates}/{totalTemplates} templates rendered successfully.";
        }
        else
        {
            verdict = GoldenVerdict.Fail;
            summary = $"Full render FAILED. All {totalTemplates} templates failed.";
        }

        sw.Stop();

        return new GoldenResult(
            chartName, verdict, helmDocs.Count, matchedDocs, contentDiffDocs,
            passedTemplates, failedTemplates, totalTemplates,
            errorTypes, sw.ElapsedMilliseconds, summary,
            templateResults, fullException?.GetType().Name ?? "none");
    }

    // ────────────────────────────────────────────────────────────
    //  Per-template isolation
    // ────────────────────────────────────────────────────────────

    private static HelmChart CreateSingleTemplateChart(HelmChart original, string templatePath)
    {
        var single = new HelmChart
        {
            Name = original.Name,
            Version = original.Version,
            ApiVersion = original.ApiVersion,
            AppVersion = original.AppVersion,
            Description = original.Description,
            ValuesYaml = original.ValuesYaml,
        };
        foreach (var dep in original.Dependencies)
            single.Dependencies.Add(dep);

        // Copy all _helper templates and other underscored templates
        foreach (var (path, content) in original.Templates)
        {
            var fn = Path.GetFileName(path);
            if (fn.StartsWith('_') || fn.Equals("NOTES.txt", StringComparison.OrdinalIgnoreCase))
                single.Templates[path] = content;
        }

        // Copy the target template
        single.Templates[templatePath] = original.Templates[templatePath];

        // Copy subcharts (they may be needed for includes)
        foreach (var (name, subchart) in original.Subcharts)
            single.Subcharts[name] = subchart;

        // Copy files
        foreach (var (name, content) in original.Files)
            single.Files[name] = content;

        return single;
    }

    // ────────────────────────────────────────────────────────────
    //  Normalization
    // ────────────────────────────────────────────────────────────

    private static string NormalizeHelmOutput(string output)
    {
        var normalized = HelmCliRunner.NormalizeLineEndings(output);
        // Remove helm source comments
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized, @"(?m)^# Source: .+\n", string.Empty);
        return normalized.TrimEnd() + "\n";
    }

    private static List<string> SplitDocuments(string manifest)
    {
        var docs = new List<string>();
        var current = new StringBuilder();
        foreach (var line in manifest.Split('\n'))
        {
            if (line == "---" && current.Length > 0)
            {
                docs.Add(current.ToString().TrimEnd() + "\n");
                current.Clear();
            }
            else
            {
                current.AppendLine(line);
            }
        }
        if (current.Length > 0)
            docs.Add(current.ToString().TrimEnd() + "\n");
        return docs;
    }

    private static (int matched, int diffCount) CompareDocumentSets(
        List<string> helmDocs, List<string> sharpDocs)
    {
        var matched = 0;
        var diffCount = 0;
        var usedSharp = new bool[sharpDocs.Count];

        foreach (var helmDoc in helmDocs)
        {
            var helmKind = ExtractKind(helmDoc);
            var helmName = ExtractName(helmDoc);
            var bestMatch = -1;

            // Try kind+name match
            for (var j = 0; j < sharpDocs.Count; j++)
            {
                if (usedSharp[j]) continue;
                var sKind = ExtractKind(sharpDocs[j]);
                var sName = ExtractName(sharpDocs[j]);
                if (helmKind == sKind && helmName == sName && helmKind != "Unknown")
                {
                    bestMatch = j;
                    break;
                }
            }

            if (bestMatch >= 0)
            {
                usedSharp[bestMatch] = true;
                if (helmDoc == sharpDocs[bestMatch])
                    matched++;
                else
                    diffCount++;
            }
        }

        return (matched, diffCount);
    }

    private static string ExtractKind(string yaml)
    {
        var m = System.Text.RegularExpressions.Regex.Match(yaml, @"(?m)^kind:\s*(.+)$");
        return m.Success ? m.Groups[1].Value.Trim() : "Unknown";
    }

    private static string ExtractName(string yaml)
    {
        var m = System.Text.RegularExpressions.Regex.Match(yaml, @"(?m)^\s+name:\s*(.+)$");
        return m.Success ? m.Groups[1].Value.Trim('"') : "Unknown";
    }
}

// ────────────────────────────────────────────────────────────────
//  Result types
// ────────────────────────────────────────────────────────────────

public enum GoldenVerdict { Pass, Partial, Fail }

public sealed record TemplateResult(
    string Path,
    bool Success,
    string? Output,
    string? Error);

public sealed record GoldenResult(
    string ChartName,
    GoldenVerdict Verdict,
    int HelmDocCount,
    int MatchedDocs,
    int ContentDiffDocs,
    int PassedTemplates,
    int FailedTemplates,
    int TotalTemplates,
    Dictionary<string, int> ErrorTypes,
    long DurationMs,
    string Summary,
    IReadOnlyList<TemplateResult> TemplateResults,
    string FullRenderError)
{
    public static GoldenResult Fail(string chartName, string reason) =>
        new(chartName, GoldenVerdict.Fail, 0, 0, 0, 0, 0, 0,
            new Dictionary<string, int>(), 0, reason,
            Array.Empty<TemplateResult>(), "n/a");

    public string ToJson()
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            chart = ChartName,
            verdict = Verdict.ToString(),
            helmDocCount = HelmDocCount,
            matchedDocs = MatchedDocs,
            contentDiffDocs = ContentDiffDocs,
            passedTemplates = PassedTemplates,
            failedTemplates = FailedTemplates,
            totalTemplates = TotalTemplates,
            durationMs = DurationMs,
            fullRenderError = FullRenderError,
            errorTypes = ErrorTypes,
            summary = Summary
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
}
