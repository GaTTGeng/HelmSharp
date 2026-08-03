# 排查失败

HelmSharp 有两种刻意区分的失败模型。根据入口 API 处理相应模型，不要把所有错误都变成没有信息量的“渲染失败”。

| API 层 | 失败形态 | 常见调用方 |
| --- | --- | --- |
| `HelmSharp.Action` 的 `HelmClient` | 含退出码、标准输出和标准错误的 `CommandResult` | HTTP 接口、CLI 包装层、部署服务。 |
| `HelmChartLoader`、`HelmValues`、`HelmTemplateRenderer`、仓库辅助方法 | .NET 异常 | 需要正常异常组合的库代码。 |

## 处理高层操作

```csharp
var result = await client.TemplateAsync(request, cancellationToken);

if (!result.Succeeded)
{
    logger.LogWarning(
        "Chart render failed for {Chart}, release {Release}: {Error}",
        request.Chart,
        request.ReleaseName,
        result.StandardError);

    return Results.BadRequest(new
    {
        error = "Chart 无法渲染。",
        exitCode = result.ExitCode
    });
}

return Results.Text(result.StandardOutput, "text/yaml");
```

命令失败时 `StandardOutput` 仍可能有价值；在访问控制允许的情况下，将它与操作记录一起保存。不要以输出非空来判断成功，应使用 `Succeeded` 或 `ExitCode`。

## 在低层渲染外补充上下文

让加载、YAML 解析和模板异常保留原始消息与堆栈。应在拥有 Chart 路径和 values 的服务边界补充请求上下文，而不是在 `catch` 中抹掉异常类型。

建议记录 Chart 标识或路径、release 名称、命名空间、选定的 Chart 版本、values 文件名、显式 set 键（不要记录机密值）、目标 Kubernetes 版本和 API 版本。要做 parity 报告时，保留准确的 values 输入，比较输出前只规范化换行符。

## 常见现象

| 现象 | 先检查什么 |
| --- | --- |
| 模板函数不支持 | [模板函数矩阵](../template-function-compatibility.md)，然后看渲染器诊断中的模板路径。 |
| Chart 输出与 Helm 不同 | 目标 capabilities、生效 values 和[兼容性约定](../helm-compatibility.md)。也可使用 [HelmCompare](../compare.md)。 |
| Release 命令失败 | `StandardError`、Kubernetes RBAC、目标命名空间、hook 状态和就绪超时。 |
| Release 存在但输出异常 | 用 `StatusAsync`、`HistoryAsync`、`GetManifestAsync` 或 `GetValuesAsync` 查看存储 revision；不要假定它由当前 Chart 产生。 |
| 直接提交 CRD 失败 | API 发现、资源标识和目标集群中安装的 CRD 版本。 |

渲染后的清单和 values 都可能含有凭据。向不受信任的调用方返回经脱敏的诊断，将敏感产物放入受限的操作记录，而不是普通应用日志。
