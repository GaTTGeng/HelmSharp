# 选择包和 API

按应用需要执行的操作选择包。渲染会加载 Chart 和 values，并返回清单文本；发布操作还需要 Kubernetes 凭据，并维护生命周期历史。

## 按任务选择
| 目标 | 从这里开始 | 主要类型 | 以下情况不要选它 |
| --- | --- | --- | --- |
| 渲染 Chart | `HelmSharp.Chart` + `HelmSharp.Engine` | `HelmChartLoader`、`HelmValues`、`HelmTemplateRenderer` | 应用还必须提交资源并记录 release 历史。 |
| 提供 Helm 风格操作 | `HelmSharp.Action` | `HelmClient`、`IHelmClient`、请求对象、`CommandResult` | 只需要 YAML 且不希望依赖 Kubernetes。 |
| 提交现有 YAML | `HelmSharp.Kube` | `KubernetesManifestApplier`、`KubernetesResourceWaiter`、`ManifestIdentity` | 需要 Helm release 状态、hook 或 values 合并。 |
| 直接处理 release | `HelmSharp.Release` | release 模型与 Kubernetes 存储 | `HelmClient` 已经拥有完整工作流。 |
| 维护 HTTP Chart 仓库 | `HelmSharp.Repo` | `HelmChartRepository`、`HelmRepoIndexer`、`HelmPullRequest` | 需求是完整 OCI registry 对齐。 |
| 转换清单文本 | `HelmSharp.PostRenderer` | `IPostRenderer` | 转换逻辑本就应该写在 Chart 模板中。 |

## 使用高层客户端还是渲染器？

应用提供渲染能力时，直接用 `HelmTemplateRenderer`。这样代码会清楚展示操作：加载 Chart、计算 values、配置 capabilities、返回字符串。

公共接口有意模拟 Helm 操作时，使用 `HelmClient`。它为 template、打包、仓库、依赖和生命周期操作统一返回 `CommandResult`，因此 HTTP 接口或 CLI 都能一致地处理标准输出、标准错误和退出码。

```csharp
var result = await client.TemplateAsync(new HelmTemplateRequest
{
    ReleaseName = "preview",
    Namespace = "platform",
    Chart = chartPath,
    ValuesFiles = ["values.production.yaml"],
    KubeVersion = "1.30.0"
}, cancellationToken);

if (!result.Succeeded)
    return Results.BadRequest(result.StandardError);

return Results.Text(result.StandardOutput, "text/yaml");
```

## 使用生成的公共 API 索引

生成页从当前源码列出公开类型、属性和方法。它是按名称和源码查找的索引，并非完整 API 参考文档。先用包指南判断*应该把哪一层抽象放进代码*，再打开链接的源码声明查询参数级细节。

| 包 | 指南 | 生成 API |
| --- | --- | --- |
| `HelmSharp.Action` | [指南](packages/action.md) | [API](api/generated/action.md) |
| `HelmSharp.Chart` | [指南](packages/chart.md) | [API](api/generated/chart.md) |
| `HelmSharp.Engine` | [指南](packages/engine.md) | [API](api/generated/engine.md) |
| `HelmSharp.Kube` | [指南](packages/kube.md) | [API](api/generated/kube.md) |
| 分发与扩展包 | [全部包指南](api/index.md) | [全部生成页](api/index.md) |

`HelmSharp.Engine.Functions` 和 `HelmSharp.Engine.Utilities` 下的模板 helper 类型主要用于实现 Helm/Sprig 行为。应用代码应将 `HelmTemplateRenderer` 视为渲染器 API；Chart 要依赖某个 helper 前，请查阅[函数矩阵](template-function-compatibility.md)。
