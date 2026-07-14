# HelmSharp.Release

## 包职责

`HelmSharp.Release` 使用 Kubernetes Secrets 保存 Helm 风格发布记录。

## 何时安装

大多数应用通过 `HelmSharp.Action` 间接使用。只有自定义 release storage 工作流才需要直接安装。

```powershell
dotnet add package HelmSharp.Release --version 1.1.1
```

## 依赖关系

该包依赖 Kubernetes .NET 客户端，并引用 Chart 和 Kube 辅助包。

## 主要类型

| 类型 | 用途 |
| --- | --- |
| `HelmReleaseStore` | 保存、列出、读取和更新发布记录。 |
| `HelmReleaseRecord` | 表示发布修订、清单、values 和状态。 |

## 常见组合

`HelmClient` 在安装、升级、卸载、状态、历史和 get 操作中使用 `HelmReleaseStore`。

## 当前边界

发布存储遵循 Helm 风格 Secret 记录，但不是通用审计数据库。
