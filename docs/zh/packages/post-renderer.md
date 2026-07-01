# HelmSharp.PostRenderer

## 包职责

`HelmSharp.PostRenderer` 定义后处理器扩展契约，用于在模板渲染后、提交前转换清单。

## 何时安装

构建自定义后处理集成时直接安装：

```powershell
dotnet add package HelmSharp.PostRenderer --version 1.1.0
```

## 依赖关系

该包很小，不依赖 Kubernetes。

## 主要类型

| 类型 | 用途 |
| --- | --- |
| `IPostRenderer` | 在下一步工作流之前转换渲染后的 YAML。 |

## 常见组合

用于策略注入、标签、注解或清单规范化。

## 当前边界

后处理器是扩展点。转换逻辑应保持确定性，并用具代表性的 Chart 输出测试。
