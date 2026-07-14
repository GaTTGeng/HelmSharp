# GitOps PR 生成器

## 你在解决什么问题

GitOps 工作流可以在进程内渲染 Chart，把 YAML 写入仓库，再打开 PR 供审核。

## 安装哪些包

```powershell
dotnet add package HelmSharp.Chart --version 1.1.1
dotnet add package HelmSharp.Engine --version 1.1.1
```

## 完整最小代码

```csharp
var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);
var values = await HelmValues.BuildAsync(
    chart,
    valuesFiles: ["values.yaml", "values.production.yaml"],
    valuesContent: null,
    setValues: new Dictionary<string, string> { ["image.tag"] = imageTag },
    setFileValues: null,
    setStringValues: null,
    setJsonValues: null,
    cancellationToken);

var renderer = new HelmTemplateRenderer(chart, releaseName, "apps", values);
var manifest = renderer.Render();

var outputPath = Path.Combine(repoRoot, "apps", releaseName, "manifest.yaml");
await File.WriteAllTextAsync(outputPath, manifest, cancellationToken);
```

## 关键 API 为什么这样用

GitOps 系统需要确定性的文件和可评审的差异，而不是直接修改集群。`HelmTemplateRenderer` 负责产出应提交的清单。

## 生产环境注意事项

- 生成路径保持稳定。
- 如果评审者需要理解输出变化，请把 values 一并提交。
- 对关键内部 Chart 增加自己的基准输出测试。

## 下一步

阅读 [值配置（Values）](../guide/values.md)，明确环境覆盖策略。
