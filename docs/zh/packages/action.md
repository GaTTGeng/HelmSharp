# HelmSharp.Action

## 包职责

`HelmSharp.Action` 是高层 facade，面向需要 Helm 风格操作的应用：template、install/upgrade、uninstall、rollback、status、history、get、lint、package、repository 和 registry 相关命令。

## 何时安装

当产品按 release 工作流或类命令结果组织逻辑时安装：

```powershell
dotnet add package HelmSharp.Action --version 1.1.0
```

## 依赖关系

该包引用渲染、Chart、Kubernetes、Release、Repo、Registry、Storage 和 PostRenderer 相关包。

## 主要类型

| 类型 | 用途 |
| --- | --- |
| `HelmClient` / `IHelmClient` | 类命令 SDK 入口。 |
| `HelmTemplateRequest` | 渲染 Chart，不 apply。 |
| `HelmUpgradeInstallRequest` | install 或 upgrade，包括 dry-run。 |
| `HelmUninstallRequest` | 删除 release 资源。 |
| `HelmExecutionOptions` | 集中管理环境默认值。 |
| `IHelmOptionsProvider` | 从配置、DI 或租户上下文提供 options。 |
| `CommandResult` | 捕获 stdout、stderr 和 exit code。 |

## 常见组合

`TemplateAsync` 用于预览，`UpgradeInstallAsync` + `DryRun = true` 用于审核，审批后才使用 `DryRun = false`。状态和审计可用 `StatusAsync`、`HistoryAsync`、`GetManifestAsync`、`GetValuesAsync`。

## 当前边界

HelmSharp 不调用 `helm`。插件、provenance、OCI auth 和少见 Kubernetes 边界仍不是 1.1.0 的完整 Helm CLI 对齐范围。
