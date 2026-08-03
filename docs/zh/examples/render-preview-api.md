# 构建渲染预览接口

预览接口应当渲染 Chart 并返回清单，不应创建 release、访问集群，也不应接受调用方给出的任意本地文件路径。

安装 `HelmSharp.Chart` 与 `HelmSharp.Engine` 后，先通过应用自己维护的 catalog 解析请求的 Chart，再调用渲染器：

```csharp
app.MapPost("/preview", async (
    PreviewRequest request,
    ChartCatalog charts,
    ValuesCatalog valuesCatalog,
    CancellationToken cancellationToken) =>
{
    var chartPath = charts.GetPath(request.ChartId); // 执行你自己的允许列表校验。
    var valuesFilePaths = valuesCatalog.GetPaths(request.ValuesFileIds); // 解析 ID，绝不直接使用调用方给出的路径。
    var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);
    var values = await HelmValues.BuildAsync(
        chart,
        valuesFiles: valuesFilePaths,
        valuesContent: request.ValuesContent,
        setValues: request.SetValues,
        setFileValues: null,
        setStringValues: request.SetStringValues,
        setJsonValues: request.SetJsonValues,
        cancellationToken: cancellationToken);

    var renderer = new HelmTemplateRenderer(
        chart,
        request.ReleaseName,
        request.Namespace,
        values,
        kubeVersion: request.KubeVersion,
        apiVersions: request.ApiVersions,
        isUpgrade: false);

    return Results.Text(renderer.Render(), "text/yaml");
});
```

`ChartCatalog` 和 `ValuesCatalog` 都刻意留给应用实现：它们可以将 ID 映射到带版本的目录、已经解压的归档，或租户有权限使用的 catalog 项。将路径解析放在请求之外，可避免路径穿越和服务端非预期的文件读取，也能让每个预览都追溯到准确的输入。

生产接口还应：

- 接受 values 文件 ID 而非路径，限制上传/内联 values 的大小，并拒绝产品不支持的覆盖路径；
- 保存 Chart 版本、生效输入集、目标 capabilities 和渲染产物，供后续审批使用；
- 控制对清单和 values 的访问，因为二者都可能含有凭据；
- 只有响应中有单独的 notes 字段时，才调用 `RenderNotes()`。

[从评审到部署](dry-run-deployment.md)展示了如何把保存的预览变成集群变更操作，同时不破坏这里的只渲染边界。
