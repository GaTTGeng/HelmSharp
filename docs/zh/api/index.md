# API 参考

在选定工作流后，用这一节查询参数和成员。生成页刻意只保留事实：它从源码镜像公开类型、属性和方法，而不试图再讲一遍使用流程。

::: tip 源码与包版本
生成页跟随当前源码树。应用必须固定在某个已发布版本时，请同时查看包指南和更新日志；本站当前示例使用 `1.3.1`。
:::

## 先看包指南

| 包 | 人工指南 | 生成参考 |
| --- | --- | --- |
| `HelmSharp.Action` | [包指南](../packages/action.md) | [API](generated/action.md) |
| `HelmSharp.Chart` | [包指南](../packages/chart.md) | [API](generated/chart.md) |
| `HelmSharp.Engine` | [包指南](../packages/engine.md) | [API](generated/engine.md) |
| `HelmSharp.Kube` | [包指南](../packages/kube.md) | [API](generated/kube.md) |
| `HelmSharp.Release` | [包指南](../packages/release.md) | [API](generated/release.md) |
| `HelmSharp.Repo` | [包指南](../packages/repo.md) | [API](generated/repo.md) |
| `HelmSharp.Registry` | [包指南](../packages/registry.md) | [API](generated/registry.md) |
| `HelmSharp.Storage` | [包指南](../packages/storage.md) | [API](generated/storage.md) |
| `HelmSharp.PostRenderer` | [包指南](../packages/post-renderer.md) | [API](generated/postrenderer.md) |

## 建议的阅读顺序

1. 先通过[包和 API 选择](../api-overview.md)确认这一层抽象适合放入应用。
2. 在生成页中查找精确成员、请求属性、返回类型和源码位置。
3. 成员会变更集群状态、values 优先级或兼容性行为时，回到对应指南或示例阅读。

## 模板函数 API

`HelmSharp.Engine.Functions` 和 `HelmSharp.Engine.Utilities` 下的类型会出现在 Engine 参考中，但它们主要服务 Helm/Sprig 模板执行。应用代码通常应调用 `HelmTemplateRenderer`，而不是直接依赖这些辅助类型。

## 公开 API 变更后重新生成

```powershell
pwsh docs/scripts/generate-api-reference.ps1
```
