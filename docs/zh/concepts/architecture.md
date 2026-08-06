# 架构

HelmSharp 是对部分 Helm 行为的托管 .NET 实现。它加载 Chart 输入、渲染模板，并且无需启动 `helm` 可执行文件即可驱动发布操作。

## 按操作责任选择层级

| 层级 | 职责 | 适用场景 |
| --- | --- | --- |
| `HelmSharp.Chart` | 加载 Chart 并合并 values。 | 需要将 Chart 输入作为 .NET 对象处理。 |
| `HelmSharp.Engine` | 将模板渲染为清单文本。 | 应用需要预览、校验或提交 YAML。 |
| `HelmSharp.Action` | 协调渲染、hook、Kubernetes 变更和 release 历史。 | 服务负责安装、升级、回滚或卸载。 |
| `HelmSharp.Kube` | 提交、删除和等待已有清单文本。 | 已经拥有 YAML，不需要 Helm 生命周期行为。 |
| `HelmSharp.Release` | 建模 release revision，并使用内置的 Secret 存储实现。 | 需要 release 记录或默认 Kubernetes 持久化。 |
| `HelmSharp.Storage` | 定义 release 存储扩展契约。 | 要为自定义持久化实现 `IHelmReleaseStore` 或 `IHelmReleasePurgeStore`。 |

## 数据流

仅渲染的应用使用 `Chart` 和 `Engine`：加载 Chart、构造生效 values、配置 release 和 capabilities 输入，然后返回 YAML。不需要 Kubernetes 客户端或 release 记录。

生命周期应用从 `HelmSharp.Action` 进入。`HelmClient` 使用相同的 Chart 和渲染层，然后通过 `Kube` 执行 hook 和资源提交，并通过 `Release` 存储记录结果。这是部署服务应选择的层级。

## 为什么 HelmSharp 不启动 Helm CLI

进程内运行让应用获得强类型请求对象、直接错误处理、可控的存储和 HTTP 行为，也不依赖本地可执行文件。这不意味着 HelmSharp 是命令行模拟器。依赖 Helm 边缘行为前，请查看[兼容性约定](../helm-compatibility.md)。

## 后续阅读

- 按任务选择[包和 API](../api-overview.md)。
- 构建仅渲染的[ASP.NET Core 预览接口](../scenarios/aspnet-core-preview.md)。
- 应用负责集群变更时，构建[部署服务](../scenarios/deployment-service.md)。
