# 为 GitOps 生成清单

在 GitOps 中，HelmSharp 的职责止于确定性的 YAML。仓库工作流负责创建提交或 PR；HelmSharp 不应再将同一份清单直接提交给集群。

```csharp
var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);
var values = await HelmValues.BuildAsync(
    chart,
    valuesFiles: [environmentValuesPath],
    valuesContent: null,
    setValues: new Dictionary<string, string>
    {
        ["image.tag"] = buildVersion
    },
    setFileValues: null,
    setStringValues: null,
    setJsonValues: null,
    cancellationToken: cancellationToken);

var renderer = new HelmTemplateRenderer(
    chart,
    releaseName: applicationName,
    releaseNamespace: targetNamespace,
    values: values,
    kubeVersion: targetKubeVersion,
    apiVersions: targetApiVersions);

var manifest = renderer.Render();
await File.WriteAllTextAsync(outputPath, manifest, cancellationToken);
```

将 Chart 版本、values 文件 revision、镜像标签、Kubernetes 版本和自定义 API 版本随生成文件一起提交。这些输入比渲染后的 YAML 更能解释一次 diff。

如果仓库要求每个资源一个文件或要求不同顺序，请把它做成明确的后处理步骤，并独立测试。不要依赖部署控制器去猜测这个文件由哪个模板引擎生成。

只有同一个服务也拥有直接变更集群的职责时，才使用 release 工作流。纯 GitOps 架构中，[渲染](../guide/first-render.md)加仓库自动化是更安全的边界。
