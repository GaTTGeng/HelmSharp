# HelmSharp.Registry

## 包职责

`HelmSharp.Registry` 包含注册表相关扩展契约，用于未来 OCI 工作流。

## 何时安装

大多数应用不直接安装。它会被 `HelmSharp.Action` 和 `HelmSharp.Repo` 引用。

```powershell
dotnet add package HelmSharp.Registry --version 1.1.1
```

## 依赖关系

该包不依赖 Kubernetes。

## 主要类型

| 类型 | 用途 |
| --- | --- |
| `IOciRegistryClient` | OCI 注册表集成扩展点。 |

## 常见组合

在围绕 HelmSharp 仓库工作流实验自定义注册表客户端时使用。

## 当前边界

完整 Helm OCI 认证对齐是计划工作，不是 1.1.1 保证。
