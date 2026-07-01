# Real Chart Rendering

## What problem this solves

Real public charts exercise template behavior that small sample charts rarely cover. HelmSharp 1.1.0 validates `podinfo`, `metrics-server`, `external-dns`, `ingress-nginx`, and `cert-manager` with 129/129 templates passing.

## Packages to install

```powershell
dotnet add package HelmSharp.Action --version 1.1.0
```

## Minimal complete code

```csharp
var result = await client.TemplateAsync(new HelmTemplateRequest
{
    ReleaseName = "ingress-nginx",
    Namespace = "ingress-system",
    Chart = "/charts/ingress-nginx",
    ValuesFiles = ["ci/controller-deployment-values.yaml"],
    KubeVersion = "1.30.0",
    ApiVersions =
    [
        "networking.k8s.io/v1",
        "policy/v1",
        "monitoring.coreos.com/v1"
    ],
    IncludeCRDs = true,
    ShowNotes = true
}, cancellationToken);

Console.WriteLine(result.StandardOutput);
```

## Why these APIs

`HelmTemplateRequest` maps to the preview shape many Helm users already know: release, namespace, chart, values, kube version, API versions, CRDs, and notes.

## Production notes

- Pin chart versions when rendering public charts.
- Keep the chart copy or chart provenance tied to the preview output.
- Use the compatibility page for current real-chart parity data.

## Next step

Open [Helm Compatibility](../helm-compatibility.md) for the full 1.1.0 matrix.
