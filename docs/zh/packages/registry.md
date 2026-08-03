# HelmSharp.Registry

`HelmSharp.Registry` 提供 OCI registry 工作的扩展契约。大多数应用无需直接安装；高层包已经引用它。

```powershell
dotnet add package HelmSharp.Registry --version 1.3.1
```

`IOciRegistryClient` 是集成接缝，不代表已具备完整 Helm OCI 行为。不要因为这个包存在，就假定 OCI 认证、推送、拉取、provenance 或凭据对齐可用。在实现集成前，查看[兼容性边界](../helm-compatibility.md)和[生成的 Registry API](../api/generated/registry.md)。
