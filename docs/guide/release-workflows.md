# Release Workflows

## What problem this solves

Release workflows combine rendering, Kubernetes apply/delete/wait, hooks, and release history. Use this path when your application owns a deployment action, not just a preview.

## Packages to install

```powershell
dotnet add package HelmSharp.Action --version 1.3.1
```

## Minimal complete code

Start with a dry run:

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#dry-run-release{csharp}

Apply only after approval:

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#apply-release{csharp}

## Why these APIs

`HelmClient.UpgradeInstallAsync` is the primary install/upgrade entry point. It loads the chart, merges values, renders manifests, applies CRDs when needed, executes hooks unless disabled, waits for readiness when requested, and saves release history.

## Production notes

- Keep `DryRun = true` in preview flows and switch to `false` only in the approved apply step.
- For non-dry-run operations, `Install = false` makes a missing release fail instead of silently creating it. Use it when an endpoint must be upgrade-only; dry runs do not look up stored releases.
- `ReuseValues = true` starts an upgrade from the stored release values, then overlays the supplied values. The default and `ResetValues = true` start from chart defaults. `ReuseValues` and `ResetValues` cannot be combined.
- `TimeoutSeconds` covers Kubernetes apply, hooks, readiness waiting, and cancellation. `Atomic` implies readiness waiting; use `WaitForJobs` only with `Wait` (or `Atomic`).
- `Description`, `Labels`, and `MaxHistory` are stored with the resulting revision. `RollbackAsync(new HelmRollbackRequest { ... })` exposes the same timeout, wait, hook, description, label, and history controls for a rollback while retaining the original overload.
- Each non-dry-run lifecycle attempt that reaches release persistence leaves durable Kubernetes Secret evidence; preflight and dry-run requests do not create a revision. Successful upgrade and rollback transitions supersede the prior deployed revision; failed install, upgrade, and rollback attempts retain a failed revision for inspection. A retained uninstall adds an `uninstalled` revision, while the default uninstall purges history.
- Hooks run by weight and then name. Job and Pod hooks wait for completion within `TimeoutSeconds`; other hook resource kinds are applied without a completion observer. `before-hook-creation`, `hook-succeeded`, and `hook-failed` cleanup policies are supported, and `GetHooksAsync` shows each stored hook's latest run state.
- Options without a managed implementation, such as `Force`, ownership takeover, repository TLS/authentication, provenance verification, or server-side apply selection, fail with a clear diagnostic before any cluster mutation.
- Capture `CommandResult.StandardError` and `ExitCode` in product logs.

## Next step

Read [Kubernetes Operations](kubernetes-operations.md) for lower-level apply/delete/wait behavior.
