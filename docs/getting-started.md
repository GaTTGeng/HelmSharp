# Getting Started

This 10-minute path helps you decide whether your application only needs rendered manifests or needs full release workflows.

## 1. Install the smallest package set

Render-only preview tools:

```powershell
dotnet add package HelmSharp.Chart --version 1.1.1
dotnet add package HelmSharp.Engine --version 1.1.1
```

Release workflows:

```powershell
dotnet add package HelmSharp.Action --version 1.1.1
```

## 2. Render without Helm CLI

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#render-first-chart{csharp}

This path never shells out to `helm` and does not mutate a cluster.

## 3. Move to dry-run release workflows

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#dry-run-release{csharp}

Keep `DryRun = true` until your application has an explicit approval step.

## 4. Choose your next page

| Need | Read |
| --- | --- |
| Install details | [Installation](guide/installation.md) |
| Values precedence | [Values](guide/values.md) |
| Capabilities and NOTES | [Template Rendering](guide/template-rendering.md) |
| Install/upgrade behavior | [Release Workflows](guide/release-workflows.md) |
| Real examples | [Examples](examples/render-preview-api.md) |
| Member-level reference | [API Reference](api/index.md) |

## Current compatibility baseline

Before relying on an edge Helm behavior in production, review [Helm Compatibility](helm-compatibility.md) for the current golden-test coverage, known boundaries, and reporting guidance.
