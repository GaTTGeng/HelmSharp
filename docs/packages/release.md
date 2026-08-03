# HelmSharp.Release

`HelmSharp.Release` contains the release model and Kubernetes Secret-backed storage used by lifecycle operations. Application code normally reaches it through `HelmSharp.Action`.

Install it directly only when implementing or inspecting release persistence outside `HelmClient`:

```powershell
dotnet add package HelmSharp.Release --version 1.3.1
```

Stored revisions are lifecycle evidence for outcomes such as deployed, superseded, failed, and retained-uninstalled. A failed revision can retain the complete attempted manifest even when resources were not, or were only partly, applied; the store is not a record of actual cluster state or a request to re-render the current chart.

Use the package guide for `HelmSharp.Action` before coupling application code to release storage. The [generated Release API](../api/generated/release.md) is the source of member-level detail.
