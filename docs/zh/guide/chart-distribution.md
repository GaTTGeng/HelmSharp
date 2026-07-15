# Chart 打包与仓库工作流

M2 使用纯托管 .NET 覆盖传统 HTTP Chart 工作流：打包 Chart、生成 `index.yaml`、管理仓库配置与缓存、拉取归档，以及更新或按锁文件重建依赖。测试套件把 Helm 作为参照，但运行时不依赖 Helm。

## 打包 Chart

需要覆盖元数据或希望打包前刷新依赖时，使用请求对象重载：

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#package-chart{csharp}

`Version` 与 `AppVersion` 只写入归档内的 `Chart.yaml`，不会修改源文件。归档命名为 `<chart-name>-<version>.tgz`，只有一个 `<chart-name>/` 根目录，并保留嵌套 Chart 与 CRD；符号链接会被跳过。

打包器读取 Chart 根目录的 `.helmignore`。空行和注释会被忽略；支持文件、目录、`*`、`?`、字符集、根路径和 `!` 取反规则。Helm 的 `**` 语法目前不支持，并会返回明确错误。`DependencyUpdate` 为 `true` 时，只有依赖更新成功才会写出归档。

## 生成仓库索引

把一个或多个 `.tgz` 包放到同一目录，再生成仓库元数据：

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#repository-index{csharp}

`Url` 会成为包条目的基础 URL。`MergeIndexPath` 可保留当前目录中不存在的旧版本。默认跳过无效包，并可通过低层诊断读取原因；发布流程需要事务性失败时设置 `FailOnInvalidPackage`。未设置 `OutputPath` 时，会写到 `DirectoryPath` 下的 `index.yaml`。

## 隔离仓库状态

`HelmChartRepository` 使用兼容 Helm 的配置与缓存位置。服务、测试和多租户场景应显式指定路径，避免并发工作负载共享凭据或过期索引：

```csharp
using var repository = new HelmChartRepository(new HelmRepositoryOptions
{
    RepositoryConfigPath = Path.Combine(tenantRoot, "repositories.yaml"),
    CacheDirectory = Path.Combine(tenantRoot, "cache")
});
```

仓库定义保存在 `repositories.yaml`，缓存索引使用 `<repository-name>-index.yaml`。未显式指定路径时，会依次考虑 `HELM_REPOSITORY_CONFIG`、`HELM_CONFIG_HOME`、`HELM_REPOSITORY_CACHE`、`HELM_CACHE_HOME`、对应的 XDG 位置和平台默认目录。

完整仓库生命周期使用以下方法：

1. `AddRepositoryAsync` 写入命名仓库定义。
2. `ListRepositoriesAsync` 读取已配置定义。
3. `FetchRepoIndexAsync` 刷新选定仓库的缓存。
4. `SearchRepoAsync(keyword)` 离线搜索已配置缓存。
5. `RemoveRepositoryAsync` 同时删除定义和对应缓存索引。

仓库搜索有意保持为纯缓存操作。需要远端最新结果时应先刷新；某个仓库的缓存缺失或损坏，不会遮蔽其他仓库的有效结果。

## 拉取 Chart

下例添加传统仓库、刷新索引、按语义版本选择包、校验索引中的摘要并解压归档：

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#pull-chart{csharp}

支持以下拉取形式：

- 使用已配置仓库和缓存索引的 `repo/chart`；
- Chart 名称配合 `RepositoryUrl` 指定仓库；
- 直接使用 `https://.../chart-version.tgz` 归档 URL。

`Destination` 控制归档保存位置。`Untar` 启用解压，`UntarDirectory` 指定解压父目录。解压会拒绝逃逸目标目录的条目。仓库凭据默认只发送到同源地址；仅当跨域归档主机可信时才启用 `PassCredentialsAll`。

## 声明依赖

`Chart.yaml` 可以组合仓库别名、Chart 别名与本地引用：

```yaml
dependencies:
  - name: redis
    alias: cache
    version: ~18.0.0
    repository: "@stable"
  - name: redis
    alias: session
    version: ~18.0.0
    repository: "alias:stable"
  - name: shared-templates
    version: 1.2.3
    repository: file://../shared-templates
```

`@stable` 与 `alias:stable` 通过 `repositories.yaml` 中的同名条目解析。相对 `file://` 路径以父 Chart 目录为基准，并打包到 `charts/`。锁文件保留原始依赖名称和仓库引用。Chart 别名会改变子 Chart 身份与 values 键，因此上面第一个依赖的 values 应放在 `cache:` 下，而不是 `redis:` 下。

`condition`、`tags` 与 `import-values` 影响 values 和渲染，不会把声明的依赖从更新或下载集合中移除。

## 更新依赖

更新操作会解析版本约束，下载或打包所有声明的依赖，删除过期 `.tgz`，并写出兼容 Helm 的 `Chart.lock`：

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#dependency-update{csharp}

普通在线更新应保持 `SkipRepositoryRefresh = false`。只有 `RepositoryCachePath` 已包含所需命名仓库索引时才设为 `true`，例如受控离线构建。

## 按 `Chart.lock` 重建

CI 与发布应使用可复现的 build 路径：

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#dependency-build{csharp}

该操作要求存在 `Chart.lock`，会校验其摘要仍与 `Chart.yaml` 一致，恢复精确锁定版本，并且不重写锁文件。已配置仓库依赖需要对应缓存索引；`file://` 依赖也要求引用的源 Chart 仍可访问且版本与锁一致。

打包前可运行 `DependencyListAsync`，检查 `ok`、缺失、版本错误、未打包目录和锁一致性状态。

## 兼容性边界

本指南覆盖传统 HTTP Chart 仓库和本地文件依赖。OCI 注册表认证与拉取/推送对齐、provenance 文件、签名和签名校验属于后续 OCI 与来源证明里程碑。这里描述的工作流都不会调用 Helm CLI。

高层 `HelmClient` 方法返回 `CommandResult`，应检查 `Succeeded`、`ExitCode`、`StandardOutput` 与 `StandardError`；低层仓库方法抛出 .NET 异常。共享模型见[错误处理](error-handling.md)，包职责见 [HelmSharp.Action](../packages/action.md)、[HelmSharp.Chart](../packages/chart.md) 与 [HelmSharp.Repo](../packages/repo.md)。
