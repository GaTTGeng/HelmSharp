# Values

## What problem this solves

Values are usually the integration boundary between your product model and a Helm chart. HelmSharp keeps the familiar Helm precedence model so operators can bring existing values files and `--set`-style overrides.

## Packages to install

```powershell
dotnet add package HelmSharp.Chart --version 1.1.0
```

## Minimal complete code

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#values-precedence{csharp}

## Why these APIs

`HelmValues.BuildAsync` merges inputs from lowest to highest precedence:

| Input | Meaning |
| --- | --- |
| Chart defaults | `values.yaml` bundled with the chart. |
| Subchart defaults | Dependency defaults under dependency name or alias. |
| `valuesFiles` | One or more values files, applied in order. |
| `valuesContent` | Inline YAML from a database, request, or generated config. |
| `setFileValues` | File content assigned to a values path. |
| `setStringValues` | String-preserving overrides. |
| `setValues` | Scalar-coercing `--set` style overrides. |
| `setJsonValues` | JSON object or array overrides. |

## Production notes

- Keep the precedence order visible in code reviews.
- Read file content before passing `SetFileValues`; the value is content, not a file path.
- Use `SetStringValues` for tags such as `"001"` that must not become numbers.

## Next step

Use [Template Rendering](template-rendering.md) when values must influence Capabilities, NOTES, or CRD output.
