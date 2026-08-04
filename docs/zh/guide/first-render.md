# 渲染 Chart

预览接口、策略检查、CI 产物和 GitOps 生成器都应走这条路径。它只读取 Chart 输入并返回文本，不会访问 Kubernetes 集群，也不会创建 release 历史。

## 1. 安装渲染器

```powershell
dotnet add package HelmSharp.Chart --version 1.3.1
dotnet add package HelmSharp.Engine --version 1.3.1
```

传给加载器的路径必须是包含 `Chart.yaml` 的 Chart 目录，或一个已打包的 Chart 归档。

## 2. 加载 values 并渲染

将以下代码放进控制台应用的 `Program.cs`，并将 Chart 路径作为第一个参数传入。

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

var renderer = new HelmTemplateRenderer(
    chart,
    releaseName: "demo",
    releaseNamespace: "default",
    values: values);

Console.WriteLine(renderer.Render());
```

代码刻意分成三个阶段：

1. `HelmChartLoader` 读取 `Chart.yaml`、模板、默认 values、Chart 文件、CRD 和已打包的依赖。
2. `HelmValues.BuildAsync` 产出模板中 `.Values` 对应的字典。
3. `HelmTemplateRenderer` 注入 release 上下文并执行模板。`Render()` 返回清单；需要展示 `NOTES.txt` 时，再调用 `RenderNotes()`。

## 3. 加上 values 文件或覆盖项

按顺序传入一个或多个 values 文件；同一键在后面的文件中出现时，后者生效。

```csharp
var values = await HelmValues.BuildAsync(
    chart,
    valuesFiles: ["values.base.yaml", "values.production.yaml"],
    valuesContent: null,
    setValues: new Dictionary<string, string> { ["image.tag"] = "1.25.3" },
    setFileValues: null,
    setStringValues: null,
    setJsonValues: null,
    cancellationToken: cancellationToken);
```

在让用户提供这些输入前，先读[Values 与覆盖项](values.md)。其中解释了优先级、保留字符串、JSON 值，以及文件路径与 `--set-file` 内容的区别。

## 4. 判断是否该使用高层 API

如果输出归你的应用使用，就保持在这层 API。若你的接口更适合返回命令式的 `CommandResult`，可使用 `HelmClient.TemplateAsync`；只有应用也要负责提交资源时，才使用 `UpgradeInstallAsync`。

若现有 Chart 与 Helm 的输出不同，请查看[兼容性约定](../helm-compatibility.md)和[模板函数矩阵](../template-function-compatibility.md)。可通过 [HelmCompare](../compare.md) 检查具体差异。
