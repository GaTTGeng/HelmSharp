# Roadmap

The roadmap is organized around what users need to trust HelmSharp in a .NET application: render the same chart, explain the same values, and mutate Kubernetes only when the application intentionally asks for it.

GitHub milestones remain the source of truth for issue-level scope.

## Current focus

**M1: Helm Template Parity** is the active workstream. The project now has real-chart golden tests, and the next work is about closing remaining differences in values, built-in objects, template functions, whitespace, `.Files`, capabilities, and subcharts.

Follow [M1 on GitHub](https://github.com/GaTTGeng/HelmSharp/milestone/1) or review the [compatibility page](helm-compatibility.md).

## Delivery principles

- Keep runtime behavior managed in .NET; do not shell out to Helm from consumer applications.
- Use Helm CLI output as a test oracle, not as an SDK dependency.
- Prefer common production chart behavior before rare terminal formatting details.
- Make package boundaries clear so applications can depend on only what they use.
- Treat Kubernetes mutation, credentials, OCI, and provenance as security-sensitive work.

## Milestone plan

| Phase | Status | User outcome |
| --- | --- | --- |
| [M1: Helm Template Parity](https://github.com/GaTTGeng/HelmSharp/milestone/1) | Active | Render common real-world charts from .NET with predictable Helm-compatible output. |
| [M2: Chart Packaging and Repository Parity](https://github.com/GaTTGeng/HelmSharp/milestone/2) | Planned | Package, index, pull, and resolve chart dependencies without CLI handoffs. |
| [M3: Release Lifecycle Parity](https://github.com/GaTTGeng/HelmSharp/milestone/6) | Planned | Install, upgrade, rollback, uninstall, status, history, and hook behavior users can reason about. |
| [M4: Kubernetes Apply and Wait Semantics](https://github.com/GaTTGeng/HelmSharp/milestone/5) | Planned | Correct resource identity, namespace handling, readiness, Jobs, deletion, and hook cleanup. |
| [M5: OCI and Provenance](https://github.com/GaTTGeng/HelmSharp/milestone/4) | Planned | Registry authentication, chart pull/push, signing, and verification. |
| [M6: Public SDK Hardening](https://github.com/GaTTGeng/HelmSharp/milestone/3) | Ongoing | Better docs, examples, nullable correctness, package quality, and API polish. |
| [M7: Compatibility Expansion Research](https://github.com/GaTTGeng/HelmSharp/milestone/7) | Research | Evidence-based decision on `netstandard`, .NET Framework, and longer target support. |

## How work graduates

A compatibility item is ready when:

1. The behavior is captured by a focused unit, integration, or golden test.
2. The compatibility page says what is supported and what remains open.
3. Public API changes have examples from a user point of view.
4. Release builds and tests pass for `net8.0`, `net9.0`, and `net10.0`.

## Contributing

Start with an existing milestone issue when possible. For a newly found Helm difference, open a compatibility issue with a minimal chart, Helm command, HelmSharp API call, and both outputs.
