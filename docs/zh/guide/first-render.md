# 第一次渲染

## 你在解决什么问题

如果应用只需要 Kubernetes YAML，用于预览、校验、策略检查、GitOps 或漂移检测，就先走只渲染路径。

## 安装哪些包

```powershell
dotnet add package HelmSharp.Chart --version 1.1.0
dotnet add package HelmSharp.Engine --version 1.1.0
```

## 完整最小代码

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#render-first-chart{csharp}

## 关键 API 为什么这样用

`HelmChartLoader.LoadAsync` 加载 Chart 元数据、values、templates、CRDs、文件、依赖和 subcharts。`HelmValues.BuildAsync` 按 Helm 风格合并 values。`HelmTemplateRenderer.Render()` 返回 manifests，`RenderNotes()` 返回 `NOTES.txt`。

## 生产环境注意事项

- 从服务边界传入绝对 chart 路径，便于复现问题。
- 保存每次预览使用的 values 输入。
- 排查差异时可使用 [对比工具](../compare.md) 或 Helm CLI golden test。

## 下一步

阅读 [Values](values.md)，正确建模用户覆盖项。
