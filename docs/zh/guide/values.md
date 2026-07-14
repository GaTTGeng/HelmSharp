# 值配置（Values）

## 你在解决什么问题

值配置（values）往往是产品模型和 Helm Chart 的集成边界。HelmSharp 保留 Helm 使用者熟悉的优先级模型，方便复用现有 values 文件和 `--set` 风格覆盖。

## 安装哪些包

```powershell
dotnet add package HelmSharp.Chart --version 1.1.1
```

## 完整最小代码

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#values-precedence{csharp}

## 关键 API 为什么这样用

`HelmValues.BuildAsync` 从低到高合并：

| 输入 | 含义 |
| --- | --- |
| Chart 默认值 | Chart 自带 `values.yaml`。 |
| 子 Chart 默认值 | 依赖名称或别名下的子 Chart 默认值。 |
| `valuesFiles` | 一个或多个 values 文件，按顺序应用。 |
| `valuesContent` | 数据库、请求或生成配置里的内联 YAML。 |
| `setFileValues` | 将文件内容写入某个 values 路径。 |
| `setStringValues` | 强制保持字符串。 |
| `setValues` | 普通 `--set` 风格标量覆盖。 |
| `setJsonValues` | JSON 对象或数组覆盖。 |

## 生产环境注意事项

- 在代码评审里保留优先级顺序。
- `SetFileValues` 的 value 是文件内容，不是文件路径。
- `001` 这类 tag 用 `SetStringValues`。

## 下一步

阅读 [模板渲染](template-rendering.md)，了解 Capabilities、NOTES 和 CRD 输出。
