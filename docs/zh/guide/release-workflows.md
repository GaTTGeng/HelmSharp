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
- `Install = false` 会让不存在的发布失败，而不是悄悄创建它；仅升级接口应使用此选项。
- `ReuseValues = true` 从已存储的发布 values 开始，再覆盖本次提供的 values。默认行为和 `ResetValues = true` 从 Chart 默认值开始。不能同时启用 `ReuseValues` 与 `ResetValues`。
- `TimeoutSeconds` 覆盖 Kubernetes 提交、hooks、就绪等待和取消。`Atomic` 隐含就绪等待；`WaitForJobs` 只能与 `Wait`（或 `Atomic`）一起使用。
- `Description`、`Labels` 和 `MaxHistory` 会随结果 revision 持久化。`RollbackAsync(new HelmRollbackRequest { ... })` 在保留旧重载的同时，为回滚提供相同的超时、等待、hook、描述、标签和历史控制。
- 没有托管实现的选项（例如 `Force`、接管资源、仓库 TLS/认证、来源验证或选择 server-side apply）会在任何集群变更前返回清晰诊断。
- 在产品日志里记录 `CommandResult.StandardError` 和 `ExitCode`。

## 下一步

阅读 [Kubernetes 操作](kubernetes-operations.md) 了解低层提交、删除和等待行为。
