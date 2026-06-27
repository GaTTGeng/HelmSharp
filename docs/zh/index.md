---
layout: home

hero:
  name: HelmSharp
  text: 在 .NET 进程内渲染 Helm Chart
  tagline: 用托管代码加载 chart、合并 values、生成 Kubernetes manifests。运行时不需要 helm 二进制，也不需要 Process.Start。
  actions:
    - theme: brand
      text: 开始渲染
      link: /zh/getting-started
    - theme: alt
      text: 查看兼容性
      link: /zh/helm-compatibility

features:
  - title: 进程内渲染
    details: Web 服务、控制器、桌面工具、CI 扩展或内部平台都可以直接从 .NET 代码得到 Helm 风格输出。
  - title: 熟悉的 values 输入
    details: 支持 values 文件、内联 YAML 和 set 风格覆盖，让应用里的环境参数仍然按 Helm 使用者熟悉的方式表达。
  - title: 需要时再进入发布流程
    details: 先只渲染；当产品需要时，再接入 install、upgrade、rollback、uninstall、apply、wait 和 release history API。
  - title: 用 Helm 输出校验兼容性
    details: Helm CLI 只作为测试基准使用。当前真实 chart 测试集已覆盖 5 个公共 chart 的 129/129 个模板。
---

## HelmSharp 解决什么问题

你的 .NET 应用需要 Helm chart 的输出，但不希望在运行时依赖 `helm` 命令。常见场景包括部署预览、策略检查、GitOps 生成、内部平台发布、控制器渲染，或者任何需要先看清 YAML 再决定是否提交到集群的流程。

HelmSharp 提供的是托管 SDK 路径：加载 chart，构建 values，渲染 manifests；需要时再继续做 Kubernetes 发布操作。

## Quick Example

```csharp
var chart = await HelmChartLoader.LoadAsync("/charts/my-chart", ct);
var renderer = new HelmTemplateRenderer(chart, "demo", "default", values);
var manifests = renderer.Render();
```

核心感觉就是这样：chart 进来，manifests 出去，全程在你的进程里。

::: details 更喜欢类似 Helm 命令的高层客户端？

需要 template、install、upgrade、uninstall、rollback、status、package、repo 等一组操作时，用 `HelmSharp.Action`。

```csharp
using HelmSharp.Action;

var client = new HelmClient(optionsProvider);

var result = await client.TemplateAsync(new HelmTemplateRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = "/charts/my-chart",
    SetValues = new Dictionary<string, string>
    {
        ["image.tag"] = "1.2.3",
        ["replicaCount"] = "2"
    }
});

Console.WriteLine(result.StandardOutput);
```

`optionsProvider` 用来集中管理 namespace、field manager、kubeconfig 和环境策略，不需要在第一次阅读示例时展开。

:::

## 安装

大多数应用从高层包开始：

```powershell
dotnet add package HelmSharp.Action
```

如果只需要渲染，可以依赖更小的层：

```powershell
dotnet add package HelmSharp.Chart
dotnet add package HelmSharp.Engine
```

## 当前范围

当前 golden suite 已经在 `podinfo`、`metrics-server`、`external-dns`、`ingress-nginx` 和 `cert-manager` 这 5 个公共 chart 上渲染 **129/129 个模板**，没有 parser exception。这是 HelmSharp 扩展兼容性的基线，不只是手写小样例。

目前已覆盖 chart 加载、values 合并、托管模板渲染、chart 打包、repository helper、Kubernetes apply/delete/wait helper，以及基于 Kubernetes Secrets 的 release history。

HelmSharp 仍然是边界清晰的 SDK。部分高级 Sprig 行为、精确输出格式、OCI 认证、provenance 校验、少见资源 readiness、插件执行等仍在计划或推进中。如果你的 chart 依赖某个 Helm 边缘行为，先看兼容性页面。
