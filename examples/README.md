# HelmSharp Examples

These examples are small console applications that show common SDK entry points.

## RenderChart

Renders `examples/sample-chart` with in-process chart loading, values merging, and template rendering.

```powershell
dotnet run --project examples/RenderChart/RenderChart.csproj
```

To render another chart directory or `.tgz` archive:

```powershell
dotnet run --project examples/RenderChart/RenderChart.csproj -- C:\charts\my-chart
```

## InstallRelease

Runs `UpgradeInstallAsync` against `examples/sample-chart`. The example defaults to dry-run mode so it does not mutate a cluster.

```powershell
dotnet run --project examples/InstallRelease/InstallRelease.csproj
```

To apply resources to the Kubernetes cluster configured by your kubeconfig:

```powershell
dotnet run --project examples/InstallRelease/InstallRelease.csproj -- C:\charts\my-chart demo --apply
```
