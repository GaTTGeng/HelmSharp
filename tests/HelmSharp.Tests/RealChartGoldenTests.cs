using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
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

    [Fact]
    public void NormalizeDynamicValues_ReplacesRandAlphaNumSuffixes()
    {
        // Simulates YAML output where podinfo test templates use randAlphaNum 5 | lower
        var input = """
            apiVersion: v1
            kind: Pod
            metadata:
              name: golden-release-podinfo-grpc-test-a3x9m
              labels:
                app: podinfo
            ---
            apiVersion: v1
            kind: Pod
            metadata:
              name: golden-release-podinfo-tls-test-z7k2p
            """;

        var result = NormalizeDynamicValues(input);

        // Random 5-char lowercase suffix should be replaced with RANDOM
        Assert.Contains("name: golden-release-podinfo-grpc-test-RANDOM", result);
        Assert.Contains("name: golden-release-podinfo-tls-test-RANDOM", result);
        // Original random suffixes should not appear
        Assert.DoesNotContain("grpc-test-a3x9m", result);
        Assert.DoesNotContain("tls-test-z7k2p", result);
        // Identical non-random names should be untouched
        Assert.Contains("app: podinfo", result);
    }

    [HelmCliTheory]
    [MemberData(nameof(AllCharts))]
    public async Task Golden_RealChart(string chartName)
    {
        var result = await RunGoldenComparisonAsync(chartName);

        Assert.True(
            result.Verdict is GoldenVerdict.Pass,
            $"Chart '{chartName}' did not match helm template exactly.\n\n{result.Summary}");

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

        var helmNorm = NormalizeDynamicValues(NormalizeHelmOutput(helmResult.Stdout));
        var helmDocs = SplitDocuments(helmNorm);

        // 2. Load chart with HelmSharp
        HelmChart chart;
        Dictionary<string, object?> values;
        try
        {
            chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);
            values = await HelmValues.BuildAsync(chart, (IEnumerable<string>?)null, null, null, null, null, null, CancellationToken.None);
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
                var singleValues = await HelmValues.BuildAsync(singleChart, (IEnumerable<string>?)null, null, null, null, null, null, CancellationToken.None);
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
            var sharpNorm = NormalizeDynamicValues(NormalizeHelmOutput(fullOutput));
            var sharpDocs = SplitDocuments(sharpNorm);
            if (helmNorm == sharpNorm)
            {
                matchedDocs = helmDocs.Count;
                contentDiffDocs = 0;
            }
            else if (DocumentMultisetsEqual(helmDocs, sharpDocs))
            {
                matchedDocs = helmDocs.Count;
                contentDiffDocs = 0;
            }
            else
            {
                var comparison = CompareDocumentSets(helmDocs, sharpDocs);
                matchedDocs = comparison.matched;
                contentDiffDocs = comparison.diffCount;
            }

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

    /// <summary>
    /// Patterns that match non-deterministic template output (from randAlphaNum,
    /// randNumeric, randAscii, etc.) and replace them with stable placeholders.
    /// This is comparison-only normalization — it does not affect renderer behavior.
    ///
    /// IMPORTANT: Patterns are scoped to specific chart templates to avoid masking
    /// real rendering differences as false passes. When adding a new pattern, ensure
    /// it matches ONLY the known random-value usage site, not arbitrary name suffixes.
    /// </summary>
    private static readonly List<(Regex Pattern, string Replacement)> DynamicPatterns =
    [
        // podinfo: {{ randAlphaNum 5 | lower }} suffixes in test Pod names
        // Template pattern: name: {{ template "podinfo.fullname" . }}-<type>-test-{{ randAlphaNum 5 | lower }}
        (new Regex(@"(name:\s*[\w-]+-test-)([a-z0-9]{5})(\s*)$", RegexOptions.Multiline), "$1RANDOM$3"),
    ];

    /// <summary>
    /// Replaces non-deterministic template output with stable placeholders
    /// so that byte-for-byte comparison works despite randAlphaNum etc.
    /// </summary>
    internal static string NormalizeDynamicValues(string yaml)
    {
        foreach (var (pattern, replacement) in DynamicPatterns)
            yaml = pattern.Replace(yaml, replacement);
        return yaml;
    }

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
            if (line == "---")
            {
                if (current.Length > 0)
                {
                    docs.Add(current.ToString().TrimEnd() + "\n");
                    current.Clear();
                }
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

    private static bool DocumentMultisetsEqual(List<string> helmDocs, List<string> sharpDocs)
    {
        if (helmDocs.Count != sharpDocs.Count)
            return false;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var doc in helmDocs)
            counts[doc] = counts.GetValueOrDefault(doc) + 1;

        foreach (var doc in sharpDocs)
        {
            if (!counts.TryGetValue(doc, out var count))
                return false;
            if (count == 1)
                counts.Remove(doc);
            else
                counts[doc] = count - 1;
        }

        return counts.Count == 0;
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
