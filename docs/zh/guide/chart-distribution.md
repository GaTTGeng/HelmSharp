# 打包 Chart 与管理依赖

HelmSharp 以托管代码支持传统 HTTP Chart 仓库工作流：打包 Chart、生成 `index.yaml`、管理隔离的仓库状态、拉取归档和解析依赖。运行时不需要 Helm CLI。

::: warning 本文范围
OCI 认证和推拉对齐、provenance 文件、签名和签名验证不属于本文工作流。在此基础上建设生产 Chart 仓库服务前，请先查看[兼容性](../helm-compatibility.md)。
:::

## 打包 Chart

需要覆盖元数据或在打包前刷新依赖时，使用请求对象重载：

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#package-chart{csharp}

`Version` 和 `AppVersion` 只改变归档内的 `Chart.yaml`，不修改源文件。归档名是 `<chart-name>-<version>.tgz`，只含一个 Chart 根目录，包含嵌套 Chart 和 CRD，并跳过符号链接。`.helmignore` 支持文件、目录、`*`、`?`、字符类、根路径和 `!` 反选模式；`**` 会被明确拒绝。

## 生成仓库元数据

将 Chart 归档放入一个目录后，生成索引：

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#repository-index{csharp}

`Url` 是包的基础 URL。`MergeIndexPath` 会保留当前目录中已不再出现的历史条目。无效归档必须阻止发布时，设置 `FailOnInvalidPackage`；否则检查被跳过包的诊断。`OutputPath` 默认是 `DirectoryPath` 下的 `index.yaml`。

## 服务中隔离仓库状态

不要让租户、测试或并发任务共享仓库配置和缓存目录。

```csharp
using var repository = new HelmChartRepository(new HelmRepositoryOptions
{
    RepositoryConfigPath = Path.Combine(tenantRoot, "repositories.yaml"),
    CacheDirectory = Path.Combine(tenantRoot, "cache")
});
```

仓库配置保存定义，缓存索引使用 `<repository-name>-index.yaml`。未显式指定路径时，会采用 Helm 兼容环境变量和平台默认路径。仓库搜索仅查询缓存：需要最新远端结果时，先刷新索引。

## 安全拉取 Chart

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#pull-chart{csharp}

请求可接受 `repo/chart`、Chart 名加 `RepositoryUrl`，或直接的 `https://…tgz` URL。下载的归档存放在 `Destination` 下。启用 `Untar` 时，`UntarDirectory` 选择解压根目录；未设置时才以 `Destination` 为解压根目录。任何逃逸出所选根目录的条目都会被拒绝。凭据默认只发送给仓库源站；只有可信仓库有意将归档跳转到另一个受认证源站时，才启用 `PassCredentialsAll`。

## 让依赖构建可复现

和 Helm 一样在 `Chart.yaml` 中声明别名和本地引用：

```yaml
dependencies:
  - name: redis
    alias: cache
    version: ~18.0.0
    repository: "@stable"
  - name: shared-templates
    version: 1.2.3
    repository: file://../shared-templates
```

别名会改变子 Chart 标识和对应的 values 键，因此第一个依赖应从 `cache:` 而不是 `redis:` 读取 values。`DependencyUpdateAsync` 解析约束、刷新依赖并写入 `Chart.lock`；CI 应使用 `DependencyBuildAsync`：它验证 lock 与 `Chart.yaml` 的一致性、还原精确锁定版本，且不重写 lock。

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#dependency-update{csharp}

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#dependency-build{csharp}

要向用户报告缺失、错误版本、解包或 lock 不一致的依赖时，打包前运行 `DependencyListAsync`。高层客户端方法返回 `CommandResult`，低层仓库方法抛出异常；两种诊断如何保留，见[排查失败](error-handling.md)。
