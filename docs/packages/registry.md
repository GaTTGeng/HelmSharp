# HelmSharp.Registry

`HelmSharp.Registry` contains extension contracts for OCI registry work. Most applications should not install it directly; it is already referenced by the higher-level packages.

```powershell
dotnet add package HelmSharp.Registry --version 2.0.1
```

`IOciRegistryClient` is an integration seam, not a promise of complete Helm OCI behavior. Do not use this package as evidence that OCI authentication, push, pull, or credential parity is available. Check the [compatibility boundary](../helm-compatibility.md) and the [generated Registry API](../api/generated/registry.md) before building an integration.

The existing repository pull entry point validates OCI references before sending a registry request. It accepts `oci://host[:port]/repository/chart`, optionally followed by an OCI tag. Repository components must be lowercase; tags use OCI tag characters. A separate version converts SemVer build metadata's `+` to `_`; if a tag is present, the version must match it literally. References with a lowercase `sha256` or `sha512` digest are parsed but rejected by the pull entry point until artifact validation is supported. This validation does not establish full registry pull support or artifact verification.
