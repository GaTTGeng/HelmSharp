# HelmSharp.Registry

`HelmSharp.Registry` 提供 OCI registry 工作的扩展契约。大多数应用无需直接安装；高层包已经引用它。

```powershell
dotnet add package HelmSharp.Registry --version 2.0.1
```

`IOciRegistryClient` 是集成接缝，不代表已具备完整 Helm OCI 行为。不要因为这个包存在，就假定 OCI 认证、推送、拉取或凭据对齐可用。在实现集成前，查看[兼容性边界](../helm-compatibility.md)和[生成的 Registry API](../api/generated/registry.md)。

现有仓库拉取入口会在发送注册表请求前校验 OCI 引用。它接受 `oci://host[:port]/repository/chart`，后面可选 OCI 标签。仓库路径必须为小写，标签须符合 OCI 字符规则。单独传入的版本会将 SemVer 构建元数据中的 `+` 转换为 `_`；若引用中已有标签，版本必须与标签的原文一致。小写的 `sha256` 或 `sha512` 摘要可以被解析，但拉取入口会拒绝此类引用，直至支持制品校验。此校验不代表已完整支持注册表拉取或制品校验。

## 注册表凭据解析

调用 `RegistryCredentialResolver.ResolveAsync` 时传入主机名或带方括号的 IPv6 地址，可附加端口。解析器将 DNS 名称转换为小写 ASCII IDN，并规范化 IP 地址。显式端口属于凭据范围，包括 443。注册表参数不能包含协议、仓库路径、用户信息、查询字符串或片段。

凭据来源按以下优先级解析。

1. 本次操作显式提供的 `RegistryCredential`。
2. 当 `HELMSHARP_REGISTRY_HOST` 与注册表匹配时，读取 `HELMSHARP_REGISTRY_USERNAME` 和 `HELMSHARP_REGISTRY_PASSWORD`。两者均为必填。未绑定主机的环境凭据会被忽略。
3. 显式指定的 Helm 配置文件，或由 `HELM_REGISTRY_CONFIG` 指定的文件。支持读取 `auths` 下的 `auth` 字段，其值为 UTF-8 `username:password` 的 Base64 编码。键可以是主机及可选端口，也可以是根路径的 HTTPS URL。同一规范化注册表的重复条目会报错。
4. 可选的 `IRegistryCredentialProvider`。它接收规范化的主机及端口，返回对应凭据或 null。

文件或条目缺失时会继续检查下一来源。适用于当前主机的 `credHelpers` 条目或已配置的 `credsStore` 会明确报错。配置不支持令牌字段，也不支持用独立用户名和密码字段代替 `auth`。解析器只读取凭据，诊断信息不包含输入值和文件路径。自定义提供程序应对诊断信息中的秘密值脱敏。

TLS 验证、自定义 CA、客户端证书和代理属于传输层。登出应从可写存储中移除精确匹配的主机及端口。显式凭据、环境凭据和只读 Helm 配置仍可用。当前解析器独立运行，Action 登录和登出接线及安全存储由 [#267](https://github.com/GaTTGeng/HelmSharp/issues/267) 跟踪。
