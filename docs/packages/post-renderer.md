# HelmSharp.PostRenderer

`HelmSharp.PostRenderer` exposes `IPostRenderer`, a contract for transforming rendered manifest text after template evaluation and before the next workflow step.

```powershell
dotnet add package HelmSharp.PostRenderer --version 1.3.1
```

Use a post-renderer for product-owned transformations such as policy labels, annotations, or deterministic normalization. Keep it side-effect free and test it against representative rendered YAML. Put chart-specific behavior in chart templates instead; a post-renderer should not become a second hidden templating language.

For the interface signature, see the [generated post-renderer API](../api/generated/postrenderer.md).
