# Generate manifests for GitOps

For GitOps, HelmSharp's job ends at deterministic rendered YAML. Your repository workflow creates the commit or pull request; HelmSharp should not also apply the same manifest to a cluster.

```csharp
var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);
var values = await HelmValues.BuildAsync(
    chart,
    valuesFiles: [environmentValuesPath],
    valuesContent: null,
    setValues: new Dictionary<string, string>
    {
        ["image.tag"] = buildVersion
    },
    setFileValues: null,
    setStringValues: null,
    setJsonValues: null,
    cancellationToken: cancellationToken);

var renderer = new HelmTemplateRenderer(
    chart,
    releaseName: applicationName,
    releaseNamespace: targetNamespace,
    values: values,
    kubeVersion: targetKubeVersion,
    apiVersions: targetApiVersions,
    isUpgrade: false);

var manifest = renderer.Render();
await File.WriteAllTextAsync(outputPath, manifest, cancellationToken);
```

Commit the generated file together with a record of the chart version, values-file revision, image tag, Kubernetes version, and custom API versions. Those inputs explain a diff far better than the rendered YAML alone.

If the repository expects one file per resource or a different ordering, make that an explicit post-rendering step and test it independently. Do not rely on a deployment controller discovering which template engine happened to generate the file.

Use a release workflow only when this same service also owns direct cluster mutation. For a GitOps-only architecture, [rendering](../guide/first-render.md) plus repository automation is the safer boundary.
