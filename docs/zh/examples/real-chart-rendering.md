# 渲染公开 Chart

公开 Chart 是必须固定版本、检查和验证的输入。某个 Chart 能在一套 Helm 环境中运行，并不代表它必然与任意托管渲染器兼容。

先使用 `HelmSharp.Repo` 或制品流水线将指定版本的 Chart 拉到受控目录，再像处理内部 Chart 一样渲染解压后的目录：

```csharp
var chart = await HelmChartLoader.LoadAsync(extractedChartPath, cancellationToken);
var values = await HelmValues.BuildAsync(
    chart,
    valuesFiles: ["values.organization.yaml"],
    valuesContent: null,
    setValues: null,
    setFileValues: null,
    setStringValues: null,
    setJsonValues: null,
    cancellationToken: cancellationToken);

var renderer = new HelmTemplateRenderer(
    chart,
    releaseName: "external-dns",
    releaseNamespace: "platform",
    values: values,
    kubeVersion: "1.30.0",
    apiVersions: ["externaldns.k8s.io/v1alpha1"]);

var manifest = renderer.Render();
```

在将它引入产品前，固定归档摘要，并用准确的 Chart 版本、values 和 capabilities 测试。输出与 Helm 不同时，先把差异缩减到一个小 Chart/模板，再比较生效 values、目标 capabilities 和[函数矩阵](../template-function-compatibility.md)。[HelmCompare](../compare.md)适合并排检查。

仓库中的公开 Chart golden 测试是回归证据，并不是对所有公开 Chart、所有版本的认证。
