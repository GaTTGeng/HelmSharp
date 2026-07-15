# HelmSharp.Chart

## 包职责

`HelmSharp.Chart` 负责加载 Chart 目录和 `.tgz` 归档，暴露 Chart 元数据，合并 values，并提供 YAML 辅助方法。

## 何时安装

任何只渲染集成都会用到：

```powershell
dotnet add package HelmSharp.Chart --version 1.1.1
```

## 依赖关系

该包依赖 `YamlDotNet`，不依赖 Kubernetes。

## 主要类型

| 类型 | 用途 |
| --- | --- |
| `HelmChartLoader` | 从目录或归档加载 Chart。 |
| `HelmChart` | 查看 Chart 元数据、模板、文件、CRDs 和子 Chart。 |
| `HelmValues` | 按 Helm 优先级构建合并后的 values。 |
| `HelmYaml` | 序列化和反序列化 YAML 对象。 |
| `HelmChartDependency` | 查看 `Chart.yaml` 依赖元数据。 |
| `HelmChartLockEntry` | 查看 `Chart.lock` 条目。 |

## 常见组合

用 `HelmChartLoader` 加载，用 `HelmValues` 合并，然后传给 `HelmSharp.Engine` 的 `HelmTemplateRenderer`。

`charts/` 下的已打包依赖会作为子 Chart 加载。`Chart.lock` 选定精确版本时，加载器使用该身份映射同一 Chart 的多个版本或别名。别名作用域的默认值和覆盖值使用别名键。`Chart.yaml` 示例见 [Chart 打包与仓库工作流](../guide/chart-distribution.md)。

## 当前边界

该包不渲染模板，也不修改 Kubernetes 资源。
