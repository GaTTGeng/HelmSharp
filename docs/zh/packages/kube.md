# HelmSharp.Kube

## 包职责

`HelmSharp.Kube` 对渲染后的 YAML 执行 apply、delete、资源识别和 wait。

## 何时安装

已有 manifests、只需要低层 Kubernetes 操作时安装：

```powershell
dotnet add package HelmSharp.Kube --version 1.1.0
```

## 依赖关系

该包依赖 Kubernetes .NET client，并引用 `HelmSharp.Chart`。

## 主要类型

| 类型 | 用途 |
| --- | --- |
| `KubernetesManifestApplier` | Apply/delete 多文档 YAML。 |
| `KubernetesResourceWaiter` | 等待常见资源 ready。 |
| `ManifestIdentity` | 从 YAML 解析 API version、kind、name、namespace。 |

## 常见组合

平台控制器可直接使用；发布工作流通常通过 `HelmClient.UpgradeInstallAsync` 间接使用。

## 当前边界

Wait 覆盖常见 Kubernetes 资源。特殊 CRD readiness 语义需要产品侧额外检查。
