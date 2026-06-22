# Changelog

All notable changes to HelmSharp will be documented in this file.

This project follows semantic versioning once stable releases begin.

## [Unreleased]

## [1.0.3] - 2026-06-22

### :tada: Golden Test Milestone — 129/129 Templates Render Successfully

HelmSharp now renders **all 129 templates across 5 real-world public Helm charts without a single parser exception**:

| Chart | Version | Templates | Result |
| --- | --- | --- | --- |
| **ingress-nginx** | 4.12.1 | 42/42 | :white_check_mark: |
| **cert-manager** | 1.17.1 | 41/41 | :white_check_mark: |
| **external-dns** | 1.21.1 | 7/7 | :white_check_mark: |
| **podinfo** | 6.14.0 | 21/21 | :white_check_mark: |
| **metrics-server** | 3.13.1 | 18/18 | :white_check_mark: |
| **Total** | — | **129/129 (100%)** | :white_check_mark: |

This was achieved by fixing two parser bugs that blocked full-chart rendering (#50, #51). Output is structurally comparable to `helm template` — remaining differences are at the YAML formatting and value-evaluation level, not the parser level. A new golden test harness (`RealChartGoldenTests`) now runs as part of CI to prevent regressions.

### Added

- Real-chart golden test harness comparing HelmSharp against `helm template` for 5 public charts.
- Golden test results and per-chart breakdown in README.

### Fixed

- **#51** — `SplitByTopLevel` now tracks parentheses depth so `|` inside `(...)` is not treated as a pipeline separator. Fixes `(empty .x)` and `($value | quote | len)` patterns.
- **#50** — else-if chain reconstruction: C# string interpolation `{{- end }}` was producing `{- end }` (single braces) instead of `{{- end }}` (double braces), causing `RenderSection` to fail finding matching end tokens. Also now includes full remaining content rather than stopping at the first `else`/`end`, preserving multi-branch chains.
- Per-template variable isolation: each template now receives a fresh `Variables` dictionary.
- Removed dead code `ExtractUntilElseOrEnd`.
- `SplitByTopLevel` quote toggling now guarded by `parenDepth == 0` for consistency with `SplitArguments`.

### Changed

- `Render()` accumulates `NotSupportedException` per-template rather than failing on the first one.
- `IncludeTemplate` and `RenderSection` errors now include template name and block expression for diagnostics.
- `GoldenResult.ToJson()` uses `System.Text.Json` serialization.

## [1.0.2] - 2026-06-20

### Added

- Example projects for rendering charts and dry-run release workflows.
- Open source SDK readiness documentation, including getting started, API overview, Helm compatibility, and roadmap pages.
- GitHub issue templates for bug reports, feature requests, and Helm compatibility gaps.
- README badges, NuGet package links, support policy, CODEOWNERS, and Dependabot configuration.
- Helm CLI golden test harness and baseline parity fixture charts.
- Built-in template objects aligned with Helm: `.Chart`, `.Release`, `.Capabilities`, `.Files`, `.Template`.
- Support for `.Capabilities.APIVersions.Has` method calls, including on variables.
- Support for dependency aliases, subchart identity, and chart file binary access.

### Changed

- Replaced placeholder extension point classes with named public interfaces (`IPostRenderer`, `IOciRegistryClient`, `IHelmReleaseStore`).
- Expanded NuGet package metadata with repository, project URL, XML docs, symbols, and SourceLink settings.
- Chart loader now reads files as `byte[]` internally for correct binary file handling.
- Refactored range loop iteration to use `CreateRangeContext` helper for consistency.
- `HelmHookExecutor` Try-parse methods no longer use nullable enum out parameters.

### Fixed

- `ApiVersionSet` constructor now validates non-null input (#42).
- `ApiVersionSet` defensively copies the input list (#43).
- `.Capabilities.APIVersions.Has` now errors on arity mismatch (#40).
- `.Capabilities.APIVersions.Has` counts piped nil value in arity check (#45).
- Removed dead-code ternary in release status assignment.

## [1.0.1] - 2026-06-17

### Added

- Initial managed Helm-style chart rendering and Kubernetes release workflow implementation.
- Open source project documentation.
- GitHub Actions workflows for CI and NuGet release publishing.
