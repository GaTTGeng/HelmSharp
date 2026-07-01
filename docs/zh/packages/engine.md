# HelmSharp.Engine

## 包职责

`HelmSharp.Engine` 在托管代码中渲染 Helm 风格模板。它通过聚焦测试用 Chart 和选定公开 Chart 的基准输出测试验证行为；如果你的 Chart 依赖特定 Helm 边缘行为，请先查看兼容性页面。

## 何时安装

只需要清单输出、不需要发布生命周期时，与 `HelmSharp.Chart` 一起安装：

```powershell
dotnet add package HelmSharp.Engine --version 1.1.0
```

## 依赖关系

该包引用 `HelmSharp.Chart`，并使用 YAML 序列化能力。

## 主要类型

| 类型 | 用途 |
| --- | --- |
| `HelmTemplateRenderer` | 渲染清单和 NOTES。 |
| `TemplateParseException` | 诊断模板解析失败。 |
| `TemplateContext` | 提供发布和 capabilities 能力上下文。 |
| `ApiVersionSet` | 建模 `.Capabilities.APIVersions`。 |
| `TemplateParser` / tokenizer / AST types | 解析器、分词器和 AST 类型的内部诊断入口，不是常规应用入口。 |

## 常见组合

`HelmChartLoader` 加载，`HelmValues.BuildAsync` 合并，`HelmTemplateRenderer` 渲染。

## 当前边界

`HelmSharp.Engine.Functions` 和 `HelmSharp.Engine.Utilities` 主要实现 Helm/Sprig 模板行为，不建议应用代码把它们当通用工具库依赖。
