# HelmSharp.Repo

`HelmSharp.Repo` 管理传统 HTTP Chart 仓库：本地仓库定义、缓存索引、搜索、带摘要校验的拉取，以及 `index.yaml` 生成。

```powershell
dotnet add package HelmSharp.Repo --version 1.3.1
```

主要 API 是 `HelmChartRepository`。长期运行的服务应传入明确的 `HelmRepositoryOptions` 路径，避免租户或并发任务共享仓库、凭据和缓存索引。仅传关键词的 `SearchRepoAsync` 重载只读取已配置仓库的缓存索引，不访问网络；传入仓库 URL 的重载会先获取并缓存该仓库索引，再执行搜索。

`HelmRepoIndexer` 生成仓库元数据，`HelmPullRequest` 描述拉取和可选安全解包。通过仓库索引解析且索引中提供摘要的 Chart，默认由 `VerifyDigest` 校验下载的归档。直接使用 `.tgz` URL 时没有索引提供的预期摘要，因此应由调用方自行固定并校验内容。传统 HTTP 仓库、本地依赖和语义版本选择在范围内；OCI 认证和 provenance 验证不在范围内。

完整流程见[Chart 交付](../guide/chart-distribution.md)，成员见[生成的 Repo API](../api/generated/repo.md)。
