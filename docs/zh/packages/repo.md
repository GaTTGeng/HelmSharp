# HelmSharp.Repo

## 包职责

`HelmSharp.Repo` 处理 Chart 仓库元数据：添加、移除、列出、搜索、拉取、生成索引，以及有限的 OCI 相关占位能力。

## 何时安装

构建仓库管理工具时直接安装：

```powershell
dotnet add package HelmSharp.Repo --version 1.1.0
```

## 依赖关系

该包引用 `HelmSharp.Chart` 和 `HelmSharp.Registry`。

## 主要类型

| 类型 | 用途 |
| --- | --- |
| `HelmChartRepository` | 管理本地仓库配置、搜索索引、拉取 Chart。 |
| `HelmRepoIndexer` | 生成仓库 `index.yaml`。 |
| `HelmRepository` | 仓库配置项。 |
| `HelmRepoIndex` | 解析后的仓库索引。 |
| `HelmChartVersion` | 索引里的 Chart 版本元数据。 |
| `HelmChartSearchResult` | 搜索结果。 |

## 常见组合

先用 repo helper 获取 Chart，再交给 `HelmChartLoader` 或高层 `HelmClient`。

## 当前边界

认证和 OCI 流程在 1.1.0 中仍比完整 Helm CLI 更有限。
