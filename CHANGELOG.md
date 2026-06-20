# Changelog

All notable changes to HelmSharp will be documented in this file.

This project follows semantic versioning once stable releases begin.

## [Unreleased]

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
