# HelmSharp.Repo

## 包职责

`HelmSharp.Repo` 处理 Chart 仓库元数据：添加、移除、列出、搜索、拉取、生成索引，以及有限的 OCI 相关占位能力。

## 仓库配置与缓存

`HelmChartRepository` 会将仓库定义持久化到兼容 Helm 的 `repositories.yaml`。默认情况下，它遵循 Helm 风格的环境变量：`HELM_REPOSITORY_CONFIG` 指定配置文件，`HELM_CONFIG_HOME`（或 `XDG_CONFIG_HOME`）指定配置目录，`HELM_REPOSITORY_CACHE` / `HELM_CACHE_HOME`（或 `XDG_CACHE_HOME`）指定仓库索引和下载 Chart 的缓存目录。`HELM_CACHE_HOME` 表示缓存根目录，仓库文件会写入其中的 `repository` 子目录。平台默认值与 Helm 一致：Linux 的配置和缓存根目录分别是 `~/.config/helm` 与 `~/.cache/helm`，macOS 分别是 `~/Library/Preferences/helm` 与 `~/Library/Caches/helm`，Windows 分别是 `%APPDATA%\helm` 与 `%TEMP%\helm`。

可使用 `HelmRepositoryOptions` 将应用或测试与用户的 Helm 状态隔离：

```csharp
var repository = new HelmChartRepository(new HelmRepositoryOptions
{
    ConfigDirectory = @"C:\app-data\helm-config",
    CacheDirectory = @"C:\app-data\helm-cache"
});
```

原有的 `HelmChartRepository(cacheDirectory)` 重载仍可使用，并会以该目录同时隔离配置和缓存。显式指定的 `HelmRepositoryOptions.ConfigDirectory` 与 `CacheDirectory` 优先于环境中的 Helm 变量。仓库索引缓存通常使用 Helm 兼容的 `<repository-name>-index.yaml` 文件名；需要进行可移植文件名清理的名称会获得确定性的身份后缀，Windows 与 macOS 上的大写名称也会获得该后缀，从而防止不同仓库身份互相覆盖。兼容 Helm 的名称可以包含空格、`@` 和前导点，但不能为空或包含 `/`。以不同设置添加完全同名仓库会报错；重复 URL 与仅大小写不同的名称仍保留独立身份。仅在提供凭据时才会保存凭据，保存格式与此前 HelmSharp 的明文方式相同；替换配置时会保留现有 Unix 权限，新建的含凭据文件从创建起即仅所有者可访问。

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

包含隔离配置/缓存路径的添加、列出、更新、搜索、索引生成和拉取完整示例见 [Chart 打包与仓库工作流](../guide/chart-distribution.md)。

## 当前边界

传统 HTTP 仓库配置、缓存搜索、语义版本拉取、摘要校验与安全解压已覆盖。来源证明校验和完整 OCI 注册表工作流仍属于后续里程碑。
