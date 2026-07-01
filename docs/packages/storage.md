# HelmSharp.Storage

## Package responsibility

`HelmSharp.Storage` contains release storage extension contracts.

## When to install

Most applications use `HelmSharp.Action` instead. Install directly only when implementing custom storage integration.

```powershell
dotnet add package HelmSharp.Storage --version 1.1.0
```

## Dependencies

This package references release and Kubernetes helper packages.

## Main types

| Type | Use it for |
| --- | --- |
| `IHelmReleaseStore` | Extension point for release record storage. |

## Common combinations

Use this package when a product needs to abstract release storage behind its own implementation.

## Current boundaries

The built-in release store lives in `HelmSharp.Release`; this package exists for the storage contract.
