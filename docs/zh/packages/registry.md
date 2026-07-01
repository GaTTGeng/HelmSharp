# HelmSharp.Registry

## 包职责

`HelmSharp.Registry` 包含 registry 相关扩展契约，用于未来 OCI 工作流。

## 何时安装

大多数应用不直接安装。它会被 `HelmSharp.Action` 和 `HelmSharp.Repo` 引用。

```powershell
dotnet add package HelmSharp.Registry --version 1.1.0
```

## 依赖关系

该包不依赖 Kubernetes。

## 主要类型

| 类型 | 用途 |
| --- | --- |
| `IOciRegistryClient` | OCI registry 集成扩展点。 |

## 常见组合

在围绕 HelmSharp repo 工作流实验自定义 registry client 时使用。

## 当前边界

完整 Helm OCI 认证对齐是计划工作，不是 1.1.0 保证。
