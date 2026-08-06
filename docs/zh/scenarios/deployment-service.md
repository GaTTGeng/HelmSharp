# Kubernetes 部署服务

当服务负责 release 生命周期操作以及执行这些操作的 Kubernetes 身份时，使用 `HelmSharp.Action`。

## 服务责任

部署服务选择 Chart 版本和命名空间，提供 Kubernetes 凭据，应用授权策略，保存操作诊断，并向调用方公开状态和历史。`HelmClient` 在该边界内执行 Helm 风格的生命周期操作。

## 建议流程

1. 从受信任且已版本化的应用记录中解析 Chart 和 values。
2. 创建带有明确命名空间、等待、超时和 hook 策略的 `HelmUpgradeInstallRequest`。
3. 需要时先执行 dry run，供操作人员预览。
4. 从保存的输入重建 apply 请求；不要复用可变的预览请求。
5. 将 `CommandResult` 和 release 历史写入操作记录。

先阅读[安装和升级 Release](../guide/release-workflows.md)。审批系统请使用[把评审结果变成部署](../examples/dry-run-deployment.md)，其中涵盖 release 状态校验和不可变输入。

## 不该使用此层的情况

如果其他控制器负责集群变更，应为该控制器渲染清单。请看[为 GitOps 生成清单](../examples/gitops-pr-generator.md)。
