# HelmSharp.Storage

`HelmSharp.Storage` defines the `IHelmReleaseStore` extension contract. It is for products that need to replace or wrap release persistence; ordinary applications should use `HelmSharp.Action` and its built-in release store.

```powershell
dotnet add package HelmSharp.Storage --version 1.3.1
```

When implementing the interface, preserve the distinction between revisions and the chart used to produce them. A store must support inspection of past lifecycle state without quietly re-rendering a newer chart.

See the [generated Storage API](../api/generated/storage.md) and [release workflow guide](../guide/release-workflows.md).
