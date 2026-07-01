# 渲染预览 API

## 你在解决什么问题

很多平台需要一个预览接口：用户选择 Chart 和 values，产品先展示 manifest，不修改集群。

## 安装哪些包

```powershell
dotnet add package HelmSharp.Chart --version 1.1.0
dotnet add package HelmSharp.Engine --version 1.1.0
```

## 完整最小代码

```csharp
app.MapPost("/preview", async (
    PreviewRequest request,
    CancellationToken cancellationToken) =>
{
    var chart = await HelmChartLoader.LoadAsync(request.ChartPath, cancellationToken);
    var values = await HelmValues.BuildAsync(
        chart,
        request.ValuesFiles,
        request.ValuesContent,
        request.SetValues,
        setFileValues: null,
        request.SetStringValues,
        request.SetJsonValues,
        cancellationToken);

    var renderer = new HelmTemplateRenderer(
        chart,
        request.ReleaseName,
        request.Namespace,
        values,
        kubeVersion: request.KubeVersion,
        apiVersions: request.ApiVersions);

    return Results.Text(renderer.Render(), "text/yaml");
});
```

## 关键 API 为什么这样用

这个示例不使用 `HelmClient`，因为 preview API 通常不应该拥有 release state。低层路径能清楚表达：加载、合并、渲染，仅此而已。

## 生产环境注意事项

- Chart 路径应来自白名单或内部 Chart registry。
- 保存每次预览使用的 values 输入。
- 限制上传 values 内容的大小。

## 下一步

如果预览后可以发布，继续看 [Dry-run 部署](dry-run-deployment.md)。
