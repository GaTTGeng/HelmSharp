# HelmSharp.Release

## Package responsibility

`HelmSharp.Release` stores Helm-style release records in Kubernetes Secrets.

## When to install

Most applications get this package through `HelmSharp.Action`. Install it directly only when building custom release storage workflows.

```powershell
dotnet add package HelmSharp.Release --version 1.2.0
```

## Dependencies

This package depends on the Kubernetes .NET client and references chart and Kubernetes helper packages.

## Main types

| Type | Use it for |
| --- | --- |
| `HelmReleaseStore` | Save, list, load, and update release records. |
| `HelmReleaseRecord` | Represent a release revision, manifest, values, and status. |

## Common combinations

`HelmClient` uses `HelmReleaseStore` for install, upgrade, uninstall, status, history, and get operations.

## Current boundaries

Release storage follows Helm-style Secret records, but this package is not a general-purpose audit database.
