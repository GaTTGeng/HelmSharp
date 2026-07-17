# Kubernetes 操作

## 你在解决什么问题

当你已经有渲染后的 YAML，并希望直接执行提交、删除、资源识别或等待时，使用 `HelmSharp.Kube`。

## 安装哪些包

```powershell
dotnet add package HelmSharp.Kube --version 1.2.0
```

## 完整最小代码

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#manifest-applier{csharp}

## 关键 API 为什么这样用

`KubernetesManifestApplier` 会切分多文档 YAML，根据 API version、kind、name、namespace 识别资源，并通过 Kubernetes .NET 客户端提交或删除。`KubernetesResourceWaiter` 用于等待常见工作负载就绪。

## 生产环境注意事项

- Kubernetes 客户端应来自产品统一的 kubeconfig 策略。
- 使用稳定的 `fieldManager`，例如 `helmsharp` 或产品名。
- 删除与提交一样属于集群变更，应走同样审批路径。

## 下一步

在向用户开放发布动作前阅读 [错误处理](error-handling.md)。
