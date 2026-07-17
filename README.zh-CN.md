<h1 align="center">
  <img src="https://raw.githubusercontent.com/GaTTGeng/HelmSharp/master/docs/public/wordmark.svg" alt="HelmSharp" width="352">
</h1>

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

HelmSharp 的模板引擎持续通过基准输出测试（golden tests）验证。测试套件包含聚焦测试用 Chart 和选定公开 Helm Chart。下表中的每个公开 Chart 都会分别由 `helm template`（参照）和 HelmSharp 托管渲染器渲染，输出经规范化后逐文档比对。

这些结果是表格中固定 Chart 版本的兼容性信号，不是对整个 Helm 生态所有 Chart 的认证。如果你的 Chart 依赖少见 Helm 行为，仍应单独验证。

> **更新日期：** 2026-07-16 · **HelmSharp 版本：** 1.2.0 · **Helm 版本：** v4.2.2 · **测试框架：** net10.0

### 汇总

| Chart | 版本 | Helm 文档数 | 模板数 | 通过 | 失败 | 逐模板通过率 | 完整渲染 | 判定 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **podinfo** | 6.14.0 | 5 | 21 | 21 | 0 | 100% | ✅ 成功 | **Pass** |
| **metrics-server** | 3.13.1 | 9 | 18 | 18 | 0 | 100% | ✅ 成功 | **Pass** |
| **external-dns** | 1.21.1 | 5 | 7 | 7 | 0 | 100% | ✅ 成功 | **Pass** |
| **ingress-nginx** | 4.12.1 | 19 | 42 | 42 | 0 | 100% | ✅ 成功 | **Pass** |
| **cert-manager** | 1.17.1 | 52 | 41 | 41 | 0 | 100% | ✅ 成功 | **Pass** |
| **总计** | — | **90** | **129** | **129** | **0** | **100%** | — | — |

### 逐 Chart 明细

```
podinfo          █████████████████████  100%  (21/21 模板)
metrics-server   █████████████████████  100%  (18/18 模板)
external-dns     █████████████████████  100%  ( 7/ 7 模板)
ingress-nginx    █████████████████████  100%  (42/42 模板)
cert-manager     █████████████████████  100%  (41/41 模板)
                 ─────────────────────
                 █████████████████████  100%  (129/129 模板总计)
```

### 错误分析

对于上表列出的固定版本公开 Chart，全部 129 个模板都能在无解析器异常的情况下渲染，并在规范化后与 `helm template` 匹配。这是已覆盖 Chart 的当前兼容性信号，不代表所有 Helm Chart 都只使用已覆盖行为。

自 1.0.3 版本以来关闭的关键对齐里程碑：

- **#109 / #111 / #113** — Block 右 trim 现已保留 action 行缩进，匹配 Go `text/template` 行为；cert-manager 基准输出测试从 45/52 恢复至 52/52 完全一致。
- **#112** — `ParseDefine` 现已对 define body 应用右 trim，匹配 `ParseBlock` 行为。
- **#97** — 完成 Sprig 函数对齐（`empty`、`keys`、`mergeOverwrite`、`mustRegexMatch`、`mustRegexReplaceAll` 等）。
- **#102** — 解决 cert-manager 剩余内容差异（YAML tag、八进制值、merge key、block scalar、注释裁剪）。
- **#96 / #99 / #108** — 解决选定公开 Chart 基准输出测试的内容差异，全部五个 Chart 达到 Pass 判定。

### 判定图例

| 判定 | 含义 |
| --- | --- |
| **Pass** | 被测 Chart 输出在规范化后逐字节一致（换行符、源注释）。 |
| **Partial** | 结构兼容——文档数量相同，或大多数独立模板渲染正确，少数存在已知解析器缺口。 |
| **Fail** | 渲染器无法为该 Chart 中任何模板生成输出。 |

## 当前边界

HelmSharp 不是完整的 Helm CLI 克隆。一些高级 Helm 行为、模板函数边界情况、插件、完整 provenance 校验流程、OCI 认证流程以及不常见 Kubernetes 资源类型仍可能需要补充实现。欢迎用聚焦的测试补齐兼容性。

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
