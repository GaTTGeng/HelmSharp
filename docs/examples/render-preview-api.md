# Render Preview API

## What problem this solves

Many platforms need a preview endpoint: users choose a chart and values, then the product shows the manifest before anything touches the cluster.

## Packages to install

```powershell
dotnet add package HelmSharp.Chart --version 1.1.0
dotnet add package HelmSharp.Engine --version 1.1.0
```

## Minimal complete code

```csharp
app.MapPost("/preview", async (
    PreviewRequest request,
    CancellationToken cancellationToken) =>
{
    var chart = await HelmChartLoader.LoadAsync(request.ChartPath, cancellationToken);
    var values = await HelmValues.BuildAsync(
        chart,
        request.ValuesFiles,
        request.ValuesContent,
        request.SetValues,
        setFileValues: null,
        request.SetStringValues,
        request.SetJsonValues,
        cancellationToken);

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

## Why these APIs

This example avoids `HelmClient` because preview APIs usually should not own release state. The lower-level path makes it clear that the endpoint loads, merges, and renders only.

## Production notes

- Validate chart paths against an allowlist or internal chart registry.
- Store the values inputs used for each preview so later apply actions are reproducible.
- Add size limits for uploaded values content.

## Next step

Pair this with [Dry-run Deployment](dry-run-deployment.md) when the preview can become a release.
