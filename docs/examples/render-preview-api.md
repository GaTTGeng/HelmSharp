# Build a render-preview endpoint

A preview endpoint should render a chart and return the manifest. It should not create a release, reach a cluster, or accept an arbitrary file-system path from a caller.

Install `HelmSharp.Chart` and `HelmSharp.Engine`, then resolve the requested chart through an application-owned catalog before calling the renderer:

```csharp
app.MapPost("/preview", async (
    PreviewRequest request,
    ChartCatalog charts,
    CancellationToken cancellationToken) =>
{
    var chartPath = charts.GetPath(request.ChartId); // Enforces your allowlist.
    var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);
    var values = await HelmValues.BuildAsync(
        chart,
        valuesFiles: request.ValuesFiles,
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
        apiVersions: request.ApiVersions);

    return Results.Text(renderer.Render(), "text/yaml");
});
```

`ChartCatalog` is intentionally application-specific. It might resolve a chart ID to a versioned directory, an extracted archive, or a tenant's authorized catalog entry. Keeping that decision outside the request prevents path traversal and makes every preview traceable to an exact chart version.

For a production endpoint:

- limit uploaded/inline values size and reject override paths your product does not support;
- persist the chart version, effective input set, target capabilities, and rendered artifact for later approval;
- keep manifest and values access-controlled because either can contain credentials;
- call `RenderNotes()` only when the response has a separate field for human-facing notes.

The [review-to-deployment example](dry-run-deployment.md) shows how to turn the stored preview into a cluster-changing operation without replacing this render-only boundary.
