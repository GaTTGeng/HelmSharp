# Helm Compatibility

HelmSharp aims to implement a practical Helm-compatible subset for .NET applications. It is not a byte-for-byte replacement for the Helm CLI.

## Supported areas

| Area | Status |
| --- | --- |
| Chart loading from directories and `.tgz` archives | Supported |
| `values.yaml` and `--set`-style overrides | Supported |
| Common Helm template control flow and functions | Partially supported |
| Chart package creation | Supported |
| Repository index, pull, and search helpers | Supported |
| Kubernetes apply/delete/wait helpers | Supported for common resource workflows |
| Release history in Kubernetes Secrets | Supported |
| Install, upgrade, uninstall, rollback, status, history, manifest, values, hooks, notes | Supported through managed APIs |

## Known gaps

- Full Sprig and Helm template function parity is still in progress.
- Plugin execution is intentionally not implemented as Helm CLI plugin loading.
- OCI authentication and registry flows are extension points first and will need focused parity work.
- Provenance verification is not complete Helm CLI parity.
- Less common Kubernetes resource wait semantics may need additional implementation.

Use focused tests when adding compatibility so behavior can be compared with Helm CLI output.
