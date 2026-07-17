# 模板渲染

## 你在解决什么问题

模板渲染把已加载的 Chart 和合并后的 values 转成 Kubernetes 清单。HelmSharp 在测试中用 Helm CLI 输出作为兼容性参照，而应用代码直接调用托管渲染器。

## 安装哪些包

```powershell
dotnet add package HelmSharp.Chart --version 1.2.0
dotnet add package HelmSharp.Engine --version 1.2.0
```

## 完整最小代码

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#template-with-capabilities{csharp}

## 关键 API 为什么这样用

`HelmTemplateRenderer` 向模板暴露 `.Release`、`.Chart`、`.Values`、`.Capabilities`、`.Files`、`.Template`、命名模板、`include`、`tpl` 和常见 Helm/Sprig 函数。`kubeVersion` 和 `apiVersions` 用于预览目标集群的输出。

## 生产环境注意事项

- 已知目标集群时显式传入 `kubeVersion` 和 `apiVersions`。
- 用户可见的说明用 `RenderNotes()` 单独渲染。
- 使用 `HelmClient.TemplateAsync` 且 `IncludeCRDs = true` 可获得类命令输出。

## 下一步

需要安装、升级、历史或回滚行为时阅读 [发布工作流](release-workflows.md)。
