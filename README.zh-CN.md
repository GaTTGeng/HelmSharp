# HelmSharp

[![CI](https://github.com/GaTTGeng/HelmSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/GaTTGeng/HelmSharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/HelmSharp.Action.svg)](https://www.nuget.org/packages/HelmSharp.Action)
[![NuGet Downloads](https://img.shields.io/nuget/dt/HelmSharp.Action.svg)](https://www.nuget.org/packages/HelmSharp.Action)
[![License](https://img.shields.io/github/license/GaTTGeng/HelmSharp.svg)](LICENSE)

[English](README.md)

HelmSharp 是一个面向 .NET 的托管 Helm 风格库，用于在不调用 `helm` 可执行文件的情况下渲染 Helm 风格 Chart 并驱动 Kubernetes Release 工作流。它适合需要在 .NET 进程内完成模板渲染、values 合并、Chart 打包、仓库操作和 Kubernetes 发布生命周期管理的应用。

项目仍在积极开发中。当前目标是实现实用的 Helm 兼容子集，而不是逐字节复刻 Helm CLI 的全部行为。

文档站点：[https://gattgeng.github.io/HelmSharp/](https://gattgeng.github.io/HelmSharp/)

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

- 从目录和 `.tgz` 归档加载 Chart。
- 支持 `values.yaml`、内联 values、`--set`、`--set-string`、`--set-json`、`--set-file` 风格的值覆盖。
- 支持常见控制流和函数的 Helm 风格模板渲染。
- 创建 Chart 包。
- 生成仓库索引、搜索和拉取 Chart。
- 对常见 Kubernetes 资源执行托管 apply/delete/wait。
- 使用 Kubernetes Secret 保存 Release 历史。
- 提供 install、upgrade、uninstall、rollback、status、history、manifest、values、hooks、notes 和 test 相关 API。

## Golden Test 结果

HelmSharp 的模板引擎持续通过真实世界的公开 Helm Chart 进行 golden test 验证。每个 Chart 分别由 `helm template`（参照）和 HelmSharp 托管渲染器渲染，输出经规范化后逐文档比对。

> **更新日期：** 2026-06-29 · **HelmSharp 版本：** 1.0.4 + Unreleased · **Helm 版本：** v3.12.3 · **测试框架：** net8.0、net9.0、net10.0

CI 会安装 Helm v3.12.3，先运行常规 Release 测试套件，再单独运行覆盖 fixture chart 和真实公共 chart 的 Helm golden test 步骤。真实 chart 步骤会写出 `GoldenReports/*.json` artifact，便于按 chart 查看覆盖结果。

### 汇总

| Chart | 版本 | Helm 文档数 | 模板数 | 通过 | 失败 | 逐模板通过率 | 完整渲染 | 判定 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **podinfo** | 6.14.0 | 5 | 21 | 21 | 0 | 100% | ✅ 成功 | **Partial** |
| **metrics-server** | 3.13.1 | 9 | 18 | 18 | 0 | 100% | ✅ 成功 | **Partial** |
| **external-dns** | 1.21.1 | 5 | 7 | 7 | 0 | 100% | ✅ 成功 | **Partial** |
| **ingress-nginx** | 4.12.1 | 19 | 42 | 40 | 2 | 95% | ❌ 降级 | **Partial** |
| **cert-manager** | 1.17.1 | 52 | 41 | 41 | 0 | 100% | ✅ 成功 | **Partial** |
| **总计** | — | **90** | **129** | **127** | **2** | **98%** | — | — |

### 逐 Chart 明细

```
podinfo          █████████████████████  100%  (21/21 模板)
metrics-server   █████████████████████  100%  (18/18 模板)
external-dns     █████████████████████  100%  ( 7/ 7 模板)
ingress-nginx    ████████████████████░   95%  (40/42 模板)
cert-manager     █████████████████████  100%  (41/41 模板)
                 ─────────────────────
                 ████████████████████░   98%  (127/129 模板总计)
```

### 错误分析

5 个真实 Chart 中有 127/129 个模板可以成功渲染。4 个 chart 可完成完整 Chart 渲染；`ingress-nginx` 当前会降级到逐模板报告，因为 `controller-deployment.yaml` 和 `controller-role.yaml` 在托管渲染时触发 `NotSupportedException`。剩余差异可归因于：

| 类别 | 影响 | 涉及 Chart |
| --- | --- | --- |
| 尚未支持的 renderer 路径 | 两个模板被报告为逐模板失败 | ingress-nginx |
| 空白符 / 文档格式 | YAML 缩进和空行存在轻微渲染差异 | 全部 5 个 Chart |
| Values 求值边界情况 | 部分条件分支产生不同输出 | cert-manager、ingress-nginx |
| Printf / 字符串格式化 | 格式化字符串输出有细微差异 | metrics-server、external-dns |

**近期解析器修复：** 此前导致 7 个模板抛出 `NotSupportedException` 的两个解析器 bug 已修复：
- **#51** — `SplitPipeline` 现已跟踪括号深度，修复了 `(empty .x)` 和 `($value | quote | len)` 模式。
- **#50** — `else if` 链重建现已正确生成平衡的模板块，修复了 C# 字符串插值转义（`{{- end }}` vs `{- end }`）。

### 判定图例

| 判定 | 含义 |
| --- | --- |
| **Pass** | 规范化后逐字节一致（换行符、源注释）。 |
| **Partial** | 结构兼容——文档数量相同，或大多数独立模板渲染正确，少数存在已知解析器缺口。 |
| **Fail** | 渲染器无法为该 Chart 中任何模板生成输出。 |

## 当前边界

HelmSharp 不是完整的 Helm CLI 克隆。一些高级 Helm 行为、模板函数边界情况、插件、完整 provenance 校验流程、OCI 认证流程以及不常见 Kubernetes 资源类型仍可能需要补充实现。欢迎用聚焦的测试补齐兼容性。

## 文档

- [文档站点](https://gattgeng.github.io/HelmSharp/)
- [入门指南](docs/getting-started.md)
- [API 概览](docs/api-overview.md)
- [Helm 兼容性](docs/helm-compatibility.md)
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

- `.github/workflows/ci.yml` 会在 push 和 pull request 时 restore、build、test、pack，并上传包产物。
- CI workflow 会安装 Helm v3.12.3，并在 Helm 可用时显式运行 Helm CLI golden tests，上传 `.trx` 测试结果和真实 chart JSON 报告。
- `.github/workflows/deploy-docs.yml` 会在 push 到 `master` 时构建 VitePress 文档站点并部署到 GitHub Pages。
- `.github/workflows/release-nuget.yml` 会打 Release 包，并可发布到 NuGet.org。

NuGet.org 发布由维护者通过 release workflow 处理。

## 贡献

请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 安全问题

请私下报告安全问题，详见 [SECURITY.md](SECURITY.md)。

## 许可证

HelmSharp 使用 [MIT License](LICENSE)。
