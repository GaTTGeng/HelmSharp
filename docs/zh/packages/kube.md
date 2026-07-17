# HelmSharp.Kube

## 包职责

`HelmSharp.Kube` 对渲染后的 YAML 执行提交、删除、资源识别和等待。

## 何时安装

已有清单、只需要低层 Kubernetes 操作时安装：

```powershell
dotnet add package HelmSharp.Kube --version 1.2.0
```

## 依赖关系

该包依赖 Kubernetes .NET 客户端，并引用 `HelmSharp.Chart`。

## 主要类型

| 类型 | 用途 |
| --- | --- |
| `KubernetesManifestApplier` | 提交/删除多文档 YAML。 |
| `KubernetesResourceWaiter` | 等待常见资源就绪。 |
| `ManifestIdentity` | 从 YAML 解析 API version、kind、name、namespace。 |

## 常见组合

平台控制器可直接使用；发布工作流通常通过 `HelmClient.UpgradeInstallAsync` 间接使用。

## 当前边界

等待行为覆盖常见 Kubernetes 资源。特殊 CRD 就绪语义需要产品侧额外检查。
