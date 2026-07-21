![HelmSharp](https://raw.githubusercontent.com/GaTTGeng/HelmSharp/master/docs/public/wordmark.svg)

[![CI](https://github.com/GaTTGeng/HelmSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/GaTTGeng/HelmSharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/HelmSharp.Action.svg)](https://www.nuget.org/packages/HelmSharp.Action)
[![NuGet Downloads](https://img.shields.io/nuget/dt/HelmSharp.Action.svg)](https://www.nuget.org/packages/HelmSharp.Action)
[![License](https://img.shields.io/github/license/GaTTGeng/HelmSharp.svg)](LICENSE)

[English](README.md)

HelmSharp 是一个面向 .NET 的托管 Helm 风格库，用于在不调用 `helm` 可执行文件的情况下渲染 Helm 风格 Chart 并驱动 Kubernetes Release 工作流。它适合需要在 .NET 进程内完成模板渲染、values 合并、Chart 打包、仓库操作和 Kubernetes 发布生命周期管理的应用。

项目仍在积极开发中。M1（Helm 模板对齐）和 M2（Chart 打包和仓库对齐）均已完成，渲染器会持续通过聚焦测试用 Chart 和选定公开 Helm Chart 校验；后续里程碑将扩展发布生命周期和 Kubernetes 操作。

最新发布版本是 **1.2.0**。下文以及所链接分发指南中的 M2 打包、仓库、拉取和依赖能力已包含在 1.2.0 NuGet 包中。

文档站点：[https://gattgeng.github.io/HelmSharp/](https://gattgeng.github.io/HelmSharp/)

文档站现已包含工作流指南、真实集成示例、逐包说明，以及从公开类型和成员生成的 API 参考页。

## NuGet 包

仓库按职责拆分为多个 NuGet 包：

| 包名 | 作用 |
| --- | --- |
| [`HelmSharp.Action`](https://www.nuget.org/packages/HelmSharp.Action) | 高层 Helm Client API 和 Release 操作。 |
| [`HelmSharp.Chart`](https://www.nuget.org/packages/HelmSharp.Chart) | Chart 加载、values 合并、YAML 帮助方法和元数据。 |
| [`HelmSharp.Engine`](https://www.nuget.org/packages/HelmSharp.Engine) | Helm 风格模板渲染。 |
| [`HelmSharp.Kube`](https://www.nuget.org/packages/HelmSharp.Kube) | Kubernetes manifest apply/delete/wait 帮助方法。 |
| [`HelmSharp.Release`](https://www.nuget.org/packages/HelmSharp.Release) | 基于 Kubernetes Secret 的 Helm Release 记录存储。 |
| [`HelmSharp.Repo`](https://www.nuget.org/packages/HelmSharp.Repo) | Chart 仓库、索引、拉取和搜索能力。 |
| [`HelmSharp.Registry`](https://www.nuget.org/packages/HelmSharp.Registry) | Registry 相关扩展点包。 |
| [`HelmSharp.Storage`](https://www.nuget.org/packages/HelmSharp.Storage) | Storage 相关扩展点包。 |
| [`HelmSharp.PostRenderer`](https://www.nuget.org/packages/HelmSharp.PostRenderer) | Post-renderer 扩展点包。 |

大多数应用可以从 `HelmSharp.Action` 开始。

## 环境要求

- 本地构建全部目标框架需要 .NET 8、.NET 9 和 .NET 10 SDK。
- 安装、升级、回滚、卸载和查询 Release 时需要 Kubernetes 集群和 kubeconfig。
- 不需要安装 `helm` 命令行工具。
- NuGet 包支持的目标框架：`net8.0`、`net9.0`、`net10.0`。
- 当前不支持 .NET Framework，除非后续版本新增 `netstandard` target。

## 安装

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

更多完整示例见 [examples](examples/README.md)。

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

以下列表描述当前 `master` 分支。通过 NuGet 使用 M2 API 前，请先阅读上面的版本说明。

- 从目录和 `.tgz` 归档加载 Chart。
- 支持 `values.yaml`、内联 values、`--set`、`--set-string`、`--set-json`、`--set-file` 风格的值覆盖。
- 支持常见控制流和函数的 Helm 风格模板渲染。
- 兼容 Helm 的 Chart 打包，包括 `.helmignore`、version/appVersion 覆盖、依赖和安全归档布局。
- 传统 HTTP 仓库配置/缓存管理、索引生成与合并、离线搜索、语义版本拉取、摘要校验和安全解压。
- 使用兼容 Helm 的 `Chart.lock` 执行依赖 list/update/build，并支持仓库别名、Chart 别名和本地 `file://` 依赖。
- 对常见 Kubernetes 资源执行托管 apply/delete/wait。
- 使用 Kubernetes Secret 保存 Release 历史。
- 提供 install、upgrade、uninstall、rollback、status、history、manifest、values、hooks、notes 和 test 相关 API。

完整示例见 [Chart 打包与仓库工作流指南](docs/zh/guide/chart-distribution.md)。OCI 与来源证明对齐将单独推进。

## 兼容性验证

HelmSharp 使用聚焦测试用 Chart 和选定公开 Chart，将托管渲染结果与 `helm template` 比对。这些测试可防止已覆盖行为回归，但不代表所有 Helm Chart 或所有 Sprig 函数都已认证兼容。

支持范围、已知边界以及生产 Chart 的验证建议，请见 [Helm 兼容性](docs/helm-compatibility.md)。

## 当前边界

HelmSharp 不是完整的 Helm CLI 克隆。尚未实现的模板函数会产生包含模板路径的渲染诊断，而不会被静默替代。对于静态客户端未包含的 Kubernetes 资源，apply/delete 会从目标集群发现资源类型；就绪判断仍只覆盖选定的资源类型。高级 Helm 行为、插件、provenance 校验和 OCI 认证流程仍可能需要补充实现。

## 文档

- [文档站点](https://gattgeng.github.io/HelmSharp/)
- [入门指南](docs/getting-started.md)
- [指南：安装与渲染](docs/guide/installation.md)
- [示例：渲染预览 API](docs/examples/render-preview-api.md)
- [包指南：HelmSharp.Action](docs/packages/action.md)
- [生成 API 参考](docs/api/index.md)
- [API 概览](docs/api-overview.md)
- [Helm 兼容性与验证](docs/helm-compatibility.md)
- [路线图](docs/roadmap.md)
- [支持政策](SUPPORT.md)
- [GitHub Releases](https://github.com/GaTTGeng/HelmSharp/releases)

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

- `.github/workflows/ci.yml` 会在推送和拉取请求时执行 restore、build、test、pack，并上传包产物。
- `.github/workflows/deploy-docs.yml` 会在推送到 `master` 时构建 VitePress 文档站点并部署到 GitHub Pages。
- `.github/workflows/release-nuget.yml` 会打 Release 包，并可发布到 NuGet.org。

NuGet.org 发布由维护者通过发布工作流处理。

## 贡献

请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 安全问题

请私下报告安全问题，详见 [SECURITY.md](SECURITY.md)。

## 许可证

HelmSharp 使用 [MIT License](LICENSE)。
