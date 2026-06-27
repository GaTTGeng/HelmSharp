# API 选择

先选最小的层。HelmSharp 拆成多个包，是为了让只做渲染的工具不必引用 Kubernetes 发布工作流。

## 应该从哪个包开始？

| 你想做什么 | 建议起点 | 原因 |
| --- | --- | --- |
| 把 chart 渲染成 YAML | `HelmSharp.Chart` + `HelmSharp.Engine` | 最短路径：加载 chart、构建 values、渲染 manifests 和 NOTES。 |
| 在应用里提供 Helm 风格操作 | `HelmSharp.Action` | 一个 facade 覆盖 template、install、upgrade、rollback、uninstall、status、package、repo 等操作。 |
| 加载和检查 chart | `HelmSharp.Chart` | Chart model、归档加载、values 合并、依赖元数据和 YAML helper。 |
| 提交已渲染资源 | `HelmSharp.Kube` | 资源识别、创建/更新/删除和 wait 行为。 |
| 保存 release 历史 | `HelmSharp.Release` | 基于 Kubernetes Secrets 的 Helm 风格 release 记录。 |
| 处理 chart repository | `HelmSharp.Repo` | index、pull 和 repository helper。 |

## 只渲染

```csharp
using HelmSharp.Chart;
using HelmSharp.Engine;

var chart = await HelmChartLoader.LoadAsync("/charts/my-chart", ct);
var values = await HelmValues.BuildAsync(chart, null, null, null, null, null, null, ct);
var renderer = new HelmTemplateRenderer(chart, "demo", "default", values);

var manifests = renderer.Render();
var notes = renderer.RenderNotes();
```

适合预览 API、校验系统、GitOps 生成器，以及永远不直接修改集群的工具。

## 类命令客户端

`HelmClient` 的多数操作返回 `CommandResult`。如果你的产品已经围绕命令、stdout/stderr、exit code 或 dry-run 输出组织流程，这个模型更顺手。

```csharp
using HelmSharp.Action;

var client = new HelmClient(optionsProvider);

var template = await client.TemplateAsync(new HelmTemplateRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = "/charts/my-chart"
});

var dryRun = await client.UpgradeInstallAsync(new HelmUpgradeInstallRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = "/charts/my-chart",
    DryRun = true
});
```

## 常见 request object

| Request | 用途 |
| --- | --- |
| `HelmTemplateRequest` | 只渲染 manifests，不提交资源。 |
| `HelmUpgradeInstallRequest` | install 或 upgrade release，包括 dry-run。 |
| `HelmUninstallRequest` | 删除 release 资源并更新 history。 |
| `HelmRollbackRequest` | 回到旧 revision。 |
| `HelmPackageRequest` | 创建 chart archive。 |
| `HelmPullRequest` | 从 repository 相关来源拉取 chart。 |

常用渲染字段包括 `ReleaseName`、`Namespace`、`Chart`、`ValuesFile`、`ValuesFiles`、`ValuesContent`、`SetValues`、`SetStringValues`、`SetJsonValues`、`SetFileValues`、`IncludeCRDs`、`ShowNotes`、`KubeVersion` 和 `ApiVersions`。

## Options provider

`IHelmOptionsProvider` 放在 request 之外，是为了让应用集中管理默认 namespace、field manager、kubeconfig 选择和租户策略。

生产服务通常从配置或请求上下文实现它。小工具可以用静态实现，并把它藏在程序启动附近。

## 错误处理

高层操作通过 `CommandResult.ExitCode` 和 `CommandResult.StandardError` 报告失败。低层渲染 API 会用普通 .NET exception 暴露解析或兼容性问题。

排查 chart 差异时，请保留 chart 路径、values 输入、HelmSharp 输出、Helm CLI 输出、HelmSharp 版本和 Helm CLI 版本。这样兼容性修复才有可复现目标。
