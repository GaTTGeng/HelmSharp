# 试运行部署

## 你在解决什么问题

部署产品应拆分预览和提交。HelmSharp 支持用同一个高层发布工作流先 `DryRun = true`，审批后再提交。

## 安装哪些包

```powershell
dotnet add package HelmSharp.Action --version 1.1.1
```

## 完整最小代码

```csharp
var dryRun = await client.UpgradeInstallAsync(new HelmUpgradeInstallRequest
{
    ReleaseName = "payments",
    Namespace = "apps",
    Chart = "/charts/payments",
    ValuesFiles = ["values.production.yaml"],
    Wait = true,
    TimeoutSeconds = 300,
    DryRun = true
}, cancellationToken);

if (dryRun.ExitCode != 0)
    return Results.BadRequest(dryRun.StandardError);

// 用户明确审批后：
var apply = await client.UpgradeInstallAsync(new HelmUpgradeInstallRequest
{
    ReleaseName = "payments",
    Namespace = "apps",
    Chart = "/charts/payments",
    ValuesFiles = ["values.production.yaml"],
    Wait = true,
    WaitForJobs = true,
    TimeoutSeconds = 300,
    DryRun = false
}, cancellationToken);
```

## 关键 API 为什么这样用

`UpgradeInstallAsync` 同时覆盖安装和升级。预览与提交使用相似请求，可以减少用户审核内容和实际部署内容之间的漂移。

## 生产环境注意事项

- 审批记录应包含 Chart 版本、values 输入、发布名称、namespace 和试运行输出哈希。
- 如果 Chart 或 values 可能变化，提交前重新渲染。
- `DryRun = false` 是唯一会修改集群的步骤。

## 下一步

阅读 [发布工作流](../guide/release-workflows.md)。
