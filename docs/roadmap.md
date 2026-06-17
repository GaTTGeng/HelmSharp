# Roadmap

This roadmap tracks areas that would make HelmSharp more complete as an open source SDK.

## Near term

- Expand examples for repository operations, chart packaging, and Kubernetes dry-run workflows.
- Add a Helm parity test corpus that compares selected chart output against Helm CLI.
- Replace extension point placeholders with stable contracts and implementations where appropriate.
- Improve XML documentation on public APIs.
- Add coverage reporting to CI.

## GitHub milestones

HelmSharp tracks larger workstreams through GitHub milestones:

| Milestone | Purpose |
| --- | --- |
| [M1: Helm Template Parity](https://github.com/MattGRP/HelmSharp/milestone/1) | Establish reliable `helm template` compatibility for values, objects, common functions, subcharts, and output comparison tests. |
| [M2: Chart Packaging and Repository Parity](https://github.com/MattGRP/HelmSharp/milestone/2) | Harden chart package, repository index, pull, and dependency workflows. |
| [M3: Release Lifecycle Parity](https://github.com/MattGRP/HelmSharp/milestone/6) | Improve install, upgrade, rollback, uninstall, status, history, get, hook, and release Secret behavior. |
| [M4: Kubernetes Apply and Wait Semantics](https://github.com/MattGRP/HelmSharp/milestone/5) | Improve resource identity, namespace handling, apply/delete/wait behavior, Jobs, hooks cleanup, and readiness semantics. |
| [M5: OCI and Provenance](https://github.com/MattGRP/HelmSharp/milestone/4) | Add OCI registry flows, signing, provenance verification, and authentication support. |
| [M6: Public SDK Hardening](https://github.com/MattGRP/HelmSharp/milestone/3) | Improve API docs, examples, docs site readiness, warnings, nullable cleanup, and compatibility matrix. |
| [M7: Compatibility Expansion Research](https://github.com/MattGRP/HelmSharp/milestone/7) | Evaluate `netstandard`, .NET Framework, dependency constraints, and long-term target framework strategy. |

## Compatibility exploration

- Evaluate whether a `netstandard` target is worth the additional API and dependency constraints.
- Keep `net8.0` as the modern LTS baseline for the main packages unless a strong consumer need justifies older targets.

## Helm parity

- Track command-by-command parity in `docs/helm-compatibility.md`.
- Prioritize common install, upgrade, template, package, repository, and values workflows before edge-case CLI behavior.
