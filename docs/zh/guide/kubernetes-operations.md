# 直接提交清单

当应用已经有多文档 Kubernetes YAML，只需要更低层的提交、删除、资源标识或就绪等待能力时，使用 `HelmSharp.Kube`。它不会创建 Helm release 历史；这属于高层发布工作流的职责。

```powershell
dotnet add package HelmSharp.Kube --version 1.3.2
```

## 提交已经渲染的 YAML

```csharp
using HelmSharp.Kube;
using k8s;

var config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
using var kubernetes = new Kubernetes(config);

var applier = new KubernetesManifestApplier(
    kubernetes,
    fieldManager: "my-deployment-service");

await foreach (var resource in applier.ApplyAsync(
    manifest,
    defaultNamespace: "platform",
    cancellationToken))
{
    Console.WriteLine($"Applied {resource}");
}
```

提交器会拆分 YAML 文档、推导每个资源的标识，并通过 Kubernetes .NET 客户端提交。field manager 当前只会传递给通过动态发现路径处理的资源，例如自定义资源；为这些路径使用稳定的名称，可让服务端字段归属和故障排查更清晰。Deployment、Service 等常见的强类型资源当前不会收到该 field manager 值。

## 命名空间参数的含义

命名空间参数只为没有声明 `metadata.namespace` 的命名空间级文档提供默认值。清单中明确的命名空间优先；集群级资源不会被赋予命名空间。

客户端会直接处理常见资源类型，其他 API 资源则通过目标集群发现。提交要求清单声明的 API version 可用；删除及删除等待遇到已退役的自定义资源版本时，会在同一 API group 中查找仍提供相同 kind 的版本并改用该 endpoint。若没有任何版本提供该 kind，直接删除会在诊断中包含清单资源标识；删除等待则把已从整个 group 移除的 kind 视为不存在。

## 确定性地删除已渲染 YAML

```csharp
await foreach (var resource in applier.DeleteAsync(
    manifest,
    defaultNamespace: "platform",
    propagationPolicy: "Foreground",
    cancellationToken))
{
    Console.WriteLine($"Deleted {resource}");
}
```

删除会按清单文档的逆序执行。不传 propagation 的重载使用 `Background`；可显式选择 `Background`、`Foreground` 或 `Orphan`，该值会传给强类型和动态发现的删除 endpoint。Kubernetes 对象返回 `404` 时视为幂等成功；动态 API version endpoint 已移除时，会先尝试同 group 的其他 served version。鉴权失败、传输失败、不支持的 core 类型、直接删除穷尽发现仍无对应 kind 及其他非 `404` API 失败会停止操作，并在异常中给出受影响资源的 API version、kind、namespace 和 name；取消会阻止下一个请求发出。

直接 applier 会删除传入的每个有效资源文档，不解释 Helm 生命周期注解。`HelmClient` 会在卸载和 rollback 清理前过滤带有 `helm.sh/resource-policy: keep` 的资源；这会阻止直接删除请求，但无法阻止 Kubernetes 因命名空间或 owner 被删除而执行级联删除。

## 只等待你真正需要的就绪状态

`KubernetesResourceWaiter` 会观察常见工作负载：Deployment、StatefulSet、DaemonSet、ReplicaSet、Job、Pod、PVC、Endpoints 和 v2 HPA。仅在调用方明确请求时才等待 Job。这个集合之外的对象只表示已被接受，不能证明 operator 管理的资源已经就绪。

如需完整的 Helm 风格生命周期，包括 hook 和持久化 revision，请使用[安装和升级 Release](release-workflows.md)。直接删除也应放在和提交相同的授权、审批路径之后。
