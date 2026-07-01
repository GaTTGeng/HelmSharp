# HelmSharp.Chart

## 包职责

`HelmSharp.Chart` 负责加载 Chart 目录和 `.tgz` 归档，暴露 Chart 元数据，合并 values，并提供 YAML helper。

## 何时安装

任何只渲染集成都会用到：

```powershell
dotnet add package HelmSharp.Chart --version 1.1.0
```

## 依赖关系

该包依赖 `YamlDotNet`，不依赖 Kubernetes。

## 主要类型

| 类型 | 用途 |
| --- | --- |
| `HelmChartLoader` | 从目录或归档加载 Chart。 |
| `HelmChart` | 查看 Chart 元数据、templates、files、CRDs 和 subcharts。 |
| `HelmValues` | 按 Helm 优先级构建 merged values。 |
| `HelmYaml` | 序列化和反序列化 YAML 对象。 |
| `HelmChartDependency` | 查看 `Chart.yaml` 依赖元数据。 |
| `HelmChartLockEntry` | 查看 `Chart.lock` 条目。 |

## 常见组合

用 `HelmChartLoader` 加载，用 `HelmValues` 合并，然后传给 `HelmSharp.Engine` 的 `HelmTemplateRenderer`。

## 当前边界

该包不渲染模板，也不修改 Kubernetes 资源。
