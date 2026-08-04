# Render a public chart

Treat a public chart as an input you must pin, inspect, and validate. The fact that it works with one Helm installation does not establish compatibility with every managed renderer.

First use `HelmSharp.Repo` or your artifact pipeline to pull a specific chart version into a controlled directory. Then render that extracted chart exactly as you would an internal chart:

```csharp
var chart = await HelmChartLoader.LoadAsync(extractedChartPath, cancellationToken);
var values = await HelmValues.BuildAsync(
    chart,
    valuesFiles: ["values.organization.yaml"],
    valuesContent: null,
    setValues: null,
    setFileValues: null,
    setStringValues: null,
    setJsonValues: null,
    cancellationToken: cancellationToken);

var renderer = new HelmTemplateRenderer(
    chart,
    releaseName: "external-dns",
    releaseNamespace: "platform",
    values: values,
    kubeVersion: "1.30.0",
    apiVersions: ["externaldns.k8s.io/v1alpha1"],
    isUpgrade: false);

var manifest = renderer.Render();
```

Before rolling this into a product, pin the archive digest and test the exact chart version with the exact values and capabilities you will use. If output differs from Helm, reduce the difference to a small chart/template, then compare the effective values, target capabilities, and the [function matrix](../template-function-compatibility.md). [HelmCompare](../compare.md) is useful for the side-by-side part of that investigation.

The public-chart golden tests in this repository are regression evidence, not a certification that every version of every public chart is supported.
