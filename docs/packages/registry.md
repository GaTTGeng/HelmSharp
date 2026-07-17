# HelmSharp.Registry

## Package responsibility

`HelmSharp.Registry` contains registry-related extension contracts for future OCI workflows.

## When to install

Most applications do not install this package directly. It is referenced by `HelmSharp.Action` and `HelmSharp.Repo`.

```powershell
dotnet add package HelmSharp.Registry --version 1.2.0
```

## Dependencies

This package has no Kubernetes dependency.

## Main types

| Type | Use it for |
| --- | --- |
| `IOciRegistryClient` | Extension point for OCI registry integration. |

## Common combinations

Use it when experimenting with custom registry clients around HelmSharp repository workflows.

## Current boundaries

Full Helm OCI authentication parity is planned work and is not a 1.2.0 guarantee.
