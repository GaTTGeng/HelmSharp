# Build a render-preview endpoint

A preview endpoint should render a chart and return the manifest. It should not create a release, reach a cluster, or accept an arbitrary file-system path from a caller.

Install `HelmSharp.Chart` and `HelmSharp.Engine`, then resolve the requested chart through an application-owned catalog before calling the renderer:

```csharp
app.MapPost("/preview", async (
    PreviewRequest request,
    ChartCatalog charts,
    ValuesCatalog valuesCatalog,
    CancellationToken cancellationToken) =>
{
    var chartPath = charts.GetPath(request.ChartId); // Enforces your allowlist.
    var valuesFilePaths = valuesCatalog.GetPaths(request.ValuesFileIds); // Resolve IDs, never caller-provided paths.
    var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);
    var values = await HelmValues.BuildAsync(
        chart,
        valuesFiles: valuesFilePaths,
        valuesContent: request.ValuesContent,
        setValues: request.SetValues,
        setFileValues: null,
        setStringValues: request.SetStringValues,
        setJsonValues: request.SetJsonValues,
        cancellationToken: cancellationToken);

    var renderer = new HelmTemplateRenderer(
        chart,
        request.ReleaseName,
        request.Namespace,
        values,
        kubeVersion: request.KubeVersion,
        apiVersions: request.ApiVersions,
        isUpgrade: false);

    return Results.Text(renderer.Render(), "text/yaml");
});
```

`ChartCatalog` and `ValuesCatalog` are intentionally application-specific. They might resolve IDs to versioned directories, extracted archives, or a tenant's authorized catalog entries. Keeping path resolution outside the request prevents path traversal and unintended server-side file reads, and makes every preview traceable to exact inputs.

For a production endpoint:

- accept values-file IDs rather than paths, limit uploaded/inline values size, and reject override paths your product does not support;
- persist the chart version, effective input set, target capabilities, and rendered artifact for later approval;
- keep manifest and values access-controlled because either can contain credentials;
- call `RenderNotes()` only when the response has a separate field for human-facing notes.

The [review-to-deployment example](dry-run-deployment.md) shows how to turn the stored preview into a cluster-changing operation without replacing this render-only boundary.
