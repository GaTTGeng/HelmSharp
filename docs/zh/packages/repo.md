# HelmSharp.Repo

`HelmSharp.Repo` 管理传统 HTTP Chart 仓库：本地仓库定义、缓存索引、搜索、带摘要校验的拉取，以及 `index.yaml` 生成。

```powershell
dotnet add package HelmSharp.Repo --version 1.3.1
```

主要 API 是 `HelmChartRepository`。长期运行的服务应传入明确的 `HelmRepositoryOptions` 路径，避免租户或并发任务共享仓库、凭据和缓存索引。搜索刻意只读本地缓存；需要远端最新结果时，先刷新索引。

`HelmRepoIndexer` 生成仓库元数据，`HelmPullRequest` 描述固定版本的拉取和可选安全解包。传统 HTTP 仓库、本地依赖、语义版本选择和摘要验证在范围内；OCI 认证和 provenance 验证不在范围内。

完整流程见[Chart 交付](../guide/chart-distribution.md)，成员见[生成的 Repo API](../api/generated/repo.md)。
