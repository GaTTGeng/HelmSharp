using HelmSharp.Chart;
using HelmSharp.Engine;

var requestedChartPath = args.Length > 0
    ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "sample-chart"));
var chartPath = ResolveChartPath(requestedChartPath);

if (chartPath is null)
{
    Console.WriteLine("Usage: RenderChart <chart-path>");
    Console.WriteLine();
    Console.WriteLine("Provide a path to a Helm chart directory, .tgz archive, or .tar.gz archive.");
    Console.WriteLine("When omitted, the example uses examples/sample-chart.");
    return;
}

var chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);

Console.WriteLine($"Chart: {chart.Name} v{chart.Version}");
if (chart.AppVersion is not null)
    Console.WriteLine($"AppVersion: {chart.AppVersion}");
if (chart.Description is not null)
    Console.WriteLine($"Description: {chart.Description}");
Console.WriteLine();

var values = await HelmValues.BuildAsync(
    chart: chart,
    valuesFiles: null,
    valuesContent: null,
    setValues: new Dictionary<string, string>
    {
        ["replicaCount"] = "1",
        ["image.repository"] = "nginx",
        ["image.tag"] = "1.25"
    },
    setFileValues: null,
    setStringValues: null,
    setJsonValues: null,
    cancellationToken: CancellationToken.None);

var renderer = new HelmTemplateRenderer(chart, "my-release", "default", values);
var manifest = renderer.Render();

Console.WriteLine("--- Rendered Manifest ---");
Console.WriteLine(manifest);

var notes = renderer.RenderNotes();
if (!string.IsNullOrWhiteSpace(notes))
{
    Console.WriteLine("--- NOTES.txt ---");
    Console.WriteLine(notes);
}

static string? ResolveChartPath(string path)
{
    var fullPath = Path.GetFullPath(path);
    if (Directory.Exists(fullPath) || File.Exists(fullPath))
        return fullPath;

    var tgzPath = fullPath + ".tgz";
    if (File.Exists(tgzPath))
        return tgzPath;

    var tarGzPath = fullPath + ".tar.gz";
    return File.Exists(tarGzPath) ? tarGzPath : null;
}
