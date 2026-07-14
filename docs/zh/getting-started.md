# 快速开始

这条 10 分钟路线帮助你判断：应用只是需要渲染后的清单，还是需要完整发布工作流。

## 1. 安装最小包组合

只渲染预览：

```powershell
dotnet add package HelmSharp.Chart --version 1.1.1
dotnet add package HelmSharp.Engine --version 1.1.1
```

发布工作流：

```powershell
dotnet add package HelmSharp.Action --version 1.1.1
```

## 2. 不通过 Helm CLI 渲染

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#render-first-chart{csharp}

这条路径不会调用 `helm`，也不会修改集群。

## 3. 进入试运行发布工作流

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#dry-run-release{csharp}

产品流程明确审批前，保持 `DryRun = true`。

## 4. 选择下一页

| 需要 | 阅读 |
| --- | --- |
| 安装细节 | [安装](guide/installation.md) |
| 值配置优先级 | [值配置（Values）](guide/values.md) |
| Capabilities 和 NOTES | [模板渲染](guide/template-rendering.md) |
| 安装/升级行为 | [发布工作流](guide/release-workflows.md) |
| 真实例子 | [示例](examples/render-preview-api.md) |
| 成员级参考 | [API 参考](api/index.md) |

## 当前兼容性基线

在生产环境依赖某个 Helm 边缘行为前，请先查看 [Helm 兼容性](helm-compatibility.md)，确认当前 golden-test 覆盖范围、已知边界和问题报告方式。
