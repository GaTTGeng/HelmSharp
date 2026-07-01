# 错误处理

## 你在解决什么问题

HelmSharp 有两类失败形态：高层类命令 API 的 `CommandResult`，以及低层 API 抛出的 .NET 异常。两者都要处理，Chart 作者才能得到有用诊断。

## 安装哪些包

大多数应用层错误处理从这里开始：

```powershell
dotnet add package HelmSharp.Action --version 1.1.0
```

## 完整最小代码

```csharp
var result = await client.TemplateAsync(request, cancellationToken);

if (result.ExitCode != 0)
{
    logger.LogWarning(
        "HelmSharp template failed for {Chart}: {Error}",
        request.Chart,
        result.StandardError);
    return Results.BadRequest(result.StandardError);
}

return Results.Text(result.StandardOutput, "text/yaml");
```

## 关键 API 为什么这样用

`HelmClient` 多数方法返回 `CommandResult`，方便应用建模标准输出、标准错误和退出码。`HelmChartLoader`、`HelmValues`、`HelmTemplateRenderer` 会在加载、values 解析或模板求值失败时抛出普通异常。

## 生产环境注意事项

- 日志至少保留 chart 路径、release name、namespace、values 来源、HelmSharp 版本和目标 kube version。
- 兼容性报告中同时保留 HelmSharp 输出和 `helm template` 输出，只规范化换行。
- 不要隐藏模板名和失败表达式，这通常是定位问题最快的信息。

## 下一步

需要成员级信息时查看 [API 参考](../api/index.md)。
