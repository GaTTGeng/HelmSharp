# 把评审结果变成部署

评审和实际提交各使用一个 release 请求。渲染评审前，应从应用自己持久化的 release store 读取当前 release 状态，让预览和实际提交使用相同的安装/升级状态及下一 revision。审批后只能改变 `DryRun`；Chart 标识、values、命名空间、release 状态和 options provider 配置必须保持不变。

```csharp
var currentRelease = await releases.GetLatestAsync(
    releaseName,
    targetNamespace,
    cancellationToken);

var preview = new HelmUpgradeInstallRequest
{
    ReleaseName = releaseName,
    Namespace = targetNamespace,
    Chart = approvedChartPath,
    ValuesFiles = approvedValuesFiles,
    CreateNamespace = true,
    Wait = true,
    TimeoutSeconds = 300,
    DryRun = true,
    DryRunIsUpgrade = currentRelease is not null,
    DryRunRevision = currentRelease is null ? 1 : currentRelease.Revision + 1
};

var previewResult = await client.UpgradeInstallAsync(preview, cancellationToken);
if (!previewResult.Succeeded)
    return Results.BadRequest("Release 预览失败。");

await approvals.SaveAsync(preview, previewResult.StandardOutput, cancellationToken);
```

审批服务校验该记录后，根据持久化字段重建请求并提交：

```csharp
var apply = await approvals.CreateApprovedRequestAsync(approvalId, cancellationToken);
apply.DryRun = false;

var result = await client.UpgradeInstallAsync(apply, cancellationToken);
if (!result.Succeeded)
    return Results.Problem("已审批的 release 无法提交。", statusCode: 409);

return Results.Ok(new { result.StandardOutput });
```

不要相信浏览器第二次提交的 values 或 Chart 版本。执行提交的服务应当自己读取已评审记录。将预览时解析出的 release 状态和审批记录一起保存；如果提交前 release 状态已变化，就拒绝提交，或重新渲染并要求新的审批。把 `CommandResult.StandardError`、退出码和操作 ID 保存到受限的操作记录中；对不受信任的调用方只返回通用失败信息。

`Wait`、`WaitForJobs`、`Atomic` 和 `TimeoutSeconds` 共同决定一次操作何时算完成。选择它们之前，请阅读[安装和升级 Release](../guide/release-workflows.md)。
