# 发布工作流

## 你在解决什么问题

发布工作流会组合渲染、Kubernetes apply/delete/wait、hooks 和 release history。只有当应用确实拥有部署动作时才走这条路径。

## 安装哪些包

```powershell
dotnet add package HelmSharp.Action --version 1.1.0
```

## 完整最小代码

先 dry-run：

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#dry-run-release{csharp}

审批后再 apply：

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#apply-release{csharp}

## 关键 API 为什么这样用

`HelmClient.UpgradeInstallAsync` 是 install/upgrade 的主要入口。它会加载 Chart、合并 values、渲染 manifests、按需应用 CRDs、执行 hooks、等待资源 ready，并保存 release history。

## 生产环境注意事项

- 预览流程保持 `DryRun = true`，审批通过后才切换为 `false`。
- 显式设置 `TimeoutSeconds`、`Wait` 和 `WaitForJobs`。
- 在产品日志里记录 `CommandResult.StandardError` 和 `ExitCode`。

## 下一步

阅读 [Kubernetes 操作](kubernetes-operations.md) 了解低层 apply/delete/wait。
