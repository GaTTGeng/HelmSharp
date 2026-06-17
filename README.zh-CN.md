# HelmSharp

[English](README.md)

HelmSharp 是一个面向 .NET 的托管 Helm 风格库，用于在不调用 `helm` 可执行文件的情况下渲染 Helm 风格 Chart 并驱动 Kubernetes Release 工作流。它适合需要在 .NET 进程内完成模板渲染、values 合并、Chart 打包、仓库操作和 Kubernetes 发布生命周期管理的应用。

项目仍在积极开发中。当前目标是实现实用的 Helm 兼容子集，而不是逐字节复刻 Helm CLI 的全部行为。

## NuGet 包

仓库按职责拆分为多个 NuGet 包：

| 包名 | 作用 |
| --- | --- |
| `HelmSharp.Action` | 高层 Helm Client API 和 Release 操作。 |
| `HelmSharp.Chart` | Chart 加载、values 合并、YAML 帮助方法和元数据。 |
| `HelmSharp.Engine` | Helm 风格模板渲染。 |
| `HelmSharp.Kube` | Kubernetes manifest apply/delete/wait 帮助方法。 |
| `HelmSharp.Release` | 基于 Kubernetes Secret 的 Helm Release 记录存储。 |
| `HelmSharp.Repo` | Chart 仓库、索引、拉取和搜索能力。 |
| `HelmSharp.Registry` | Registry 相关扩展点包。 |
| `HelmSharp.Storage` | Storage 相关扩展点包。 |
| `HelmSharp.PostRenderer` | Post-renderer 扩展点包。 |

大多数应用可以从 `HelmSharp.Action` 开始。

## 环境要求

- 本地构建全部目标框架需要 .NET 8、.NET 9 和 .NET 10 SDK。
- 安装、升级、回滚、卸载和查询 Release 时需要 Kubernetes 集群和 kubeconfig。
- 不需要安装 `helm` 命令行工具。
- NuGet 包支持的目标框架：`net8.0`、`net9.0`、`net10.0`。
- 当前不支持 .NET Framework，除非后续版本新增 `netstandard` target。

## 安装

包发布到 NuGet.org 后：

```powershell
dotnet add package HelmSharp.Action
```

如果只需要底层能力：

```powershell
dotnet add package HelmSharp.Chart
dotnet add package HelmSharp.Engine
```

## 快速开始

渲染本地 Chart：

```csharp
using HelmSharp.Action;

var client = new HelmClient(new StaticHelmOptionsProvider());

var result = await client.TemplateAsync(new HelmTemplateRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = @"C:\charts\my-chart",
    SetValues = new Dictionary<string, string>
    {
        ["image.tag"] = "1.2.3",
        ["replicaCount"] = "2"
    }
});

Console.WriteLine(result.StandardOutput);

sealed class StaticHelmOptionsProvider : IHelmOptionsProvider
{
    public ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new HelmExecutionOptions
        {
            DefaultNamespace = "default",
            FieldManager = "helmsharp"
        });
}
```

安装或升级 Release：

```csharp
var result = await client.UpgradeInstallAsync(new HelmUpgradeInstallRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = @"C:\charts\my-chart",
    CreateNamespace = true,
    Wait = true,
    TimeoutSeconds = 300
});

if (result.ExitCode != 0)
{
    Console.Error.WriteLine(result.StandardError);
}
else
{
    Console.WriteLine(result.StandardOutput);
}
```

执行不会修改集群的 dry run：

```csharp
await foreach (var line in client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = @"C:\charts\my-chart",
    DryRun = true
}))
{
    Console.WriteLine(line);
}
```

## 已支持能力

- 从目录和 `.tgz` 归档加载 Chart。
- 支持 `values.yaml`、内联 values、`--set`、`--set-string`、`--set-json`、`--set-file` 风格的值覆盖。
- 支持常见控制流和函数的 Helm 风格模板渲染。
- 创建 Chart 包。
- 生成仓库索引、搜索和拉取 Chart。
- 对常见 Kubernetes 资源执行托管 apply/delete/wait。
- 使用 Kubernetes Secret 保存 Release 历史。
- 提供 install、upgrade、uninstall、rollback、status、history、manifest、values、hooks、notes 和 test 相关 API。

## 当前边界

HelmSharp 不是完整的 Helm CLI 克隆。一些高级 Helm 行为、模板函数边界情况、插件、完整 provenance 校验流程、OCI 认证流程以及不常见 Kubernetes 资源类型仍可能需要补充实现。欢迎用聚焦的测试补齐兼容性。

## 构建

```powershell
dotnet restore HelmSharp.sln
dotnet build HelmSharp.sln --configuration Release --no-restore
dotnet test HelmSharp.sln --configuration Release --no-build --no-restore
```

## 打包

```powershell
dotnet pack HelmSharp.sln --configuration Release --no-build --output artifacts/packages
```

NuGet 包元数据定义在 `src/Directory.Build.props`。英文 README 会被打入 NuGet 包。

## 自动化构建与发布

仓库已包含 GitHub Actions 工作流：

- `.github/workflows/ci.yml` 会在 push 和 pull request 时 restore、build、test、pack，并上传包产物。
- `.github/workflows/release-nuget.yml` 会打 Release 包，并可发布到 NuGet.org。

要发布到 NuGet.org，请在 GitHub 仓库 Secrets 中创建 `NUGET_API_KEY`，值为 NuGet API Key。之后推送 `v1.0.0` 这类 tag，或者手动运行 release workflow。

## 贡献

请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 安全问题

请私下报告安全问题，详见 [SECURITY.md](SECURITY.md)。

## 许可证

HelmSharp 使用 [MIT License](LICENSE)。
