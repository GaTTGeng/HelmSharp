# HelmSharp.Chart

`HelmSharp.Chart` 负责 Chart 输入。它加载目录或 `.tgz` 归档，提供 Chart 元数据和文件，解析已打包子 Chart，并构建渲染器使用的 values 字典。

```powershell
dotnet add package HelmSharp.Chart --version 1.3.1
```

## 用它完成渲染的输入部分

| 类型 | 作用 |
| --- | --- |
| `HelmChartLoader` | 加载 `Chart.yaml`、模板、文件、CRD、values、依赖和归档。 |
| `HelmChart` | 传给 values 与渲染 API 的已加载 Chart 对象。 |
| `HelmValues` | 合并默认值、values 文件、内联 YAML 和 set 风格覆盖项。 |
| `HelmYaml` | 读写 YAML 兼容对象。 |
| `HelmChartDependency` / `HelmChartLockEntry` | 检查依赖和 lock 元数据。 |

本包不依赖 Kubernetes，也不执行模板。预览场景将它与 `HelmSharp.Engine` 配对；需要完整生命周期时则安装 `HelmSharp.Action`。

## 会影响 values 的依赖细节

`charts/` 下的已打包 Chart 会被加载为子 Chart。多个别名或版本存在时，`Chart.lock` 条目确定被选中的版本。别名也会改变 values 键：名为 `redis`、别名为 `cache` 的依赖从 `cache:` 接收 values。

合并语义请看[Values 与覆盖项](../guide/values.md)，update/build 行为请看[Chart 交付](../guide/chart-distribution.md)。全部成员见[生成的 Chart API](../api/generated/chart.md)。
