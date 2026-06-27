# 快速开始

先回答一个集成问题：你只是想拿到渲染后的 Kubernetes YAML，还是想在应用里做 Helm 风格的发布流程？

如果只是渲染，先用低层 API。它的依赖更少，行为更直接，也最适合验证你的 chart 是否适配 HelmSharp。

## 安装

```powershell
dotnet add package HelmSharp.Chart
dotnet add package HelmSharp.Engine
```

需要发布、回滚、状态查询等工作流时，再加 `HelmSharp.Action`。

## 不通过 Helm CLI 渲染 chart

```csharp
using HelmSharp.Chart;
using HelmSharp.Engine;

var chart = await HelmChartLoader.LoadAsync("/charts/my-chart", ct);

var values = await HelmValues.BuildAsync(
    chart: chart,
    valuesFiles: ["values.production.yaml"],
    valuesContent: null,
    setValues: new Dictionary<string, string>
    {
        ["image.tag"] = "1.25",
        ["replicaCount"] = "2"
    },
    setFileValues: null,
    setStringValues: null,
    setJsonValues: null,
    cancellationToken: ct);

var renderer = new HelmTemplateRenderer(chart, "demo", "default", values);
var manifests = renderer.Render();
var notes = renderer.RenderNotes();
```

这个路径适合部署预览、策略检查、漂移报告、GitOps 生成，或者任何需要在 apply 之前先分析 manifests 的系统。

## values 应该贴近你的产品模型

HelmSharp 支持 Helm 使用者熟悉的覆盖方式：

| 输入 | 适合场景 |
| --- | --- |
| `valuesFiles` | 运维或平台团队已经维护环境 values 文件。 |
| `valuesContent` | values 来自数据库、配置中心或应用生成内容。 |
| `setValues` | 普通 `--set` 风格覆盖。 |
| `setStringValues` | 看起来像数字或布尔值的内容也必须保持字符串。 |
| `setJsonValues` | 产品配置天然是 JSON 对象或数组。 |
| `setFileValues` | 某个 value 来自文件内容。 |

后面的输入覆盖前面的输入。把这个顺序留在代码里，会让预览和排障更容易解释。

## 进入发布工作流

当你希望一个客户端覆盖 template、dry-run、install、upgrade、rollback、uninstall、status、history、package、repo 等操作时，安装 `HelmSharp.Action`。

```powershell
dotnet add package HelmSharp.Action
```

```csharp
using HelmSharp.Action;

var client = new HelmClient(optionsProvider);

var result = await client.UpgradeInstallAsync(new HelmUpgradeInstallRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = "/charts/my-chart",
    ValuesFiles = ["values.production.yaml"],
    CreateNamespace = true,
    Wait = true,
    TimeoutSeconds = 300,
    DryRun = true
});

Console.WriteLine(result.StandardOutput);
```

在产品流程明确允许修改集群之前，保持 `DryRun = true`。

::: details 最小静态 options provider

生产应用通常从配置、DI 或租户上下文实现 `IHelmOptionsProvider`。小型控制台程序可以先这样写：

```csharp
sealed class StaticHelmOptionsProvider : IHelmOptionsProvider
{
    public ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new HelmExecutionOptions
        {
            DefaultNamespace = "default",
            FieldManager = "helmsharp"
        });
}
```

:::

## 运行仓库示例

```powershell
dotnet run --project examples/RenderChart -- examples/sample-chart
dotnet run --project examples/InstallRelease -- examples/sample-chart demo
dotnet run --project examples/InstallRelease -- examples/sample-chart demo --apply
```

安装示例默认 dry-run。传入 `--apply` 才会提交到当前 Kubernetes context。

## 下一步

阅读 [API 选择](api-overview.md) 来确定最小依赖层，再查看 [Helm 兼容性](helm-compatibility.md) 确认你的 chart 依赖的行为是否已覆盖。
