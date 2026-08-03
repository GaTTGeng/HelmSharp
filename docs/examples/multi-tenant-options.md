# Keep tenant defaults isolated

An `IHelmOptionsProvider` is a policy boundary. Use it to derive allowed namespaces, field-manager names, Kubernetes capabilities, and repository/cache locations from a tenant identity. Do not let callers override those defaults by passing raw environment paths or cluster configuration.

```csharp
public sealed class TenantHelmOptionsProvider(TenantContext tenant) : IHelmOptionsProvider
{
    public ValueTask<HelmExecutionOptions> GetHelmAsync(
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new HelmExecutionOptions
        {
            DefaultNamespace = $"tenant-{tenant.Slug}",
            FieldManager = "my-platform",
            TimeoutSeconds = 300,
            KubeVersion = tenant.KubernetesVersion,
            ApiVersions = tenant.ApiVersions
        });
}
```

Build the client with the tenant-scoped provider, but still validate every request's release name, chart identity, allowed values paths, and namespace. A provider supplies defaults; it is not authorization by itself.

For repository workflows, create a tenant-specific `HelmRepositoryOptions` with explicit configuration and cache paths. This prevents repository credentials and stale index data from crossing tenant boundaries. The [distribution guide](../guide/chart-distribution.md) shows the pattern.
