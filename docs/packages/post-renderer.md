# HelmSharp.PostRenderer

## Package responsibility

`HelmSharp.PostRenderer` defines the post-renderer extension contract used to transform rendered manifests after template rendering and before apply.

## When to install

Install directly when building a custom post-rendering integration:

```powershell
dotnet add package HelmSharp.PostRenderer --version 1.2.0
```

## Dependencies

This package is intentionally small and has no Kubernetes dependency.

## Main types

| Type | Use it for |
| --- | --- |
| `IPostRenderer` | Transform rendered YAML before the next workflow step. |

## Common combinations

Use a post-renderer for policy injection, labels, annotations, or manifest normalization that should happen outside chart templates.

## Current boundaries

Post-renderer execution is an extension point. Keep transformations deterministic and test them against representative chart output.
