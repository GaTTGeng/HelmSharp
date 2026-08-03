# 隔离租户默认配置

`IHelmOptionsProvider` 是一条策略边界。用它从租户身份推导允许使用的命名空间、field manager、Kubernetes capabilities 和仓库/缓存目录；不要让调用方通过原始环境路径或集群配置覆盖这些默认值。

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

用租户范围的 provider 创建客户端后，仍要校验每个请求的 release 名称、Chart 标识、允许的 values 路径和命名空间。provider 只提供默认值，本身不是授权系统。

仓库工作流中，为每个租户创建带明确配置和缓存路径的 `HelmRepositoryOptions`。这样可以避免仓库凭据和陈旧索引跨租户泄漏；模式见[分发指南](../guide/chart-distribution.md)。
