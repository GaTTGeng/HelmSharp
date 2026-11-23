# 配置 Kubernetes 等待、Job、超时与清理

组合公开的 `HelmExecutionOptions`、`HelmUpgradeInstallRequest` 和 `HelmUninstallRequest` 选项，使命名空间创建、升级字段归属、Job 完成等待、操作超时、调用方取消和清理行为保持明确。

## 固定执行默认值

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#deployment-options-provider{csharp}

`ServerSideApply = false` 使用已支持的客户端提交路径（托管生命周期会在变更前拒绝 `ServerSideApply = true`）。在存在先前已部署清单的升级中，`HelmClient` 会执行三方合并，保留集群侧 live-only 字段并应用 Chart 变更。`FieldManager` 会转发给自定义资源等动态发现的创建/替换路径。

## 带 Job 等待、超时、取消和失败恢复的部署

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#kubernetes-wait-and-cleanup{csharp}

- **命名空间**：`Namespace` 为未声明 `metadata.namespace` 的命名空间级清单提供默认值，`CreateNamespace = true` 在目标命名空间缺失时自动创建。清单显式声明的命名空间仍优先，集群级资源不附带命名空间。
- **就绪等待与 Job**：`Wait = true` 以 3 秒间隔轮询 12 种已支持就绪判断的资源类型；其余类型（如 `ConfigMap`、`Secret`、`Ingress`、`CronJob`、`autoscaling/v1` HPA 以及自定义资源实例）提交后即视为就绪，不会轮询其状态。`WaitForJobs = true` 会额外等待 `batch/v1` Job 完成，且要求启用 `Wait = true` 或 `Atomic = true`。
- **CrashLoopBackOff 语义（[#259](https://github.com/GaTTGeng/HelmSharp/issues/259)）**：报告 `CrashLoopBackOff` 的 Pod 会继续保持等待，直到出现 `Ready=True` 或超时。若在截止时间前恢复，等待成功；否则报告超时并附带最后观察到的状态（`Pod in CrashLoopBackOff`）。只有 Pod 进入 `Failed` 阶段才视为终止性 Pod 失败。
- **超时与调用方取消**：单个正整数 `TimeoutSeconds` 覆盖 Chart 加载、hook、提交和就绪等待。调用方取消始终作为 `OperationCanceledException` 传播。操作超时会表现为 `TimeoutException`（等待器截止时间到期）或 `cancellationToken.IsCancellationRequested == false` 的 `OperationCanceledException`（关联的操作超时令牌在 API 读取或轮询等待期间触发）。
- **失败恢复**：`Atomic = true` 与 `CleanupOnFail = true` 会先持久化 `failed` revision 并执行恢复，再重新抛出原始异常。对于已有先前已部署 revision 的失败升级，`CleanupOnFail` 会删除本次失败尝试新增的资源，`Atomic` 还会恢复先前已部署的清单；对于失败的首次安装，两者都会删除本次尝试提交的清单。

## 使用 Foreground 传播策略与删除等待安全卸载

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#kubernetes-safe-cleanup{csharp}

`UninstallAsync` 会按清单逆序删除 release 资源，对直接删除请求遵守 `helm.sh/resource-policy: keep`，并在 `Wait = true` 时通过 `KubernetesResourceWaiter.WaitForDeletedAsync` 持续轮询，直到每个已删除资源均已消失。设置 `KeepHistory = true` 会记录 `uninstalled` revision，而不是清除 release 历史。

完整的逐类型就绪谓词、hook 等待与清理边界以及测试证据见 [Kubernetes 兼容性契约](../helm-compatibility.md#kubernetes-就绪、等待、超时与取消契约)、[Action API](../api/generated/action.md) 与 [Kube API](../api/generated/kube.md)。
