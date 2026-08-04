# HelmSharp.Engine

`HelmSharp.Engine` evaluates Helm-style templates in managed code. It is the package for applications that need manifest text but do not need a Kubernetes lifecycle client.

```powershell
dotnet add package HelmSharp.Engine --version 1.3.1
```

Install `HelmSharp.Chart` with it. The usual entry point is `HelmTemplateRenderer`: give it a loaded chart, a merged values dictionary, release identity, and optional Kubernetes capabilities, then call `Render()` or `RenderNotes()`.

| Type | Use |
| --- | --- |
| `HelmTemplateRenderer` | Evaluate templates and return manifests or notes. |
| `TemplateParseException` | Diagnose malformed template input. |

`TemplateContext`, `ApiVersionSet`, and the `Functions` and `Utilities` namespaces are internal renderer implementation details. They are not supported public APIs and do not appear in the generated reference. Use the renderer, and check the [template-function matrix](../template-function-compatibility.md) before a chart depends on a helper.

[Render for a target cluster](../guide/template-rendering.md) covers release and capabilities context. For member lookup, see the [generated Engine API](../api/generated/engine.md).
