# Kind 生命周期测试通道

Kind 生命周期测试通道会在真实集群和真实控制器上验证 Kubernetes apply、wait、hook、rollback、uninstall 以及 release Secret 行为。它与快速的单元测试、渲染 golden 测试、包契约测试和文档检查相互独立。

## 通道覆盖范围

| 场景 | 断言内容 |
| --- | --- |
| 安装到卸载 | 针对同一 fixture chart 覆盖 install、status/history、upgrade、rollback 和 purge |
| 命名空间与资源标识 | 创建命名空间、命名空间内资源标识，以及无命名空间的集群级 CRD |
| 升级与 CRD 发现 | 升级后 chart 拥有的字段会变更；自定义资源通过集群发现路径提交 |
| 就绪与 hook | Deployment 就绪、hook Job 成功完成，以及受控的 hook Job 失败 |
| 失败后回滚 | 失败的升级会记录 failed 修订；回滚会恢复先前资源 |
| 保留式卸载 | `KeepHistory` 会保留 release Secret；purge 会将其删除 |
| 等待超时 | 拉取失败的工作负载会记录 failed 修订，而不是留下不明确状态 |
| Release Secret 契约 | Helm v3 的 `sh.helm.release.v1.<name>.v<rev>` Secret 具备预期的 type、labels 与 payload key |

Helm CLI 仅可用作测试对照，绝不会成为 SDK 运行时依赖。

## 维护者如何触发通道

1. 推送到 `master` — 通道会自动运行。
2. 在 Actions 页面手动运行 **Kind Lifecycle**。
3. 在 PR 上添加 `kind-lifecycle` 标签。作业会在下一次 synchronize 或打标签事件时运行。

待通道在若干版本中稳定后，可在分支保护中将 `Kind lifecycle integration` 设为必需状态检查。

## 本地运行

前置条件：

- Docker
- [kind](https://kind.sigs.k8s.io/)
- .NET 8 或更高版本 SDK
- 一次性测试集群（不要指向生产环境）

```powershell
kind create cluster --name helmsharp-kind
$env:HELM_SHARP_KIND_TESTS = "1"
dotnet test tests/HelmSharp.Tests/HelmSharp.Tests.csproj --configuration Release --filter "Category=Kind"
kind delete cluster --name helmsharp-kind
```

当 `HELM_SHARP_KIND_TESTS` 不是 `1` 时，相关测试会给出明确原因并跳过，因此默认测试套件不依赖集群。

## 失败诊断

失败时工作流会上传 `kind-lifecycle-diagnostics`，其中包含：

- 工作负载的 `kubectl get` / `describe` 输出、事件与 Pod 日志
- Helm release Secret（来自一次性 kind 集群的 labels 与 payload）
- CRD 清单
- TRX 结果文件

诊断产物不包含 kubeconfig 或镜像仓库凭据。kind 集群会随 runner 一起销毁。

## 超时与重跑

- 作业超时为 25 分钟。
- 每个测试使用唯一 release 名与命名空间，并在结束后尽力清理。
- 通道可以安全重跑；中断运行遗留的对象会随集群一起销毁。
