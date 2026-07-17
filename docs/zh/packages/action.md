# HelmSharp.Action

## 包职责

`HelmSharp.Action` 是高层门面，面向需要 Helm 风格操作的应用：模板渲染、安装/升级、卸载、回滚、状态查询、历史记录、get、lint、打包、仓库和注册表相关命令。

## 何时安装

当产品按发布工作流或类命令结果组织逻辑时安装：

```powershell
dotnet add package HelmSharp.Action --version 1.2.0
```

::: warning 版本可用性
1.2.0 是最新发布包。下文的 M2 请求类型以及打包、拉取、仓库索引和依赖工作流已包含在上面的 1.2.0 安装命令所安装的包中。
:::

## 依赖关系

该包引用渲染、Chart、Kubernetes、Release、Repo、Registry、Storage 和 PostRenderer 相关包。

## 主要类型

| 类型 | 用途 |
| --- | --- |
| `HelmClient` / `IHelmClient` | 类命令 SDK 入口。 |
| `HelmTemplateRequest` | 渲染 Chart，不提交。 |
| `HelmUpgradeInstallRequest` | 安装或升级，包括试运行。 |
| `HelmUninstallRequest` | 删除发布资源。 |
| `HelmPackageRequest` | 使用元数据与依赖选项打包 Chart。 |
| `HelmDependencyUpdateRequest` | 解析依赖并更新 `Chart.lock`。 |
| `HelmDependencyBuildRequest` | 按 `Chart.lock` 恢复精确版本。 |
| `HelmExecutionOptions` | 集中管理环境默认值。 |
| `IHelmOptionsProvider` | 从配置、DI 或租户上下文提供选项。 |
| `CommandResult` | 捕获标准输出、标准错误和退出码。 |

## 常见组合

`TemplateAsync` 用于预览，`UpgradeInstallAsync` + `DryRun = true` 用于审核，审批后才使用 `DryRun = false`。状态和审计可用 `StatusAsync`、`HistoryAsync`、`GetManifestAsync`、`GetValuesAsync`。Chart 分发使用 `PackageAsync`、`PullAsync` 与 `RepoIndexAsync`；依赖生命周期使用 `DependencyListAsync`、`DependencyUpdateAsync` 与 `DependencyBuildAsync`。

完整请求示例、锁文件行为、仓库隔离与兼容性边界见 [Chart 打包与仓库工作流](../guide/chart-distribution.md)。

## 当前边界

HelmSharp 不调用 `helm`。M2 覆盖传统 HTTP 仓库与本地文件依赖；来源证明以及完整 OCI 认证和拉取/推送对齐仍属于后续兼容性工作。
