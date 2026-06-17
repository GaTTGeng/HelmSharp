# Roadmap

This roadmap tracks areas that would make HelmSharp more complete as an open source SDK.

## Near term

- Expand examples for repository operations, chart packaging, and Kubernetes dry-run workflows.
- Add a Helm parity test corpus that compares selected chart output against Helm CLI.
- Replace extension point placeholders with stable contracts and implementations where appropriate.
- Improve XML documentation on public APIs.
- Add coverage reporting to CI.

## Compatibility exploration

- Evaluate whether a `netstandard` target is worth the additional API and dependency constraints.
- Keep `net8.0` as the modern LTS baseline for the main packages unless a strong consumer need justifies older targets.

## Helm parity

- Track command-by-command parity in `docs/helm-compatibility.md`.
- Prioritize common install, upgrade, template, package, repository, and values workflows before edge-case CLI behavior.
