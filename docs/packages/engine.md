# HelmSharp.Engine

## Package responsibility

`HelmSharp.Engine` renders Helm-style templates from managed code. In 1.1.0, its golden suite reaches Pass verdicts for five real-world public charts and 129/129 templates.

## When to install

Install this package with `HelmSharp.Chart` when you need manifest output but not release lifecycle operations:

```powershell
dotnet add package HelmSharp.Engine --version 1.1.0
```

## Dependencies

This package references `HelmSharp.Chart` and uses YAML serialization support.

## Main types

| Type | Use it for |
| --- | --- |
| `HelmTemplateRenderer` | Render manifests and NOTES. |
| `TemplateParseException` | Diagnose malformed template input. |
| `TemplateContext` | Provide render-time release and capabilities context. |
| `ApiVersionSet` | Model `.Capabilities.APIVersions`. |
| `TemplateParser` / tokenizer / AST types | Parser internals and diagnostics, not typical app entry points. |

## Common combinations

Load with `HelmChartLoader`, merge with `HelmValues.BuildAsync`, then render with `HelmTemplateRenderer`.

## Current boundaries

Function and utility classes under `HelmSharp.Engine.Functions` and `HelmSharp.Engine.Utilities` primarily implement Helm/Sprig template behavior. Treat them as renderer support APIs rather than stable application convenience libraries.
