# HelmSharp.Kube

`HelmSharp.Kube` 是较低层的 Kubernetes 层。当 YAML 已经存在，代码只需要托管的提交、删除、资源标识或选择性的就绪等待时，安装它。

```powershell
dotnet add package HelmSharp.Kube --version 1.3.1
```

| 类型 | 用途 |
| --- | --- |
| `KubernetesManifestApplier` | 拆分多文档 YAML，并提交或删除每个资源。 |
| `KubernetesResourceWaiter` | 等待已支持工作负载变为就绪。 |
| `ManifestIdentity` | 从 YAML 文档解析 API 版本、类型、名称和命名空间。 |

本包使用 Kubernetes .NET 客户端，不负责合并 Chart values、执行模板 hook 或持久化 Helm revision。控制器或自定义部署工作流可以直接用它；这些生命周期职责需要统一管理时，使用 `HelmSharp.Action`。

等待器覆盖常见工作负载，不推导任意 CRD 的就绪状态。请看[直接提交清单](../guide/kubernetes-operations.md)和[生成的 Kube API](../api/generated/kube.md)。
