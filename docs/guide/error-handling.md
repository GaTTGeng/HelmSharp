# Troubleshoot failures

HelmSharp has two intentionally different failure models. Handle the one used by your entry point instead of converting every failure into a vague “render failed” response.

| API layer | Failure shape | Typical caller |
| --- | --- | --- |
| `HelmClient` in `HelmSharp.Action` | `CommandResult` with exit code, standard output, and standard error | HTTP endpoint, CLI wrapper, deployment service. |
| `HelmChartLoader`, `HelmValues`, `HelmTemplateRenderer`, repository helpers | .NET exception | Library code that wants normal exception composition. |

## Handle high-level operations

```csharp
var result = await client.TemplateAsync(request, cancellationToken);

if (!result.Succeeded)
{
    logger.LogWarning(
        "Chart render failed for {Chart}, release {Release}: {Error}",
        request.Chart,
        request.ReleaseName,
        result.StandardError);

    return Results.BadRequest(new
    {
        error = result.StandardError,
        exitCode = result.ExitCode
    });
}

return Results.Text(result.StandardOutput, "text/yaml");
```

`StandardOutput` can still be useful when a command fails, so retain it with the operation record when access controls permit. Do not infer success from a non-empty output string; use `Succeeded` or `ExitCode`.

## Add context around lower-level rendering

Let loading, YAML parsing, and template exceptions keep their original message and stack trace. Add request context at the boundary that owns the chart path and values, not in a catch block that erases the exception type.

Useful context includes the chart identity or path, release name, namespace, selected chart version, values-file names, explicit set keys (not secret values), target Kubernetes version, and API versions. For a parity report, keep the exact values inputs and normalize line endings only before comparing output.

## Common failure questions

| Symptom | First thing to inspect |
| --- | --- |
| A template function is not supported | [Template-function matrix](../template-function-compatibility.md), then the template path in the renderer diagnostic. |
| A chart differs from Helm | Target capabilities, effective values, and the [compatibility contract](../helm-compatibility.md). [HelmCompare](../compare.md) can help inspect the difference. |
| A release command returns failure | `StandardError`, Kubernetes RBAC, target namespace, hook status, and readiness timeout. |
| A release exists but output is unexpected | Stored revision with `StatusAsync`, `HistoryAsync`, `GetManifestAsync`, or `GetValuesAsync`; do not assume the current chart was used. |
| A direct apply fails for a CRD | API discovery, resource identity, and the target cluster's installed CRD version. |

Rendered manifests and values may contain credentials. Return sanitized diagnostics to untrusted callers and keep sensitive artifacts in a restricted operation record rather than application logs.
