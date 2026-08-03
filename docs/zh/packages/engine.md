# HelmSharp.Engine

`HelmSharp.Engine` 在托管代码中执行 Helm 风格模板。应用需要清单文本、但不需要 Kubernetes 生命周期客户端时，使用这个包。

```powershell
dotnet add package HelmSharp.Engine --version 1.3.1
```

它需要与 `HelmSharp.Chart` 一起安装。常用入口是 `HelmTemplateRenderer`：传入已加载 Chart、合并后的 values、release 标识和可选 Kubernetes capabilities，再调用 `Render()` 或 `RenderNotes()`。

| 类型 | 用途 |
| --- | --- |
| `HelmTemplateRenderer` | 执行模板并返回清单或 notes。 |
| `TemplateContext` | 表达式可见的运行时上下文。 |
| `ApiVersionSet` | 建模 `.Capabilities.APIVersions`。 |
| `TemplateParseException` | 定位格式不正确的模板输入。 |

`Functions` 和 `Utilities` 命名空间是实现 Helm/Sprig 行为的渲染器内部设施。它们会出现在生成参考中，但不是通用工具库。应用应使用渲染器；Chart 要依赖某个 helper 前，请查[模板函数矩阵](../template-function-compatibility.md)。

release 和 capabilities 上下文见[按目标集群渲染](../guide/template-rendering.md)，成员查询见[生成的 Engine API](../api/generated/engine.md)。
