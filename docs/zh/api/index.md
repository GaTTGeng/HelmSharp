# API 参考

选好包和工作流之后，再查 API 参考。生成页会从源码列出公开类型、属性和方法，便于后续版本刷新。

::: warning 源码与发布版本
生成页反映当前 `master` 源码树；M2 请求和分发 API 已包含在最新发布的 1.2.0 包中。
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

## 如何阅读生成页

生成页只保留事实信息：类型类别、源码文件、属性、方法和简短使用提示。它不会取代包指南，因为成员列表无法解释工作流边界。

## 模板函数 API

`HelmSharp.Engine.Functions` 和 `HelmSharp.Engine.Utilities` 下的类型会出现在 Engine 参考中，但它们主要服务 Helm/Sprig 模板执行。应用代码通常应调用 `HelmTemplateRenderer`，而不是直接依赖这些辅助类型。

## 重新生成

```powershell
pwsh docs/scripts/generate-api-reference.ps1
```
