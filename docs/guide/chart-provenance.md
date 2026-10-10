# Chart provenance and signing

Provenance files (`.prov`) let you prove that a chart archive came from a trusted publisher and has not been modified. HelmSharp generates and verifies Helm-compatible OpenPGP clearsigned provenance files.

## Integrity vs authenticity

Two different guarantees are often confused:

| Guarantee | What it proves | API |
| --- | --- | --- |
| **Integrity** | The chart bytes match a recorded SHA-256 digest. | `CheckDigestAsync` (non-authenticating) |
| **Authenticity** | A key you trust signed the digest and metadata, and the chart matches. | `VerifyAsync` (OpenPGP) |

Integrity alone is **not** authenticity. A plaintext `sha256:` field can be rewritten by anyone who can replace the chart. Only an OpenPGP signature bound to a key you trust establishes origin.

## Signing a chart

Signing requires explicit private-key material. HelmSharp never generates keys, never reads your GPG agent, and never persists secrets.

```csharp
using HelmSharp.Action;

// Load a secret key (binary or ASCII-armored) once.
using var signingKey = HelmProvenanceSigningKey.FromSecretKeyFile(
    @"C:\secure\signing-key.gpg",
    passphrase: "key-passphrase".ToCharArray());

// Sign a packaged chart archive; writes chart-1.0.0.tgz.prov alongside it.
var provPath = await HelmProvenance.SignAsync(
    @"C:\charts\chart-1.0.0.tgz",
    signingKey);
```

The resulting `.prov` uses the same clearsigned layout Helm produces (`Hash: SHA512`, chart metadata YAML, a `files:` digest map, and a real OpenPGP signature), so `helm verify` accepts it.

## Verifying a chart

Verification requires explicit trusted public keys. There is no implicit keyring and no trust-on-first-use: if you do not supply the signer's key, verification fails.

```csharp
using HelmSharp.Action;

// Load the public key(s) you trust for this chart publisher.
var trustedKeys = HelmProvenanceTrustedKey.FromKeyringFile(@"C:\secure\publisher-keys.gpg");

var result = await HelmProvenance.VerifyAsync(
    @"C:\charts\chart-1.0.0.tgz",
    trustedKeys,
    provPath: null); // defaults to chart-1.0.0.tgz.prov

if (result.IsValid)
{
    Console.WriteLine($"Signed by {result.SignerUserId} ({result.SignerFingerprint})");
}
else
{
    Console.WriteLine($"Rejected: {result.FailureReason}");
}
```

`VerifyAsync` checks all three conditions and returns a result object:

- `SignatureValid` — the OpenPGP signature over the signed body is cryptographically valid.
- `SignerTrusted` — the signing key matches one of the supplied trusted keys.
- `DigestMatches` — the archive SHA-256 equals the signed `files:` digest.

`IsValid` is true only when all three hold. On failure, inspect `FailureReason` and the individual flags to distinguish tampered content from an untrusted signer.

## Trust model

- The caller supplies trusted keys explicitly to every `VerifyAsync` call.
- A valid signature from a key outside the trusted set is rejected (`SignerTrusted` is false) even when the signature is cryptographically sound.
- Tampered chart bytes, tampered signed metadata, malformed armor, and missing `.prov` files all fail closed.
- Signer identity is reported (`SignerFingerprint`, `SignerKeyId`, `SignerUserId`) so you can apply additional policy (allow-lists, key rotation) in your own code.

## Non-authenticating digest check

For workflows that only need integrity — for example, detecting accidental corruption inside a trusted pipeline — use `CheckDigestAsync`:

```csharp
bool intact = await HelmProvenance.CheckDigestAsync(@"C:\charts\chart-1.0.0.tgz");
```

This compares the archive SHA-256 against the plaintext digest in the `.prov` file. It verifies **no signature** and provides **no authenticity**. Do not present it as proof of origin.

`ExtractSha256` and `ExtractMetadata` are likewise non-authenticating parsers for inspecting provenance content.

## Migrating from legacy pseudo-signature files

Earlier HelmSharp versions wrote `.prov` files whose `PGP SIGNATURE` block contained a Base64 SHA-512 digest instead of a real OpenPGP signature. Those files looked like signed provenance but were forgeable by anyone who could replace the chart.

**What changed:** `GenerateProvFileAsync` and the hash-only `VerifyAsync` overload are removed. Legacy files fail authenticity verification — the block is not a valid OpenPGP signature — and report a migration message in `FailureReason`.

**How to migrate:**

1. Re-sign existing chart archives with `HelmProvenance.SignAsync` and a real OpenPGP key.
2. Distribute the new `.prov` files alongside the archives.
3. Callers that only need integrity can temporarily use `CheckDigestAsync`, which reads legacy digests, but should plan to move to `VerifyAsync`.

Legacy files remain parseable by `ExtractSha256` and `ExtractMetadata` so you can inventory what needs re-signing.

## Key input reference

| Type | Factory methods | Notes |
| --- | --- | --- |
| `HelmProvenanceSigningKey` | `FromSecretKeyData`, `FromSecretKeyFile` | Accepts binary or ASCII-armored secret keys and keyrings. Optional passphrase. `IDisposable` zeroes the in-memory passphrase. |
| `HelmProvenanceTrustedKey` | `FromPublicKeyData`, `FromPublicKeyFile`, `FromKeyringFile`, `FromKeyringData` | Accepts binary or ASCII-armored public keys and keyrings. Secret keyrings are accepted; only public halves are used. |

Both types copy caller buffers immediately. Key material and passphrases are never logged.

## Helm interoperability

Provenance files are byte-compatible with Helm's format:

- A chart signed by HelmSharp verifies with `helm verify chart-1.0.0.tgz --keyring pubring.gpg`.
- A chart signed by `helm package --sign` verifies with `HelmProvenance.VerifyAsync`.
- Legacy HelmSharp pseudo-signature files are rejected by both `helm verify` and `VerifyAsync`.
