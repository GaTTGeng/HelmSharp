# HelmSharp.Kube

## Package responsibility

`HelmSharp.Kube` applies, deletes, identifies, and waits for Kubernetes resources from rendered YAML.

## When to install

Install this package when you already have manifests and want lower-level Kubernetes operations:

```powershell
dotnet add package HelmSharp.Kube --version 1.1.0
```

## Dependencies

This package depends on the Kubernetes .NET client and references `HelmSharp.Chart`.

## Main types

| Type | Use it for |
| --- | --- |
| `KubernetesManifestApplier` | Apply and delete multi-document YAML. |
| `KubernetesResourceWaiter` | Wait for common resources to become ready. |
| `ManifestIdentity` | Parse API version, kind, name, and namespace from YAML. |

## Common combinations

Use this package directly for platform controllers, or indirectly through `HelmClient.UpgradeInstallAsync`.

## Current boundaries

Wait behavior covers common Kubernetes resources. Uncommon CRD readiness semantics should be handled by product-specific checks when needed.
