# 直接提交清单

当应用已经有多文档 Kubernetes YAML，只需要更低层的提交、删除、资源标识或就绪等待能力时，使用 `HelmSharp.Kube`。它不会创建 Helm release 历史；这属于高层发布工作流的职责。

```powershell
dotnet add package HelmSharp.Kube --version 1.3.1
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
    @namespace: "platform",
    cancellationToken))
{
    Console.WriteLine($"Applied {resource}");
}
```

提交器会拆分 YAML 文档、推导每个资源的标识，并通过 Kubernetes .NET 客户端提交。为每个产品指定稳定的 field manager 名称，这会让服务端字段归属和故障排查更清晰。

## 命名空间参数的含义

命名空间参数只为没有声明 `metadata.namespace` 的命名空间级文档提供默认值。清单中明确的命名空间优先；集群级资源不会被赋予命名空间。

客户端会直接处理常见资源类型，其他 API 资源则通过目标集群发现后提交或删除。如果 API 版本已移除，或找不到自定义资源类型，诊断中会包含对应资源标识。

## 只等待你真正需要的就绪状态

`KubernetesResourceWaiter` 会观察常见工作负载：Deployment、StatefulSet、DaemonSet、ReplicaSet、Job、Pod、PVC、Endpoints 和 v2 HPA。仅在调用方明确请求时才等待 Job。这个集合之外的对象只表示已被接受，不能证明 operator 管理的资源已经就绪。

如需完整的 Helm 风格生命周期，包括 hook 和持久化 revision，请使用[安装和升级 Release](release-workflows.md)。直接删除也应放在和提交相同的授权、审批路径之后。
