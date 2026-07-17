# 发布工作流

## 你在解决什么问题

发布工作流会组合渲染、Kubernetes 提交/删除/等待、hooks 和发布历史。只有当应用确实拥有部署动作时才走这条路径。

## 安装哪些包

```powershell
dotnet add package HelmSharp.Action --version 1.2.0
```

## 完整最小代码

先试运行：

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#dry-run-release{csharp}

审批后再提交：

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#apply-release{csharp}

## 关键 API 为什么这样用

`HelmClient.UpgradeInstallAsync` 是安装/升级的主要入口。它会加载 Chart、合并 values、渲染清单、按需应用 CRDs、执行 hooks、等待资源就绪，并保存发布历史。

## 生产环境注意事项

- 预览流程保持 `DryRun = true`，审批通过后才切换为 `false`。
- 显式设置 `TimeoutSeconds`、`Wait` 和 `WaitForJobs`。
- 在产品日志里记录 `CommandResult.StandardError` 和 `ExitCode`。

## 下一步

阅读 [Kubernetes 操作](kubernetes-operations.md) 了解低层提交、删除和等待行为。
