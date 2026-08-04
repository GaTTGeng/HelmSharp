---
layout: home

hero:
  name: HelmSharp
  text: 在 .NET 中使用 Helm Chart
  tagline: 在托管代码里渲染、检查和发布 Helm Chart；运行时不需要启动 Helm CLI。
  image:
    src: /logo.svg
    alt: HelmSharp logo
  actions:
    - theme: brand
      text: 运行快速开始
      link: /zh/getting-started
    - theme: alt
      text: 查看示例
      link: /zh/examples/render-preview-api
    - theme: alt
      text: 对比输出
      link: /zh/compare

features:
  - title: 进程内渲染
    details: 加载 Chart、合并 values 并生成清单；部署环境不需要额外携带 Helm 可执行文件。
  - title: 有边界地部署
    details: 先预览，再由真正拥有部署职责的服务执行安装、升级、回滚和发布记录管理。
  - title: 明确兼容性范围
    details: 兼容性页面区分已验证能力、部分支持能力，以及需要按 Chart 自行验证的边界。
---

## 按你要完成的事开始

<div class="docs-paths">
  <a class="docs-path" href="./guide/first-render"><strong>渲染 Chart</strong><span>为预览、策略检查或 GitOps 提交生成清单，不需要访问 Kubernetes 集群。</span></a>
  <a class="docs-path" href="./guide/release-workflows"><strong>执行发布工作流</strong><span>由拥有部署职责的服务完成试运行、提交、等待、检查 revision、回滚和卸载。</span></a>
  <a class="docs-path" href="./guide/chart-distribution"><strong>管理 Chart 交付</strong><span>在 .NET 中打包 Chart、生成索引、从 HTTP 仓库拉取 Chart，并处理依赖。</span></a>
</div>

## 最小的有效渲染

先安装 Chart 与渲染器包：

```powershell
dotnet add package HelmSharp.Chart --version 1.3.1
dotnet add package HelmSharp.Engine --version 1.3.1
```

然后加载 Chart 目录、构造 values 并渲染。可直接复制运行的程序和这三个对象的职责见[快速开始](getting-started.md)。

```csharp
var chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);
var values = await HelmValues.BuildAsync(chart, valuesFiles: null, null, null, null, null, null, CancellationToken.None);
var renderer = new HelmTemplateRenderer(chart, "demo", "default", values);

var manifest = renderer.Render();
```

<div class="key-point"><strong>预览请使用低层路径。</strong>它只读取 Chart 输入并返回字符串。只有明确需要发布记录或 Kubernetes 变更时，才使用 <code>HelmSharp.Action</code>。</div>

## 选择入口

| 你在构建… | 从这里开始 | 接着阅读 |
| --- | --- | --- |
| 预览、校验或 GitOps 生成器 | `HelmSharp.Chart` + `HelmSharp.Engine` | [渲染 Chart](guide/first-render.md) |
| 部署服务或 operator | `HelmSharp.Action` | [安装和升级 Release](guide/release-workflows.md) |
| 已经持有 YAML 的 Kubernetes 控制器 | `HelmSharp.Kube` | [直接提交清单](guide/kubernetes-operations.md) |
| Chart 仓库或打包流水线 | `HelmSharp.Action` + `HelmSharp.Repo` | [Chart 交付](guide/chart-distribution.md) |

[包和 API 选择](api-overview.md)会进一步解释这些边界，并链接到每个包的生成 API 参考。

## 接入现有 Chart 前

HelmSharp 在已经实现并测试的范围内遵循 Helm 行为；它不保证任意 Chart 或插件都可原样运行。把某个 Helm 边缘行为作为生产依赖前，请先查看[兼容性约定](helm-compatibility.md)和[模板函数矩阵](template-function-compatibility.md)。

[HelmCompare](compare.md)也可用于并排检查输出。
