# 把评审结果变成部署

评审和实际提交各使用一个 release 请求。关键约束是：审批后只能改变 `DryRun`，Chart 标识、values、命名空间和 options provider 配置必须保持不变。

```csharp
var preview = new HelmUpgradeInstallRequest
{
    ReleaseName = releaseName,
    Namespace = targetNamespace,
    Chart = approvedChartPath,
    ValuesFiles = approvedValuesFiles,
    CreateNamespace = true,
    Wait = true,
    TimeoutSeconds = 300,
    DryRun = true
};

var previewResult = await client.UpgradeInstallAsync(preview, cancellationToken);
if (!previewResult.Succeeded)
    return Results.BadRequest(previewResult.StandardError);

await approvals.SaveAsync(preview, previewResult.StandardOutput, cancellationToken);
```

审批服务校验该记录后，根据持久化字段重建请求并提交：

```csharp
var apply = await approvals.CreateApprovedRequestAsync(approvalId, cancellationToken);
apply.DryRun = false;

var result = await client.UpgradeInstallAsync(apply, cancellationToken);
if (!result.Succeeded)
    return Results.Problem(result.StandardError, statusCode: 409);

return Results.Ok(new { result.StandardOutput });
```

不要相信浏览器第二次提交的 values 或 Chart 版本。执行提交的服务应当自己读取已评审记录。失败时保留 `CommandResult.StandardError`、退出码和操作 ID，不要把 Kubernetes、hook 或模板诊断压缩为笼统的 HTTP 500。

`Wait`、`WaitForJobs`、`Atomic` 和 `TimeoutSeconds` 共同决定一次操作何时算完成。选择它们之前，请阅读[安装和升级 Release](../guide/release-workflows.md)。
