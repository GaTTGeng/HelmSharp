# HelmSharp.Storage

## 包职责

`HelmSharp.Storage` 包含 release storage 扩展契约。

## 何时安装

大多数应用使用 `HelmSharp.Action`。只有实现自定义 storage 集成时才直接安装。

```powershell
dotnet add package HelmSharp.Storage --version 1.1.0
```

## 依赖关系

该包引用 release 和 Kubernetes helper 包。

## 主要类型

| 类型 | 用途 |
| --- | --- |
| `IHelmReleaseStore` | Release record storage 扩展点。 |

## 常见组合

当产品需要把 release storage 抽象到自己的实现后面时使用。

## 当前边界

内置 release store 位于 `HelmSharp.Release`；该包主要提供 storage contract。
