# Helm Compatibility

HelmSharp is not trying to be a command-line emulator. It is trying to give .NET applications the Helm behavior they need when rendering charts and managing releases from inside a process.

The Helm CLI is used as a test oracle. It is not required at runtime by consumers.

## Validation scope

Focused fixture charts and selected public charts are compared with `helm template` in CI. They exercise helpers, nested values, `.Files`, capabilities, and formatting patterns that small examples miss. This is regression coverage for those behaviors, not a universal compatibility certification. Validate your own chart when it relies on an uncommon Helm behavior.

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
| Helm-style template rendering | Supported | Common parsing, control flow, named templates, built-in objects, whitespace/trim markers, and selected Sprig functions have focused coverage. An unimplemented function fails with a path-aware rendering diagnostic. |
| Template control flow (`if`/`else if`/`else`/`range`/`with`) | Supported | All control-flow constructs render correctly against `helm template` output, validated by dedicated fixture charts and real-chart golden tests. |
| Named templates and helpers | Supported | Cross-template `define`/`template`/`include` calls are covered by dedicated fixtures and public charts with extensive helper usage. |
| Built-in objects (`.Release`, `.Chart`, `.Values`, `.Files`, `.Capabilities`, `.Template`) | Supported | All built-in objects are populated and render consistently with Helm CLI output. |
| Chart packaging and repositories | Partial | Useful APIs exist; archive and repository edge cases remain. |
| Install, upgrade, rollback, uninstall | Partial | Dry-run and managed workflows exist; full lifecycle parity is still expanding. |
| Kubernetes apply, delete, wait | Partial | Typed operations cover common resources. Other API resources are discovered from the target cluster for apply/delete; readiness behavior remains selective. |
| Release history in Kubernetes Secrets | Supported | Release records can be persisted without Helm CLI. |
| OCI registry and provenance | Planned | API surface exists or is planned; production parity is not complete. |

## Known boundaries

These are the areas to check before betting a production workflow on exact Helm behavior:

- obscure values coercion and edge-case list syntax;
- OCI authentication and registry flows;
- provenance verification;
- Sprig functions or Go-template behaviors outside the implemented surface;
- readiness for uncommon Kubernetes resource kinds;
- safe replacement for Helm plugin execution.

Golden tests demonstrate only the behavior they exercise. New charts can surface unimplemented functions, parsing edge cases, or manifest differences, so compatibility reports should include a minimal chart and the equivalent Helm CLI command.

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
