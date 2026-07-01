# Release Workflows

## What problem this solves

Release workflows combine rendering, Kubernetes apply/delete/wait, hooks, and release history. Use this path when your application owns a deployment action, not just a preview.

## Packages to install

```powershell
dotnet add package HelmSharp.Action --version 1.1.0
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
- Set `TimeoutSeconds`, `Wait`, and `WaitForJobs` explicitly so UI and API timeouts match deployment semantics.
- Capture `CommandResult.StandardError` and `ExitCode` in product logs.

## Next step

Read [Kubernetes Operations](kubernetes-operations.md) for lower-level apply/delete/wait behavior.
