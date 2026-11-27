# HelmSharp.Registry

`HelmSharp.Registry` contains extension contracts for OCI registry work. Most applications should not install it directly; it is already referenced by the higher-level packages.

```powershell
dotnet add package HelmSharp.Registry --version 2.0.1
```

`IOciRegistryClient` is an integration seam, not a promise of complete Helm OCI behavior. Do not use this package as evidence that OCI authentication, push, pull, or credential parity is available. Check the [compatibility boundary](../helm-compatibility.md) and the [generated Registry API](../api/generated/registry.md) before building an integration.

The existing repository pull entry point validates OCI references before sending a registry request. It accepts `oci://host[:port]/repository/chart`, optionally followed by an OCI tag. Repository components must be lowercase; tags use OCI tag characters. A separate version converts SemVer build metadata's `+` to `_`; if a tag is present, the version must match it literally. References with a lowercase `sha256` or `sha512` digest are parsed but rejected by the pull entry point until artifact validation is supported. This validation does not establish full registry pull support or artifact verification.

## Registry credential resolution

Use `RegistryCredentialResolver.ResolveAsync` with a hostname or bracketed IPv6 address and an optional port. The resolver lowercases DNS names, uses their ASCII IDN representation, and normalizes IP addresses. Explicit ports remain part of the scope, including port 443. Schemes, repository paths, user info, queries, and fragments are invalid as registry arguments.

The resolver selects the first applicable source in this order.

1. A `RegistryCredential` explicitly supplied for the operation.
2. `HELMSHARP_REGISTRY_USERNAME` and `HELMSHARP_REGISTRY_PASSWORD` when `HELMSHARP_REGISTRY_HOST` matches the registry. Both values are required for that host. Credentials without a host binding are ignored.
3. An explicitly selected Helm config file, or the file named by `HELM_REGISTRY_CONFIG` when no path is supplied. The supported subset is `auths` with base64-encoded UTF-8 `username:password` values in `auth`. Keys may be authorities or HTTPS URLs with a root path. Duplicate keys for the same normalized registry are rejected.
4. The optional `IRegistryCredentialProvider`, which receives the exact normalized authority and returns a credential for that authority or null.

A missing file or entry allows the next source to run. An applicable `credHelpers` entry or a configured `credsStore` fails explicitly. Token fields and username/password fields without `auth` are unsupported. The resolver reads credentials and never writes them. Its diagnostics omit input values and paths. Custom providers should redact secrets in their own diagnostics.

TLS validation, custom CAs, client certificates, and proxy settings belong to the registry transport. Logout removes the exact authority from the writable store and leaves explicit, environment, and read-only Helm config credentials available. The current resolver is standalone. Action login/logout integration and secure storage are tracked under [#267](https://github.com/GaTTGeng/HelmSharp/issues/267).
