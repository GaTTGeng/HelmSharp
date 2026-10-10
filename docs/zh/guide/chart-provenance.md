# Chart 溯源与签名

溯源文件（`.prov`）用于证明 Chart 归档来自可信发布方且未被篡改。HelmSharp 生成并验证与 Helm 兼容的 OpenPGP 明文签名溯源文件。

## 完整性与真实性

两个概念经常被混淆：

| 保证 | 证明什么 | API |
| --- | --- | --- |
| **完整性** | Chart 字节与记录的 SHA-256 摘要一致。 | `CheckDigestAsync`（不提供真实性） |
| **真实性** | 你信任的密钥签名了摘要与元数据，且 Chart 一致。 | `VerifyAsync`（OpenPGP） |

完整性本身**不是**真实性。明文 `sha256:` 字段可以被任何能替换 Chart 的人改写。只有绑定到你信任的密钥的 OpenPGP 签名才能确立来源。

## 签名 Chart

签名需要显式传入私钥材料。HelmSharp 不生成密钥、不读取 GPG agent、不持久化任何机密。

```csharp
using HelmSharp.Action;

// 加载私钥（二进制或 ASCII 装甲）。
using var signingKey = HelmProvenanceSigningKey.FromSecretKeyFile(
    @"C:\secure\signing-key.gpg",
    passphrase: "key-passphrase".ToCharArray());

// 签名已打包的 Chart 归档；在同目录写出 chart-1.0.0.tgz.prov。
var provPath = await HelmProvenance.SignAsync(
    @"C:\charts\chart-1.0.0.tgz",
    signingKey);
```

生成的 `.prov` 与 Helm 产出的明文签名布局一致（`Hash: SHA512`、Chart 元数据 YAML、`files:` 摘要映射，以及真实 OpenPGP 签名），`helm verify` 可直接验证。

## 验证 Chart

验证需要显式传入可信公钥。没有隐式密钥环、没有首次信任（TOFU）：不提供签名者密钥就会验证失败。

```csharp
using HelmSharp.Action;

// 加载你信任的发布方公钥。
var trustedKeys = HelmProvenanceTrustedKey.FromKeyringFile(@"C:\secure\publisher-keys.gpg");

var result = await HelmProvenance.VerifyAsync(
    @"C:\charts\chart-1.0.0.tgz",
    trustedKeys,
    provPath: null); // 默认为 chart-1.0.0.tgz.prov

if (result.IsValid)
{
    Console.WriteLine($"签名者：{result.SignerUserId}（{result.SignerFingerprint}）");
}
else
{
    Console.WriteLine($"拒绝：{result.FailureReason}");
}
```

`VerifyAsync` 同时检查三个条件并返回结果对象：

- `SignatureValid` — 签名正文上的 OpenPGP 签名在密码学上有效。
- `SignerTrusted` — 签名密钥与传入的可信密钥之一匹配。
- `DigestMatches` — 归档 SHA-256 与签名的 `files:` 摘要一致。

仅当三者全部成立时 `IsValid` 才为 true。失败时查看 `FailureReason` 与各标志位，可区分内容被篡改与签名者不受信。

## 信任模型

- 调用方在每次 `VerifyAsync` 调用时显式提供可信密钥。
- 即使签名在密码学上有效，只要签名密钥不在可信集合中就会被拒绝（`SignerTrusted` 为 false）。
- Chart 字节被篡改、签名元数据被篡改、装甲格式错误、`.prov` 缺失，均按失败关闭处理。
- 结果报告签名者身份（`SignerFingerprint`、`SignerKeyId`、`SignerUserId`），便于你在业务层实施密钥白名单、密钥轮换等策略。

## 不提供真实性的摘要检查

仅需完整性的工作流——例如在可信流水线内检测意外损坏——可使用 `CheckDigestAsync`：

```csharp
bool intact = await HelmProvenance.CheckDigestAsync(@"C:\charts\chart-1.0.0.tgz");
```

该方法将归档 SHA-256 与 `.prov` 中的明文摘要比对。**不验证任何签名、不提供任何真实性。** 不要将其当作来源证明。

`ExtractSha256` 与 `ExtractMetadata` 同样是不提供真实性的解析辅助方法。

## 从旧版伪签名文件迁移

早期 HelmSharp 版本写出的 `.prov` 文件，其 `PGP SIGNATURE` 块包含的是 Base64 的 SHA-512 摘要而非真实 OpenPGP 签名。这些文件看起来像已签名的溯源文件，但任何能替换 Chart 的人都可以伪造。

**变更内容：** `GenerateProvFileAsync` 与仅做哈希比较的 `VerifyAsync` 重载已移除。旧版文件无法通过真实性验证——该块不是有效的 OpenPGP 签名——`FailureReason` 会给出迁移提示。

**迁移步骤：**

1. 使用 `HelmProvenance.SignAsync` 与真实 OpenPGP 密钥重新签名现有 Chart 归档。
2. 将新的 `.prov` 文件随归档一起分发。
3. 仅需完整性的调用方可暂时使用 `CheckDigestAsync`（可读取旧版摘要），并计划迁移到 `VerifyAsync`。

旧版文件仍可被 `ExtractSha256` 与 `ExtractMetadata` 解析，便于盘点需要重签的内容。

## 密钥输入参考

| 类型 | 工厂方法 | 说明 |
| --- | --- | --- |
| `HelmProvenanceSigningKey` | `FromSecretKeyData`、`FromSecretKeyFile` | 接受二进制或 ASCII 装甲的私钥与密钥环，支持口令。`IDisposable`，销毁时清零内存中的口令。 |
| `HelmProvenanceTrustedKey` | `FromPublicKeyData`、`FromPublicKeyFile`、`FromKeyringFile`、`FromKeyringData` | 接受二进制或 ASCII 装甲的公钥与密钥环；也接受私钥环，仅使用其公钥部分。 |

两种类型都会立即复制调用方缓冲区。密钥材料与口令从不写入日志。

## Helm 互操作

溯源文件与 Helm 格式字节兼容：

- HelmSharp 签名的 Chart 可用 `helm verify chart-1.0.0.tgz --keyring pubring.gpg` 验证。
- `helm package --sign` 签名的 Chart 可用 `HelmProvenance.VerifyAsync` 验证。
- 旧版 HelmSharp 伪签名文件会被 `helm verify` 与 `VerifyAsync` 同时拒绝。
