---
layout: home

hero:
  name: HelmSharp
  text: 面向 .NET 的 Helm 兼容渲染
  tagline: 在托管 .NET 代码中加载 Chart、合并 values、渲染 Kubernetes 清单并运行发布工作流，运行时不依赖 Helm CLI。
  image:
    src: /logo.svg
    alt: HelmSharp logo
  actions:
    - theme: brand
      text: 开始指南
      link: /zh/getting-started
    - theme: alt
      text: 示例
      link: /zh/examples/render-preview-api
    - theme: alt
      text: 在线对比
      link: /zh/compare

features:
  - title: 托管 Helm 工作流
    details: 通过 .NET SDK 完成渲染、试运行、安装、升级、查看发布状态和 Chart 打包。
  - title: 按工作流组织的文档
    details: 覆盖安装选择、只渲染预览、values 优先级、发布试运行、Kubernetes 提交/等待和错误处理。
  - title: 每个包的使用边界
    details: 每个 NuGet 包都有职责、安装建议、主要类型、常见组合和当前边界说明。
  - title: 可核查的兼容性证据
    details: 测试用 Chart 和选定公开 Chart 的基准输出测试独立呈现，便于用户确认当前覆盖范围和边界。
---

## 眼见为实

上传 Helm Chart，让 HelmSharp 和真实 Helm CLI 并排渲染并对比输出。

<div class="compare-cta">
  <a class="compare-cta-btn" href="./compare">
    <span class="compare-cta-label">打开在线对比</span>
    <span class="compare-cta-arrow">→</span>
  </a>
</div>

## HelmSharp 适合什么

你的 .NET 应用需要 Helm Chart 输出，但不希望在运行时依赖 `helm` 可执行文件。调用方可能是 Web 服务、operator、构建代理、GitOps 生成器，或者需要在触碰集群前预览清单的产品功能。

HelmSharp 提供托管路径：加载 Chart、构建 values、渲染清单、查看 NOTES，并可按需进入 Kubernetes 发布操作。

## 快速示例

```csharp
var chart = await HelmChartLoader.LoadAsync("/charts/my-chart", ct);
var renderer = new HelmTemplateRenderer(chart, "demo", "default", values);
var manifests = renderer.Render();
```

::: details 更喜欢类似 Helm 命令的高层客户端？

需要模板渲染、试运行、安装、升级、卸载、回滚、状态查询、打包、仓库操作和发布历史时，用 `HelmSharp.Action`。

```csharp
using HelmSharp.Action;

var client = new HelmClient(optionsProvider);

var result = await client.TemplateAsync(new HelmTemplateRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = "/charts/my-chart",
    SetValues = new Dictionary<string, string>
    {
        ["image.tag"] = "1.2.3",
        ["replicaCount"] = "2"
    }
});

Console.WriteLine(result.StandardOutput);
```

:::

## 文档路径

| 路径 | 适合场景 |
| --- | --- |
| [快速开始](getting-started.md) | 想最快完成第一次渲染或试运行。 |
| [指南](guide/installation.md) | 想逐步理解安装、values 配置、渲染、发布、Kubernetes 操作和错误处理。 |
| [示例](examples/render-preview-api.md) | 想看真实集成模式。 |
| [包](packages/action.md) | 需要选择 NuGet 包边界。 |
| [API 参考](api/index.md) | 需要从源码生成的公开成员索引。 |

## 安装

多数应用从高层包开始：

```powershell
dotnet add package HelmSharp.Action --version 1.2.0
```

如果只需要渲染，可以依赖更小的层：

```powershell
dotnet add package HelmSharp.Chart --version 1.2.0
dotnet add package HelmSharp.Engine --version 1.2.0
```

::: warning 版本可用性
1.2.0 是最新发布版本。本站记录的 M2 打包、仓库、拉取和依赖 API 已包含在 1.2.0 NuGet 包中。
:::

## 当前范围

当前 `master` 分支已覆盖 Chart 加载、values 合并、托管模板渲染、Chart 打包、仓库辅助方法、Kubernetes 提交/删除/等待辅助方法，以及基于 Kubernetes Secrets 的发布历史。尚未进入 NuGet 的能力请以上面的版本说明为准。

兼容性通过聚焦测试用 Chart 和选定公开 Chart 的基准输出测试持续验证，但 HelmSharp 仍然是边界清晰的 SDK。高级插件行为、完整来源校验、OCI 认证对齐和少见就绪场景仍在计划或推进中。如果你的 Chart 依赖某个 Helm 边缘行为，先看 [Helm 兼容性](helm-compatibility.md)。
