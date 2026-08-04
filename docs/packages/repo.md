# HelmSharp.Repo

`HelmSharp.Repo` manages traditional HTTP chart repositories: local repository definitions, cached indexes, search, digest-checked pull, and `index.yaml` generation.

```powershell
dotnet add package HelmSharp.Repo --version 1.3.1
```

`HelmChartRepository` is the primary API. Give long-running services explicit `HelmRepositoryOptions` paths so repositories, credentials, and cached indexes are not shared between tenants or concurrent jobs. The keyword-only `SearchRepoAsync` overload reads configured cached indexes and does not access the network. The overload that accepts a repository URL fetches and caches that repository index before searching.

`HelmRepoIndexer` writes repository metadata, while `HelmPullRequest` describes a pull and optional safe extraction. For a chart resolved through a repository index that publishes a digest, `VerifyDigest` validates the downloaded archive by default. A direct `.tgz` URL has no index-provided digest, so pin and validate its content separately. Traditional HTTP repositories, local dependencies, and semantic-version selection are in scope. OCI authentication and provenance verification are not.

See [Chart delivery](../guide/chart-distribution.md) for complete flows and the [generated Repo API](../api/generated/repo.md) for members.
