# Values 与覆盖项

Values 是应用和 Chart 的交界面。构造一次后，保存产生它的输入；任何必须可复现的预览或部署决策都应使用同一份结果。

## 优先级

`HelmValues.BuildAsync` 从上到下合并来源；表中靠后的来源会覆盖同一路径上靠前的值。

| 顺序 | 来源 | 常见用途 |
| ---: | --- | --- |
| 1 | Chart 与子 Chart 默认值 | Chart 自带的 `values.yaml`。 |
| 2 | `valuesFiles` | 环境或产品默认配置，按从左到右顺序应用。 |
| 3 | `valuesContent` | 数据库、API 请求或生成配置中的 YAML。 |
| 4 | `setFileValues` | 将文件内容赋给某个 values 路径。 |
| 5 | `setStringValues` | 必须保持为字符串的值。 |
| 6 | `setValues` | 类似 Helm `--set` 的标量覆盖项。 |
| 7 | `setJsonValues` | 将 JSON 对象或数组赋给某个路径。 |

下面的代码保留带前导零的镜像标签，同时传入结构化的服务端口列表：

```csharp
var license = await File.ReadAllTextAsync(licensePath, cancellationToken);

var values = await HelmValues.BuildAsync(
    chart,
    valuesFiles: ["values.base.yaml", "values.production.yaml"],
    valuesContent: """
        global:
          environment: production
        """,
    setValues: new Dictionary<string, string> { ["replicaCount"] = "3" },
    setFileValues: new Dictionary<string, string> { ["license.text"] = license },
    setStringValues: new Dictionary<string, string> { ["image.tag"] = "001" },
    setJsonValues: new Dictionary<string, string>
    {
        ["service.ports"] = """[{"name":"http","port":80}]"""
    },
    cancellationToken: cancellationToken);
```

## 选择合适的覆盖形式

| 输入 | 适用场景 | 不适用场景 |
| --- | --- | --- |
| `setValues` | `replicaCount=3` 这类简单标量。 | 值的类型或前导零不能丢失。 |
| `setStringValues` | tag、ID、编号必须保持文本。 | Chart 应收到布尔值或数字。 |
| `setJsonValues` | 调用方持有列表或对象，并能校验 JSON。 | 人工编辑 YAML 时；此时应使用 values 文件。 |
| `setFileValues` | 值就是证书、许可等文件的内容。 | 只有文件路径时；先读出内容。 |

## 安全处理输入

用户或租户提交的 values 应被视为不受信任的配置。限制内联 YAML 大小，校验允许覆盖的路径，并将 values 文件名与显式覆盖项随渲染产物一起保存。values 和清单中可能含有机密，不能写入普通应用日志。

如需让条件分支匹配真实集群版本，请看[按目标集群渲染](template-rendering.md)。如需提交到集群，请使用[安装和升级 Release](release-workflows.md)。
