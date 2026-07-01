# HelmSharp.Storage

## 包职责

`HelmSharp.Storage` 包含发布存储扩展契约。

## 何时安装

大多数应用使用 `HelmSharp.Action`。只有实现自定义 storage 集成时才直接安装。

```powershell
dotnet add package HelmSharp.Storage --version 1.1.0
```

## 依赖关系

该包引用 release 和 Kubernetes 辅助包。

## 主要类型

| 类型 | 用途 |
| --- | --- |
| `IHelmReleaseStore` | 发布记录存储扩展点。 |

## 常见组合

当产品需要把发布存储抽象到自己的实现后面时使用。

## 当前边界

内置发布存储位于 `HelmSharp.Release`；该包主要提供存储契约。
