# Helm Compatibility

HelmSharp is not trying to be a command-line emulator. It is trying to give .NET applications the Helm behavior they need when rendering charts and managing releases from inside a process.

The Helm CLI is used as a test oracle. It is not required at runtime by consumers.

## Current test signal

Compatibility data is a test signal, not a universal guarantee for every chart. The current public-chart golden suite covers five pinned charts and is validated by CI against a real `helm` binary:

| Chart | Version | Templates | Result |
| --- | --- | --- | --- |
| podinfo | 6.14.0 | 21/21 | Pass |
| metrics-server | 3.13.1 | 18/18 | Pass |
| external-dns | 1.21.1 | 7/7 | Pass |
| ingress-nginx | 4.12.1 | 42/42 | Pass |
| cert-manager | 1.17.1 | 41/41 | Pass |
| **Total** | - | **129/129** | **Pass** |

These charts are useful because they expose helper templates, nested values, `.Files`, capabilities, and formatting patterns that small examples miss. Treat the table as evidence for the covered cases, then validate your own chart when it relies on uncommon Helm behavior. See the [Compatibility Validation](https://github.com/GaTTGeng/HelmSharp#compatibility-validation) section in the README for the detailed breakdown and verdict definitions.

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
| Chart loading from directories and `.tgz` archives | Supported | Safe starting point for render and packaging tools. Covered by fixture and public-chart tests. |
| Values files and `--set`-style overrides | Partial | Common flows work; edge-case coercion and list syntax still tracked by golden tests. |
| Helm-style template rendering | Supported | Common parsing, control-flow, named templates, built-in objects, whitespace/trim markers, and Sprig functions are covered by focused tests and the current public-chart suite. |
| Template control flow (`if`/`else if`/`else`/`range`/`with`) | Supported | All control-flow constructs render correctly against `helm template` output, validated by dedicated fixture charts and real-chart golden tests. |
| Named templates and helpers | Supported | Cross-template `define`/`template`/`include` calls are covered by dedicated fixtures and public charts with extensive helper usage. |
| Built-in objects (`.Release`, `.Chart`, `.Values`, `.Files`, `.Capabilities`, `.Template`) | Supported | All built-in objects are populated and render consistently with Helm CLI output. |
| Chart packaging and repositories | Partial | Useful APIs exist; archive and repository edge cases remain. |
| Install, upgrade, rollback, uninstall | Partial | Dry-run and managed workflows exist; full lifecycle parity is still expanding. |
| Kubernetes apply, delete, wait | Partial | Common resource operations exist; less common readiness behavior needs coverage. |
| Release history in Kubernetes Secrets | Supported | Release records can be persisted without Helm CLI. |
| OCI registry and provenance | Planned | API surface exists or is planned; production parity is not complete. |

## Known boundaries

These are the areas to check before betting a production workflow on exact Helm behavior:

- obscure values coercion and edge-case list syntax;
- OCI authentication and registry flows;
- provenance verification;
- readiness for uncommon Kubernetes resource kinds;
- safe replacement for Helm plugin execution.

The current golden tests do not show known gaps in Sprig function coverage or manifest formatting for the covered charts. New public charts can still surface edge cases, so compatibility reports should include a minimal chart and the equivalent Helm CLI command.

## How to report a gap

Open a compatibility issue with:

- Helm CLI and HelmSharp versions;
- a minimal chart and values input;
- the exact Helm command and output;
- the equivalent HelmSharp API call and output;
- whether the difference affects rendering, release state, or cluster mutation.

Small reproducible charts are more useful than screenshots or large private charts.

## Continuous validation

Golden test results are validated on every push and pull request through the [CI workflow](https://github.com/GaTTGeng/HelmSharp/blob/master/.github/workflows/ci.yml). The CI runner installs Helm CLI (`v4.2.2`) alongside the .NET SDKs and executes the full golden test suite, including both fixture-chart and real-chart comparisons. JSON reports are published as workflow artifacts for each run.
