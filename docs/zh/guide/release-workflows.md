# 安装和升级 Release

当一个组件既拥有部署动作、又不能只停留在 YAML 预览时，使用 `HelmSharp.Action`。`HelmClient.UpgradeInstallAsync` 会组合 Chart 加载、values、渲染、hook、Kubernetes 提交、可选的就绪等待，以及 release 历史持久化。

```powershell
dotnet add package HelmSharp.Action --version 1.3.1
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

试运行会渲染并校验请求，但不会提交资源，也不会创建 release revision。在产品中，应把预览、values 输入与结果一起纳入审批记录。

## 提交已经审批的请求

获得明确审批后，用 `DryRun = false` 再执行同一请求。不要在预览和提交之间悄悄改变 values、Chart 版本、目标命名空间或 capabilities 输入。`HelmUpgradeInstallRequest` 是可变对象：要么根据已审批的数据创建新请求，要么只在实例未被共享时修改标记。

```csharp
request.DryRun = false;
var applyResult = await client.UpgradeInstallAsync(request, cancellationToken);

if (!applyResult.Succeeded)
    throw new InvalidOperationException(applyResult.StandardError);
```

## 明确设置生命周期行为

| 设置 | 含义 |
| --- | --- |
| `Install = false` | 找不到 release 即失败；适用于只允许升级的接口。 |
| `ReuseValues = true` | 从已存储的 release values 开始，再覆盖本请求 values。 |
| `ResetValues = true` | 从 Chart 默认值开始，不能和 `ReuseValues` 同时使用。 |
| `Wait = true` | 提交后等待已支持资源就绪。 |
| `WaitForJobs = true` | 同时等待 Job，需要 `Wait` 或 `Atomic`。 |
| `TimeoutSeconds` | Kubernetes 提交、hook、就绪等待和取消共用的上限。 |
| `Atomic = true` | 等待并在失败时恢复。 |
| `DisableHooks = true` | 不执行 Chart hook。 |
| `MaxHistory` | 最多保留多少个 revision；`0` 表示不限制。 |

HelmSharp 将成功、被 supersede、失败以及保留卸载记录的 revision 存在 Kubernetes Secret 中。默认卸载会清除 release 历史；保留历史的卸载会写入一个 `uninstalled` revision。用 `StatusAsync`、`HistoryAsync`、`GetManifestAsync`、`GetValuesAsync` 和按 revision 查询的方法读取真正保存的记录；查询不会重新渲染当前 Chart。

## Hook 和就绪等待属于一次操作

Hook 先按 weight、再按名称运行。Job 和 Pod hook 会在超时内观察完成状态，其他 hook 类型会被提交但不会有完成状态观察。支持的清理策略是 `before-hook-creation`、`hook-succeeded` 和 `hook-failed`。

内置就绪等待器覆盖常见工作负载。CRD 可以被提交，但不会自动推导其领域就绪语义。当 Kubernetes 接受对象还不足以说明部署可用时，应添加产品自己的健康检查。

## 权限和错误处理

Kubernetes 身份需要目标资源种类、命名空间、所用 CRD、hook 和 release Secret 的权限。高层操作返回 `CommandResult`，在向用户报告结果前检查 `Succeeded`、`ExitCode`、`StandardOutput` 和 `StandardError`。[排查失败](error-handling.md)说明了两类失败模型以及应保留哪些诊断上下文。
