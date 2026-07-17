# 安装

## 你在解决什么问题

这页帮助你选择最小的 HelmSharp 包组合。先从你的工作流出发：只做渲染预览、使用类似 Helm 命令的高层操作、处理仓库，还是执行 Kubernetes 发布操作。

HelmSharp 运行时不需要 `helm` 可执行文件。只有发布、提交、删除、等待等 Kubernetes 操作需要可访问的集群和 kubeconfig。

## 安装哪些包

大多数应用从高层客户端开始：

```powershell
dotnet add package HelmSharp.Action --version 1.2.0
```

只做渲染时安装低层包：

```powershell
dotnet add package HelmSharp.Chart --version 1.2.0
dotnet add package HelmSharp.Engine --version 1.2.0
```

## 完整最小代码

```csharp
using HelmSharp.Action;

var client = new HelmClient(new StaticHelmOptionsProvider());

var result = await client.TemplateAsync(new HelmTemplateRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = "/charts/my-chart",
    SetValues = new Dictionary<string, string>
    {
        ["image.tag"] = "1.1.0",
        ["replicaCount"] = "2"
    }
});

Console.WriteLine(result.StandardOutput);
```

## 关键 API 为什么这样用

`HelmSharp.Action` 适合类 Helm 命令工作流。`HelmSharp.Chart` 和 `HelmSharp.Engine` 更适合只渲染、不接触集群的预览工具。

## 生产环境注意事项

- 本地构建全部目标框架需要 .NET 8、.NET 9 和 .NET 10 SDK。
- 只有需要发布状态、Kubernetes 提交/删除/等待、仓库操作或命令式结果时才用 `HelmSharp.Action`。
- 会修改集群的示例在审批前保持 `DryRun = true`。

## 下一步

继续阅读 [第一次渲染](first-render.md)，再查看 [包职责](../packages/action.md)。
