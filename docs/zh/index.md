---
layout: home

hero:
  name: HelmSharp
  text: 面向 .NET 的 Helm 兼容渲染
  tagline: HelmSharp 1.1.0 在 5 个真实公开 Chart 的 129/129 个模板上与 helm template 达到规范化后逐字节一致。运行时不需要 helm 可执行文件。
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
  - title: 真实 Chart 兼容性基线
    details: "1.1.0 golden suite 覆盖 podinfo、metrics-server、external-dns、ingress-nginx 和 cert-manager，129/129 个模板通过。"
  - title: 按工作流组织的文档
    details: 覆盖安装选择、只渲染预览、values 优先级、发布试运行、Kubernetes 提交/等待和错误处理。
  - title: 每个包的使用边界
    details: 每个 NuGet 包都有职责、安装建议、主要类型、常见组合和当前边界说明。
  - title: 生成式 API 参考
    details: 从源码索引公开类、接口、记录、枚举、方法和属性，便于后续版本维护。
---

## 眼见为实

上传 Helm Chart，让 HelmSharp 和真实 Helm CLI 并排渲染并对比输出。

<div class="compare-cta">
  <a class="compare-cta-btn" href="./compare">
    <span class="compare-cta-label">打开在线对比</span>
    <span class="compare-cta-arrow">→</span>
  </a>
</div>

## HelmSharp 1.1.0 适合什么

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
dotnet add package HelmSharp.Action --version 1.1.0
```

如果只需要渲染，可以依赖更小的层：

```powershell
dotnet add package HelmSharp.Chart --version 1.1.0
dotnet add package HelmSharp.Engine --version 1.1.0
```

## 当前范围

当前 golden suite 已经在 `podinfo`、`metrics-server`、`external-dns`、`ingress-nginx` 和 `cert-manager` 上渲染 **129/129 个模板**，全部取得 Pass 判定。这是 HelmSharp 面向真实 Chart 扩展兼容性的基线，不只是手写小样例。

目前已覆盖 Chart 加载、values 合并、托管模板渲染、Chart 打包、仓库 helper、Kubernetes 提交/删除/等待 helper，以及基于 Kubernetes Secrets 的发布历史。

HelmSharp 仍然是边界清晰的 SDK。高级插件行为、完整来源校验、OCI 认证对齐和少见就绪场景仍在计划或推进中。如果你的 Chart 依赖某个 Helm 边缘行为，先看 [Helm 兼容性](helm-compatibility.md)。
