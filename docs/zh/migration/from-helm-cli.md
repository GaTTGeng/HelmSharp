# 从 Helm CLI 迁移

HelmSharp 将 Helm 操作移入应用代码。它不执行 `helm`，因此应迁移命令的意图，并显式提供每个输入。

| Helm CLI 意图 | HelmSharp 起点 |
| --- | --- |
| `helm template` | `HelmChartLoader`、`HelmValues` 和 `HelmTemplateRenderer`，或 `HelmClient.TemplateAsync`。 |
| `helm upgrade --install` | `HelmClient.UpgradeInstallAsync`。 |
| `helm rollback` | `HelmClient.RollbackAsync`。 |
| `helm uninstall` | `HelmClient.UninstallAsync`。 |
| `helm status` / `helm history` | `StatusAsync` / `HistoryAsync`。 |
| `helm get manifest` / `helm get values` | `GetManifestAsync` / `GetValuesAsync`。 |

## 示例：升级或安装

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

## 迁移清单

- 用明确的请求属性替换 shell 参数。
- 将集群凭据和执行默认值放入应用自有的 `IHelmOptionsProvider`。
- 决定由 HelmSharp 还是其他控制器负责集群变更。
- 保存操作所使用的 Chart 版本和生效 values。
- 在[兼容性约定](../helm-compatibility.md)中验证所需行为；CLI 插件和所有 CLI 开关都不是运行时依赖或 API 等价物。

release 历史行为见[Release 存储](../concepts/release-storage.md)。
