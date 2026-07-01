# Changelog

All notable changes to HelmSharp will be documented in this file.

This project follows semantic versioning once stable releases begin.

## [Unreleased]

### Added

- Expanded the VitePress documentation into workflow guides, real integration examples, package-by-package pages, and generated API reference indexes in English and Simplified Chinese.
- Added HelmSharp logo and wordmark assets to the READMEs and VitePress documentation site.

### Changed

- Updated GitHub Actions workflow dependencies and bumped YamlDotNet to 18.1.0.
- Reframed public-chart golden results in the VitePress docs and READMEs as compatibility evidence instead of primary marketing copy.
- Polished Simplified Chinese documentation wording to reduce unnecessary English terms while preserving API and product names.

## [1.1.0] - 2026-07-01

### :tada: M1 Complete — Helm Template Parity Achieved

HelmSharp achieves **byte-for-byte identical output** with `helm template` across all five real-world golden test charts (129/129 templates). All five charts now carry the **Pass** verdict:

| Chart | Version | Templates | Full Render | Verdict |
| --- | --- | --- | --- | --- |
| podinfo | 6.14.0 | 21/21 | 52/52 docs exact | **Pass** |
| metrics-server | 3.13.1 | 18/18 | 45/45 docs exact | **Pass** |
| external-dns | 1.21.1 | 7/7 | 37/37 docs exact | **Pass** |
| ingress-nginx | 4.12.1 | 42/42 | 90/90 docs exact | **Pass** |
| cert-manager | 1.17.1 | 41/41 | 52/52 docs exact | **Pass** |

Key M1 parity milestones closed: block right-trim matching Go `text/template` (#109, #111, #113), define body right-trim (#112), complete Sprig function parity (#97), YAML tag/octal/merge key/block scalar handling (#102), golden test normalization (#101, #100), and real-chart content diff resolution (#96, #99, #108).

### Added

- Added managed renderer support for Helm `.Files.Glob`, `.Files.AsConfig`, and `.Files.AsSecrets` helpers.
- Preserved Helm-compatible `.Files.AsConfig` quoted scalar output for multiline and CRLF file content.
- Added a VitePress documentation site and GitHub Pages deployment workflow.
- Added Helm CLI golden coverage for whitespace trimming, `indent`/`nindent`, YAML document separators, and NOTES rendering (#9).
- Added named-template golden coverage for current-dot `include` and `tpl` calls (#84).

### Changed

- Hardened CI, documentation, and NuGet release workflows with PR validation, release tag guards, package caching, and symbol package publishing.
- Bumped `Microsoft.NET.Test.Sdk` from 18.6.0 to 18.7.0.
- Reworked the VitePress documentation around user workflows and added Simplified Chinese localization.
- Updated all documentation to reflect M1 Pass verdicts — golden test results, compatibility page, and AGENTS.md (#14).

### Fixed

- Restored block right-trim behavior and guarded `IsFormattingWhitespace` against newline suppression, restoring cert-manager golden test to 52/52 Pass verdict (#109, #111, #113).
- Applied right-trim to define body in `ParseDefine`, matching `ParseBlock` behavior for `{{- define \"name\" -}}` (#112).
- Matched Helm named template scope for `include`, `template`, `tpl`, and `$` root lookups (#8).
- Matched Helm whitespace behavior for block end trim markers, `nindent` output after `{{- ... }}`, and escaped newlines in quoted template strings (#9).
- Matched Helm failure boundaries for `fail`, missing template functions, required values, and malformed templates (#13).
- Matched Helm/Sprig string slicing behavior for negative `trunc` lengths and `substr` end indexes (#10).
- Render install and upgrade manifests before creating missing namespaces so render failures do not mutate the cluster (#32).
- Render aliased dependency instances independently and merge subchart defaults for every declared alias.
- Patched the docs Vite dependency resolution to clear Dependabot security alerts.

## [1.0.4] - 2026-06-23

### :rocket: AST-Based Template Parsing

This release introduces a **tokenizer and AST-based template parsing pipeline**, replacing the previous regex-driven approach. This is the foundational M1 architecture change that enables accurate Helm template compatibility:

- **Tokenizer** (`HelmTokenizer`): Lexes Helm templates into a structured token stream (text, `{{`, `}}`, `|`, `:=`, identifiers, strings, numbers, operators, parentheses, etc.), preserving source positions for diagnostics.
- **AST** (`AstBuilder`): Parses the token stream into an abstract syntax tree with nodes for `TemplateNode`, `TextNode`, `ActionNode`, `PipelineNode`, `CommandNode`, `FieldNode`, `IfNode`, `RangeNode`, `WithNode`, `DefineNode`, and `IncludeNode` — faithfully modeling Helm's template grammar.
- **Malformed template error handling** (`#62`): The AST parser now produces structured `TemplateParseException` errors for malformed templates instead of cryptic low-level failures, with source location information for debugging.

### :building_construction: Parse–Evaluation Layer Boundary

- **`#57`** — Defined a clean separation between the parsing layer (tokenizer + AST) and the evaluation layer (rendering). Decomposed `HelmTemplateRenderer` into focused components: `TemplateParser` (orchestrates tokenize → AST), `TemplateEvaluator` (walks AST nodes), and `HelmTemplateRenderer` (public API facade). This boundary enables future optimizations like AST caching and incremental re-rendering.

### Fixed

- **#55** — Chart values now follow Helm's precedence rules (user-supplied `--values` files override `values.yaml`; later files override earlier ones). Multiple values files are now supported.
- **#54** — `DefaultApiVersions` now adapts to `kubeVersion` in `Capabilities` (fixes `#49`). API versions are filtered based on the target Kubernetes version.
- **#64** — `Render()` now collects `TemplateParseException` per-template, so a single broken template doesn't abort the entire chart render.
- **#65** — `IncludeTemplate` now preserves `TemplateParseException` for structured per-template error reporting.
- **#59** — `ExtractQuotedFirstArg` now requires quoted define names, aligning with Helm's `define`/`template` syntax.
- **#66** — Removed dead `DefineRegex` static field.

### Changed

- `HelmTemplateRenderer` decomposed into `TemplateParser` + `TemplateEvaluator` with a clean public API surface.
- Token positions tracked throughout the tokenizer for error diagnostics.

### Changed

- Synced golden test results to `README.zh-CN.md`.

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

[Unreleased]: https://github.com/GaTTGeng/HelmSharp/compare/1.1.0...HEAD
[1.1.0]: https://github.com/GaTTGeng/HelmSharp/compare/1.0.4...1.1.0
[1.0.4]: https://github.com/GaTTGeng/HelmSharp/compare/1.0.3...1.0.4
[1.0.3]: https://github.com/GaTTGeng/HelmSharp/compare/1.0.2...1.0.3
[1.0.2]: https://github.com/GaTTGeng/HelmSharp/compare/1.0.1...1.0.2
[1.0.1]: https://github.com/GaTTGeng/HelmSharp/releases/tag/1.0.1
