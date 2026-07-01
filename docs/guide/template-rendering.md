# Template Rendering

## What problem this solves

Template rendering turns a loaded chart and merged values into Kubernetes manifests. HelmSharp uses Helm CLI output as a compatibility oracle in tests, while application code calls the managed renderer directly.

## Packages to install

```powershell
dotnet add package HelmSharp.Chart --version 1.1.0
dotnet add package HelmSharp.Engine --version 1.1.0
```

## Minimal complete code

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#template-with-capabilities{csharp}

## Why these APIs

`HelmTemplateRenderer` exposes `.Release`, `.Chart`, `.Values`, `.Capabilities`, `.Files`, `.Template`, named templates, `include`, `tpl`, and common Helm/Sprig functions to templates. `kubeVersion` and `apiVersions` let you preview output for a target cluster instead of the machine running the renderer.

## Production notes

- Use explicit `kubeVersion` and `apiVersions` when previewing manifests for a known cluster.
- Render NOTES separately with `RenderNotes()` when the output is user-facing.
- Include CRDs through `HelmClient.TemplateAsync` with `IncludeCRDs = true` when you want command-like output.

## Next step

Move to [Release Workflows](release-workflows.md) if you need install, upgrade, history, or rollback behavior.
