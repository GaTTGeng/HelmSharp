# HelmSharp.Action

## 包职责

`HelmSharp.Action` 是高层门面，面向需要 Helm 风格操作的应用：模板渲染、安装/升级、卸载、回滚、状态查询、历史记录、get、lint、打包、仓库和注册表相关命令。

## 何时安装

当产品按发布工作流或类命令结果组织逻辑时安装：

```powershell
dotnet add package HelmSharp.Action --version 1.1.1
```

## 依赖关系

该包引用渲染、Chart、Kubernetes、Release、Repo、Registry、Storage 和 PostRenderer 相关包。

## 主要类型

| 类型 | 用途 |
| --- | --- |
| `HelmClient` / `IHelmClient` | 类命令 SDK 入口。 |
| `HelmTemplateRequest` | 渲染 Chart，不提交。 |
| `HelmUpgradeInstallRequest` | 安装或升级，包括试运行。 |
| `HelmUninstallRequest` | 删除发布资源。 |
| `HelmExecutionOptions` | 集中管理环境默认值。 |
| `IHelmOptionsProvider` | 从配置、DI 或租户上下文提供选项。 |
| `CommandResult` | 捕获标准输出、标准错误和退出码。 |

## 常见组合

`TemplateAsync` 用于预览，`UpgradeInstallAsync` + `DryRun = true` 用于审核，审批后才使用 `DryRun = false`。状态和审计可用 `StatusAsync`、`HistoryAsync`、`GetManifestAsync`、`GetValuesAsync`。

## 当前边界

HelmSharp 不调用 `helm`。插件、来源证明、OCI 认证和少见 Kubernetes 边界仍不是 1.1.1 的完整 Helm CLI 对齐范围。
