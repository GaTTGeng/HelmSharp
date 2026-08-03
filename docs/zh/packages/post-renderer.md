# HelmSharp.PostRenderer

`HelmSharp.PostRenderer` 提供 `IPostRenderer`：在模板执行完成、进入下一工作流步骤前转换渲染后清单文本的契约。

```powershell
dotnet add package HelmSharp.PostRenderer --version 1.3.1
```

它适合产品拥有的转换，如策略标签、注解或确定性规范化。实现应无副作用，并用有代表性的 YAML 测试。Chart 特有的行为应放进 Chart 模板，避免把 post-renderer 变成第二种隐藏模板语言。

接口签名请看[生成的 Post-renderer API](../api/generated/postrenderer.md)。
