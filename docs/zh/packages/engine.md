# HelmSharp.Engine

`HelmSharp.Engine` 在托管代码中执行 Helm 风格模板。应用需要清单文本、但不需要 Kubernetes 生命周期客户端时，使用这个包。

```powershell
dotnet add package HelmSharp.Engine --version 1.3.1
```

它需要与 `HelmSharp.Chart` 一起安装。常用入口是 `HelmTemplateRenderer`：传入已加载 Chart、合并后的 values、release 标识和可选 Kubernetes capabilities，再调用 `Render()` 或 `RenderNotes()`。

| 类型 | 用途 |
| --- | --- |
| `HelmTemplateRenderer` | 执行模板并返回清单或 notes。 |
| `TemplateParseException` | 定位格式不正确的模板输入。 |

`TemplateContext`、`ApiVersionSet` 以及 `Functions` 和 `Utilities` 命名空间都是渲染器的内部实现细节，不是受支持的公开 API，也不会出现在生成的参考文档中。应用应使用渲染器；Chart 要依赖某个 helper 前，请查[模板函数矩阵](../template-function-compatibility.md)。

release 和 capabilities 上下文见[按目标集群渲染](../guide/template-rendering.md)，成员查询见[生成的 Engine API](../api/generated/engine.md)。
