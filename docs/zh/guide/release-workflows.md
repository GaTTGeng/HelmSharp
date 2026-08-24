# 安装和升级 Release

当一个组件既拥有部署动作、又不能只停留在 YAML 预览时，使用 `HelmSharp.Action`。`HelmClient.UpgradeInstallAsync` 会组合 Chart 加载、values、渲染、hook、Kubernetes 提交、可选的就绪等待，以及 release 历史持久化。

```powershell
dotnet add package HelmSharp.Action --version 1.3.2
```

## 先从试运行开始

将 `HelmClient` 交给应用自己的 `IHelmOptionsProvider` 创建。命名空间、field manager、Kubernetes 版本、API 版本和超时等默认值应在这里集中管理，而不是由每个调用方各自猜测。

```csharp
var request = new HelmUpgradeInstallRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = chartPath,
    ValuesFiles = ["values.production.yaml"],
    CreateNamespace = true,
    Wait = true,
    TimeoutSeconds = 300,
    DryRun = true
};

var result = await client.UpgradeInstallAsync(request, cancellationToken);

if (!result.Succeeded)
{
    logger.LogWarning("Release preview failed: {Error}", result.StandardError);
    return;
}

Console.WriteLine(result.StandardOutput);
```

试运行会渲染并校验请求，但不会提交资源，也不会创建 release revision。这个简短示例适合一次性预览。目标 release 可能已有历史时，审批预览还必须从完整历史派生 `DryRunIsUpgrade` 与 `DryRunRevision`；写入审批记录前，请使用[状态感知的从评审到部署示例](../examples/dry-run-deployment.md)。`ReuseValues` 不支持与 `DryRun` 同时使用；审批工作流应在渲染前自行解析并持久化已存储的生效 values。

## 提交已经审批的请求

获得明确审批后，根据记录的输入重建请求，并通过[状态感知的从评审到部署示例](../examples/dry-run-deployment.md)提交。不要在预览和提交之间悄悄改变 values、Chart 版本、目标命名空间、capabilities 输入或已解析出的 release 状态。`HelmUpgradeInstallRequest` 是可变对象，因此不要在预览与提交操作之间共享该请求。

## 明确设置生命周期行为

| 设置 | 含义 |
| --- | --- |
| `Install = false` | 找不到 release 即失败；适用于只允许升级的接口。 |
| `ReuseValues = true` | 从已存储的 release values 开始，再覆盖本请求 values。它不能与 `DryRun` 同时使用；审批预览前应自行解析这些 values。 |
| `ResetValues = true` | 从 Chart 默认值开始，不能和 `ReuseValues` 同时使用。 |
| `Wait = true` | 提交后等待已支持资源就绪。 |
| `WaitForJobs = true` | 同时等待 Job，需要 `Wait` 或 `Atomic`。 |
| `TimeoutSeconds` | Kubernetes 提交、hook、就绪等待和取消共用的上限。 |
| `Atomic = true` | 等待并在失败时恢复。 |
| `DisableHooks = true` | 不执行 Chart hook。 |
| `MaxHistory` | 最多保留多少个 revision；`0` 表示不限制。 |

HelmSharp 将成功、被 supersede、失败以及保留卸载记录的 revision 存在 Kubernetes Secret 中。默认卸载会清除 release 历史；保留历史的卸载会写入一个 `uninstalled` revision。用 `StatusAsync`、`HistoryAsync`、`GetManifestAsync`、`GetValuesAsync` 和按 revision 查询的方法读取真正保存的记录；查询不会重新渲染当前 Chart。

## 控制卸载与 rollback 清理

```csharp
var result = await client.UninstallAsync(new HelmUninstallRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    KeepHistory = true,
    Wait = true,
    TimeoutSeconds = 300,
    DeletionPropagation = HelmDeletionPropagation.Foreground
}, cancellationToken);
```

卸载会按逆序删除常规清单资源。`DeletionPropagation` 默认为 `Background`；`Foreground` 会要求 Kubernetes 在阻塞 dependent 全部消失前保留 owner，`Orphan` 则保留 dependent。`Wait = true` 还会轮询，直到每个已请求删除的资源都不存在。对象已经不存在视为成功；发现、权限或其他 API 失败会停止清理并指出受影响资源。HelmSharp 不会直接向带有 `helm.sh/resource-policy: keep` 的资源发送删除请求，之后 release 历史再根据 `KeepHistory` 选择清除或标记为已卸载。与 Helm 一样，如果该资源的命名空间或 owner 被删除，此注解无法阻止 Kubernetes 的级联删除。

Rollback 会先提交目标 revision，再以 background propagation 逆序删除仅存在于当前 revision 的资源；HelmSharp 不会直接删除带有 `helm.sh/resource-policy: keep` 的资源，但同样受命名空间和 owner 级联删除限制。只有这些清理成功后才执行 post-rollback hook。

## Hook 和就绪等待属于一次操作

Hook 先按 weight、再按名称运行。Job 和 Pod hook 会在超时内观察完成状态，其他 hook 类型会被提交但不会有完成状态观察。支持的清理策略是 `before-hook-creation`、`hook-succeeded` 和 `hook-failed`；未声明策略时默认使用 `before-hook-creation`。清理会等待 Kubernetes 确认 hook 对象已不存在后再继续。同一事件批次中，带 `hook-succeeded` 的资源会保留给后续 hook 使用；整批全部成功后才按执行顺序的逆序删除。如果后续 hook 失败或操作被取消，HelmSharp 会先终结清理此前已成功的 hook，再返回原始失败。若 `before-hook-creation` 删除失败，将阻止可能冲突的 hook 创建；若 `hook-succeeded` 清理失败，本次操作也会失败。失败或取消后的整批终结清理共享一个独立且有界的时间窗口，并会在单个删除失败后继续尝试其余 hook；若清理失败，HelmSharp 会保留原始 hook 异常，并把单个或聚合清理异常附加到 `Exception.Data["HelmSharp.HookCleanupError"]`。`DisableHooks = true` 会同时跳过 hook 执行和 hook 清理。Hook 清理使用 background propagation，且不会删除 release 的常规清单。为保持 Helm 兼容并避免级联删除自定义资源，删除策略永远不会删除 `CustomResourceDefinition` hook。

内置就绪等待器覆盖常见工作负载。CRD 可以被提交，但不会自动推导其领域就绪语义。当 Kubernetes 接受对象还不足以说明部署可用时，应添加产品自己的健康检查。

## 权限和错误处理

Kubernetes 身份需要目标资源种类、命名空间、所用 CRD、hook 和 release Secret 的权限。高层操作既可能返回 `CommandResult`，也可能抛异常；返回结果时检查 `Succeeded`、`ExitCode`、`StandardOutput` 和 `StandardError`，并在服务边界捕获和记录异常。[排查失败](error-handling.md)说明了两类失败模型以及应保留哪些诊断上下文。
