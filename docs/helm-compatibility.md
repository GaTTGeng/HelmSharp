# Helm Compatibility

HelmSharp is not trying to be a command-line emulator. It is trying to give .NET applications the Helm behavior they need when rendering charts and managing releases from inside a process.

The Helm CLI is used as a test oracle. It is not required at runtime by consumers.

## Current confidence level

The real-chart golden suite renders **129/129 templates** across five public Helm charts with no parser exceptions. These results are validated on every push and pull request by the CI workflow, which runs identical golden tests against a real `helm` binary.

| Chart | Version | Templates | Result |
| --- | --- | --- | --- |
| podinfo | 6.14.0 | 21/21 | Pass |
| metrics-server | 3.13.1 | 18/18 | Pass |
| external-dns | 1.21.1 | 7/7 | Pass |
| ingress-nginx | 4.12.1 | 42/42 | Pass |
| cert-manager | 1.17.1 | 41/41 | Pass |
| **Total** | - | **129/129** | **Pass** |

That number matters because real charts expose helper templates, nested values, `.Files`, capabilities, and formatting patterns that small examples miss. See the [Golden Test Results](../README.md#golden-test-results) section in the README for per-chart breakdowns, error analysis, and verdict definitions.

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
| Chart loading from directories and `.tgz` archives | Supported | Safe starting point for render and packaging tools. Validated across five real-world charts (podinfo, metrics-server, external-dns, ingress-nginx, cert-manager). |
| Values files and `--set`-style overrides | Partial | Common flows work; edge-case coercion and list syntax still tracked by golden tests. |
| Helm-style template rendering | Partial | All 129 templates across five real public charts render without parser exceptions; remaining gaps are content-level formatting diffs tracked in the README golden test breakdown. |
| Template control flow (`if`/`else if`/`else`/`range`/`with`) | Supported | All control-flow constructs render correctly against `helm template` output, validated by dedicated fixture charts and real-chart golden tests. |
| Named templates and helpers | Supported | Cross-template `define`/`template`/`include` calls resolve correctly; validated across real charts with extensive helper usage (ingress-nginx: 42 templates). |
| Built-in objects (`.Release`, `.Chart`, `.Values`, `.Files`, `.Capabilities`, `.Template`) | Supported | All built-in objects are populated and render consistently with Helm CLI output. |
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

## Continuous validation

Golden test results are validated on every push and pull request through the [CI workflow](../.github/workflows/ci.yml). The CI runner installs Helm CLI (`v3.12.3`) alongside the .NET SDKs and executes the full golden test suite, including both fixture-chart and real-chart comparisons. JSON reports are published as workflow artifacts for each run.
