# Migrate from Helm CLI

HelmSharp moves Helm operations into application code. It does not execute `helm`, so migrate the intent of a command and make every input explicit.

| Helm CLI intent | HelmSharp starting point |
| --- | --- |
| `helm template` | `HelmChartLoader`, `HelmValues`, and `HelmTemplateRenderer`, or `HelmClient.TemplateAsync`. |
| `helm upgrade --install` | `HelmClient.UpgradeInstallAsync`. |
| `helm rollback` | `HelmClient.RollbackAsync`. |
| `helm uninstall` | `HelmClient.UninstallAsync`. |
| `helm status` / `helm history` | `StatusAsync` / `HistoryAsync`. |
| `helm get manifest` / `helm get values` | `GetManifestAsync` / `GetValuesAsync`. |

## Example: upgrade or install

```csharp
var result = await client.UpgradeInstallAsync(new HelmUpgradeInstallRequest
{
    ReleaseName = "nginx",
    Namespace = "web",
    Chart = "./charts/nginx",
    ValuesFiles = ["values.production.yaml"],
    CreateNamespace = true,
    Wait = true,
    TimeoutSeconds = 300
}, cancellationToken);

if (!result.Succeeded)
    throw new InvalidOperationException(result.StandardError);
```

## Migration checklist

- Replace shell arguments with explicit request properties.
- Put cluster credentials and execution defaults behind your application's `IHelmOptionsProvider`.
- Decide whether HelmSharp or another controller owns cluster mutation.
- Preserve the chart version and effective values that an operation used.
- Validate required behavior in the [compatibility contract](../helm-compatibility.md); CLI plugins and every CLI switch are not runtime dependencies or API equivalents.

For release-history behavior, see [Release storage](../concepts/release-storage.md).
