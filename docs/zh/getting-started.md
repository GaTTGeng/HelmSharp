# 快速开始

这条 10 分钟路线帮助你判断：应用只是需要渲染后的 manifests，还是需要完整 release 工作流。

## 1. 安装最小包组合

只渲染预览：

```powershell
dotnet add package HelmSharp.Chart --version 1.1.0
dotnet add package HelmSharp.Engine --version 1.1.0
```

发布工作流：

```powershell
dotnet add package HelmSharp.Action --version 1.1.0
```

## 2. 不通过 Helm CLI 渲染

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#render-first-chart{csharp}

这条路径不会调用 `helm`，也不会修改集群。

## 3. 进入 dry-run 发布工作流

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#dry-run-release{csharp}

产品流程明确审批前，保持 `DryRun = true`。

## 4. 选择下一页

| 需要 | 阅读 |
| --- | --- |
| 安装细节 | [安装](guide/installation.md) |
| Values 优先级 | [Values](guide/values.md) |
| Capabilities 和 NOTES | [模板渲染](guide/template-rendering.md) |
| Install/upgrade 行为 | [发布工作流](guide/release-workflows.md) |
| 真实例子 | [示例](examples/render-preview-api.md) |
| 成员级参考 | [API 参考](api/index.md) |

## 当前兼容性基线

HelmSharp 1.1.0 在 5 个真实公开 Chart 上取得 Pass 判定：129/129 个模板与 `helm template` 在规范化后逐字节一致。详见 [Helm 兼容性](helm-compatibility.md)。
