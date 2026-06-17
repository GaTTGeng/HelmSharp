using HelmSharp.Chart;
using HelmSharp.Engine;

var chartPath = args.Length > 0
    ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "sample-chart"));

if (!Directory.Exists(chartPath) && !File.Exists(chartPath + ".tgz"))
{
    Console.WriteLine("Usage: RenderChart <chart-path>");
    Console.WriteLine();
    Console.WriteLine("Provide a path to a Helm chart directory or .tgz archive.");
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
    valuesFile: null,
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
