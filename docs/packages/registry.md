# HelmSharp.Registry

`HelmSharp.Registry` contains extension contracts for OCI registry work. Most applications should not install it directly; it is already referenced by the higher-level packages.

```powershell
dotnet add package HelmSharp.Registry --version 1.3.1
```

`IOciRegistryClient` is an integration seam, not a promise of complete Helm OCI behavior. Do not use this package as evidence that OCI authentication, push, pull, provenance, or credential parity is available. Check the [compatibility boundary](../helm-compatibility.md) and the [generated Registry API](../api/generated/registry.md) before building an integration.
