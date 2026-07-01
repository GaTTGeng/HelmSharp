# HelmSharp.Repo

## 包职责

`HelmSharp.Repo` 处理 Chart repository 元数据：add、remove、list、search、pull、index，以及有限的 OCI 相关占位能力。

## 何时安装

构建 repository 管理工具时直接安装：

```powershell
dotnet add package HelmSharp.Repo --version 1.1.0
```

## 依赖关系

该包引用 `HelmSharp.Chart` 和 `HelmSharp.Registry`。

## 主要类型

| 类型 | 用途 |
| --- | --- |
| `HelmChartRepository` | 管理本地 repo 配置、搜索 index、拉取 Chart。 |
| `HelmRepoIndexer` | 生成 repository `index.yaml`。 |
| `HelmRepository` | Repository 配置项。 |
| `HelmRepoIndex` | 解析后的 repository index。 |
| `HelmChartVersion` | Index 里的 Chart version 元数据。 |
| `HelmChartSearchResult` | Search result。 |

## 常见组合

先用 repo helper 获取 Chart，再交给 `HelmChartLoader` 或高层 `HelmClient`。

## 当前边界

认证和 OCI 流程在 1.1.0 中仍比完整 Helm CLI 更有限。
