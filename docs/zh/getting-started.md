# 快速开始：渲染 Chart

本快速开始会在 .NET 控制台应用中渲染本地 Helm Chart，不需要 Kubernetes 集群，也不需要 Helm CLI。

## 创建项目

```powershell
dotnet new console --name RenderChart
cd RenderChart
dotnet add package HelmSharp.Chart --version 1.3.1
dotnet add package HelmSharp.Engine --version 1.3.1
```

## 渲染 Chart 目录

用以下代码替换 `Program.cs`，运行时传入包含 `Chart.yaml` 的目录路径。

```csharp
using HelmSharp.Chart;
using HelmSharp.Engine;

var chartPath = args.Length == 1
    ? args[0]
    : throw new ArgumentException("Pass the path to a Helm chart.");

var chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);
var values = await HelmValues.BuildAsync(
    chart,
    valuesFiles: null,
    valuesContent: null,
    setValues: null,
    setFileValues: null,
    setStringValues: null,
    setJsonValues: null,
    cancellationToken: CancellationToken.None);

var renderer = new HelmTemplateRenderer(chart, "demo", "default", values);

Console.WriteLine(renderer.Render());
```

```powershell
dotnet run -- ../charts/my-chart
```

清单会输出到标准输出。你可以重定向到文件、从 HTTP 接口返回、交给策略引擎，或提交到 GitOps 仓库。如果 Chart 有 `NOTES.txt` 且应用需要展示它，再调用 `renderer.RenderNotes()`。

## 接下来做什么

| 你想… | 阅读 |
| --- | --- |
| 传入 `values.yaml`、`--set`、JSON 或保留字符串的覆盖项 | [Values 与覆盖项](guide/values.md) |
| 按指定 Kubernetes 版本渲染条件模板 | [按目标集群渲染](guide/template-rendering.md) |
| 构建真实的预览接口 | [渲染预览接口](examples/render-preview-api.md) |
| 提交 Chart 并保存 release 历史 | [安装和升级 Release](guide/release-workflows.md) |
| 选择其他包 | [选择包和 API](api-overview.md) |

将某项 Helm 行为作为生产依赖前，请查看[兼容性约定](helm-compatibility.md)和[模板函数矩阵](template-function-compatibility.md)，了解已支持的行为与已知限制。
