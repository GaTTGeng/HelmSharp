# HelmSharp Roadmap

HelmSharp is evolving toward dependable Helm-compatible behavior for applications that need chart rendering and release workflows inside a .NET process.

> The roadmap describes direction, not a release guarantee. GitHub milestones are the source of truth for current scope and issue progress.

## Current Focus

**M1: Helm Template Parity** is the active workstream. The immediate goal is a repeatable compatibility harness that compares selected HelmSharp output with `helm template`, followed by focused fixes for values, built-in objects, template functions, whitespace, and subcharts.

Track the live work in [M1 on GitHub](https://github.com/GaTTGeng/HelmSharp/milestone/1) or review the detailed [compatibility matrix](helm-compatibility.md).

## Delivery Principles

- Prefer managed .NET behavior over shelling out to the Helm CLI at runtime.
- Measure compatibility with small, reproducible fixtures and Helm CLI reference output.
- Prioritize common production workflows before rare CLI formatting details.
- Keep public APIs stable, documented, and independently useful as SDK abstractions.
- Treat Kubernetes mutation, authentication, and provenance as security-sensitive work.

## Milestone Plan

| Phase | Status | Outcome |
| --- | --- | --- |
| [M1: Helm Template Parity](https://github.com/GaTTGeng/HelmSharp/milestone/1) | **Active** | Reliable `helm template` behavior for values, built-in objects, common functions, subcharts, and rendered output. |
| [M2: Chart Packaging and Repository Parity](https://github.com/GaTTGeng/HelmSharp/milestone/2) | Planned | Predictable package archives, repository indexes, pulls, and dependency workflows. |
| [M3: Release Lifecycle Parity](https://github.com/GaTTGeng/HelmSharp/milestone/6) | Planned | Consistent install, upgrade, rollback, uninstall, status, history, hooks, and release state transitions. |
| [M4: Kubernetes Apply and Wait Semantics](https://github.com/GaTTGeng/HelmSharp/milestone/5) | Planned | Correct resource identity, namespace handling, readiness, Jobs, deletion, and hook cleanup behavior. |
| [M5: OCI and Provenance](https://github.com/GaTTGeng/HelmSharp/milestone/4) | Planned | OCI authentication, chart pull/push, signing, and provenance verification. |
| [M6: Public SDK Hardening](https://github.com/GaTTGeng/HelmSharp/milestone/3) | Ongoing | Stronger API documentation, examples, analyzers, nullable correctness, and package quality. |
| [M7: Compatibility Expansion Research](https://github.com/GaTTGeng/HelmSharp/milestone/7) | Research | Evidence-based decision on `netstandard`, .NET Framework, and longer-term target frameworks. |

## How Work Advances

A compatibility item is ready to leave a milestone when:

1. The intended behavior is captured by a focused unit, integration, or golden test.
2. Supported behavior and remaining gaps are reflected in the compatibility matrix.
3. Public API changes include appropriate documentation and examples.
4. Release builds and tests pass for `net8.0`, `net9.0`, and `net10.0`.

Milestones may overlap where the behavior crosses subsystem boundaries. For example, install parity depends on both release lifecycle behavior in M3 and Kubernetes wait semantics in M4.

## Contributing

Start with an existing milestone issue where possible. For a newly discovered Helm difference, [open a compatibility issue](https://github.com/GaTTGeng/HelmSharp/issues/new) and include a minimal chart, the exact Helm command, HelmSharp API usage, and both outputs.
