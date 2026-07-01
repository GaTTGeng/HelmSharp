# Error Handling

## What problem this solves

HelmSharp exposes two failure shapes: high-level `CommandResult` failures for command-like APIs and exceptions for lower-level APIs. Handle both deliberately so chart authors get useful diagnostics.

## Packages to install

Use the package that owns the failing workflow. Most application-level error handling starts from:

```powershell
dotnet add package HelmSharp.Action --version 1.1.0
```

## Minimal complete code

```csharp
var result = await client.TemplateAsync(request, cancellationToken);

if (result.ExitCode != 0)
{
    logger.LogWarning(
        "HelmSharp template failed for {Chart}: {Error}",
        request.Chart,
        result.StandardError);
    return Results.BadRequest(result.StandardError);
}

return Results.Text(result.StandardOutput, "text/yaml");
```

## Why these APIs

`HelmClient` methods generally return `CommandResult` so application code can model stdout, stderr, and exit codes. `HelmChartLoader`, `HelmValues`, and `HelmTemplateRenderer` throw ordinary .NET exceptions when loading, values parsing, or template evaluation fails.

## Production notes

- Log chart path, release name, namespace, values file names, inline values source, HelmSharp version, and target kube version.
- For compatibility reports, capture HelmSharp output and `helm template` output with line endings normalized only.
- Do not hide parser context from chart authors; template name and failing expression are usually the fastest path to a fix.

## Next step

Use [API Reference](../api/index.md) when you need member-level details.
