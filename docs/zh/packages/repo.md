# HelmSharp.Repo

## 包职责

`HelmSharp.Repo` 处理 Chart 仓库元数据：添加、移除、列出、搜索、拉取、生成索引，以及有限的 OCI 相关占位能力。

## 仓库配置与缓存

`HelmChartRepository` 会将仓库定义持久化到兼容 Helm 的 `repositories.yaml`。默认情况下，它遵循 Helm 风格的环境变量：`HELM_REPOSITORY_CONFIG` 指定配置文件，`HELM_CONFIG_HOME`（或 `XDG_CONFIG_HOME`）指定配置目录，`HELM_REPOSITORY_CACHE` / `HELM_CACHE_HOME`（或 `XDG_CACHE_HOME`）指定仓库索引和下载 Chart 的缓存目录。`HELM_CACHE_HOME` 表示缓存根目录，仓库文件会写入其中的 `repository` 子目录。在 Windows 上，未配置时会使用当前用户的应用数据目录。

可使用 `HelmRepositoryOptions` 将应用或测试与用户的 Helm 状态隔离：

```csharp
var repository = new HelmChartRepository(new HelmRepositoryOptions
{
    ConfigDirectory = @"C:\app-data\helm-config",
    CacheDirectory = @"C:\app-data\helm-cache"
});
```

原有的 `HelmChartRepository(cacheDirectory)` 重载仍可使用，并会以该目录同时隔离配置和缓存。显式指定的 `HelmRepositoryOptions.ConfigDirectory` 与 `CacheDirectory` 优先于环境中的 Helm 变量。仓库索引缓存使用 Helm 兼容的 `<repository-name>-index.yaml` 文件名。仓库名称仅可包含字母、数字、`.`、`_` 与 `-`；添加同名仓库会抛出错误，与 Helm 未使用 `--force` 时的行为一致。仅在提供凭据时才会保存凭据，保存格式与此前 HelmSharp 的明文方式相同，因此应使用操作系统的常规文件权限保护配置文件。

## 何时安装

构建仓库管理工具时直接安装：

```powershell
dotnet add package HelmSharp.Repo --version 1.1.1
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

先用仓库辅助方法获取 Chart，再交给 `HelmChartLoader` 或高层 `HelmClient`。

## 当前边界

认证和 OCI 流程在 1.1.1 中仍比完整 Helm CLI 更有限。
