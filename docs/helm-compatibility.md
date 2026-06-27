# Helm Compatibility

HelmSharp is not trying to be a command-line emulator. It is trying to give .NET applications the Helm behavior they need when rendering charts and managing releases from inside a process.

The Helm CLI is used as a test oracle. It is not required at runtime by consumers.

## Current confidence level

The current real-chart golden suite renders **129/129 templates** across five public charts with no parser exceptions:

| Chart | Version | Templates | Result |
| --- | --- | --- | --- |
| podinfo | 6.14.0 | 21/21 | Pass |
| metrics-server | 3.13.1 | 18/18 | Pass |
| external-dns | 1.21.1 | 7/7 | Pass |
| ingress-nginx | 4.12.1 | 42/42 | Pass |
| cert-manager | 1.17.1 | 41/41 | Pass |
| **Total** | - | **129/129** | **Pass** |

That number matters because real charts expose helper templates, nested values, `.Files`, capabilities, and formatting patterns that small examples miss.

## Compatibility contract

A behavior is treated as supported when it:

- is reachable through a documented managed API;
- has focused automated coverage;
- behaves consistently across `net8.0`, `net9.0`, and `net10.0`;
- matches Helm where the rendered output, release state, or failure behavior is observable by users.

Exact CLI colors, progress text, terminal formatting, and plugin execution are not compatibility goals unless they affect chart output or automation.

## Capability snapshot

| Area | Current level | What it means for users |
| --- | --- | --- |
| Chart loading from directories and `.tgz` archives | Supported | Safe starting point for render and packaging tools. |
| Values files and `--set`-style overrides | Partial | Common flows work; edge-case coercion still needs parity work. |
| Helm-style template rendering | Partial | Real public charts render; remaining gaps are tracked by golden tests. |
| Chart packaging and repositories | Partial | Useful APIs exist; archive and repository edge cases remain. |
| Install, upgrade, rollback, uninstall | Partial | Dry-run and managed workflows exist; full lifecycle parity is still expanding. |
| Kubernetes apply, delete, wait | Partial | Common resource operations exist; less common readiness behavior needs coverage. |
| Release history in Kubernetes Secrets | Supported | Release records can be persisted without Helm CLI. |
| OCI registry and provenance | Planned | API surface exists or is planned; production parity is not complete. |

## Known boundaries

These are the areas to check before betting a production workflow on exact Helm behavior:

- full Sprig function parity;
- obscure values coercion and list syntax cases;
- byte-for-byte manifest formatting;
- OCI authentication and registry flows;
- provenance verification;
- readiness for uncommon Kubernetes resource kinds;
- safe replacement for Helm plugin execution.

## How to report a gap

Open a compatibility issue with:

- Helm CLI and HelmSharp versions;
- a minimal chart and values input;
- the exact Helm command and output;
- the equivalent HelmSharp API call and output;
- whether the difference affects rendering, release state, or cluster mutation.

Small reproducible charts are more useful than screenshots or large private charts.
