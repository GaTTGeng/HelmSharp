# HelmSharp.Repo

`HelmSharp.Repo` manages traditional HTTP chart repositories: local repository definitions, cached indexes, search, digest-checked pull, and `index.yaml` generation.

```powershell
dotnet add package HelmSharp.Repo --version 1.3.1
```

`HelmChartRepository` is the primary API. Give long-running services explicit `HelmRepositoryOptions` paths so repositories, credentials, and cached indexes are not shared between tenants or concurrent jobs. Search intentionally reads the local cache; refresh an index when remote freshness matters.

`HelmRepoIndexer` writes repository metadata, while `HelmPullRequest` describes a pull and optional safe extraction. For a chart resolved through a repository index that publishes a digest, `VerifyDigest` validates the downloaded archive by default. A direct `.tgz` URL has no index-provided digest, so pin and validate its content separately. Traditional HTTP repositories, local dependencies, and semantic-version selection are in scope. OCI authentication and provenance verification are not.

See [Chart delivery](../guide/chart-distribution.md) for complete flows and the [generated Repo API](../api/generated/repo.md) for members.
