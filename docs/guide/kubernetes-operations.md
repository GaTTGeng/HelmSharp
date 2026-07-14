# Kubernetes Operations

## What problem this solves

Use `HelmSharp.Kube` when you already have rendered YAML and want managed apply, delete, namespace, resource identity, or wait behavior without the higher-level release facade.

## Packages to install

```powershell
dotnet add package HelmSharp.Kube --version 1.1.1
```

## Minimal complete code

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#manifest-applier{csharp}

## Why these APIs

`KubernetesManifestApplier` splits multi-document YAML, identifies resources by API version, kind, name, and namespace, and applies or deletes them through the Kubernetes .NET client. `KubernetesResourceWaiter` watches common workload readiness after apply.

## Production notes

- Construct the Kubernetes client from the same kubeconfig policy your product uses elsewhere.
- Use a stable `fieldManager` such as `helmsharp` or your product name.
- Treat delete operations as cluster mutations; keep them behind the same approval path as apply operations.

## Next step

Review [Error Handling](error-handling.md) before exposing release actions to users.
