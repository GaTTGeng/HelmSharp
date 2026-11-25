# HelmSharp.Registry

`HelmSharp.Registry` 提供 OCI registry 工作的扩展契约。大多数应用无需直接安装；高层包已经引用它。

```powershell
dotnet add package HelmSharp.Registry --version 2.0.1
```

`IOciRegistryClient` 是集成接缝，不代表已具备完整 Helm OCI 行为。不要因为这个包存在，就假定 OCI 认证、推送、拉取或凭据对齐可用。在实现集成前，查看[兼容性边界](../helm-compatibility.md)和[生成的 Registry API](../api/generated/registry.md)。

现有仓库拉取入口会在发送注册表请求前校验 OCI 引用。它接受 `oci://host[:port]/repository/chart`，后面可选 OCI 标签。仓库路径必须为小写，标签须符合 OCI 字符规则。单独传入的版本会将 SemVer 构建元数据中的 `+` 转换为 `_`；若引用中已有标签，版本必须与标签的原文一致。小写的 `sha256` 或 `sha512` 摘要可以被解析，但拉取入口会拒绝此类引用，直至支持制品校验。此校验不代表已完整支持注册表拉取或制品校验。
