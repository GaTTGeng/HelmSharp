using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Formats.Tar;
using System.IO.Compression;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using HelmSharp.Chart;

namespace HelmSharp.Action;

// ReSharper disable UnusedType.Global, UnusedMember.Global // Public provenance helpers are available to callers independently of HelmClient.
/// <summary>
/// Chart provenance: generates and verifies Helm-compatible <c>.prov</c> files.
/// </summary>
/// <remarks>
/// <para>
/// <b>Integrity vs authenticity.</b> A <c>.prov</c> file clearsigns chart metadata and the
/// archive SHA-256 with an OpenPGP key. <see cref="VerifyAsync"/> checks the cryptographic
/// signature against caller-supplied trusted keys, so a valid result means the chart was signed
/// by a key you trust and has not been altered — that is authenticity. The digest-only helpers
/// (<see cref="CheckDigestAsync"/>, <see cref="ExtractSha256"/>) compare a plaintext hash and
/// provide integrity only: anyone who can replace the chart can also rewrite the hash. Never
/// treat digest-only agreement as proof of origin.
/// </para>
/// <para>
/// <b>Trust model.</b> Verification fails closed unless the OpenPGP signature is valid, the
/// signer matches one of the supplied trusted keys, and the signed SHA-256 equals the archive
/// digest. There is no implicit keyring and no TOFU: callers pass trusted keys explicitly.
/// </para>
/// <para>
/// <b>File format.</b> Generated files match Helm's provenance layout (clearsigned chart
/// metadata YAML, a <c>files:</c> digest map, and a real OpenPGP signature) so they interoperate
/// with <c>helm verify</c> and <c>helm package --sign</c>. The file is UTF-8 without a BOM;
/// cleartext lines starting with <c>-</c> are dash-escaped in the armor while the signature
/// covers the unescaped text, and the signature block may carry the RFC 4880 CRC-24 armor
/// checksum. Verification treats the checksum as advisory and accepts it when missing,
/// malformed, or mismatched.
/// </para>
/// <para>
/// <b>Legacy files.</b> Earlier HelmSharp builds wrote a pseudo-signature whose PGP SIGNATURE
/// block carried a base64 SHA-512 digest instead of an OpenPGP signature. Those files fail
/// authenticity verification (the block is not a valid signature) and must be re-signed with
/// <see cref="SignAsync"/>. They remain readable through the digest-only helpers.
/// </para>
/// </remarks>
public static class HelmProvenance
{
    private static readonly HashSet<string> RegisteredArmorHashNames = new(StringComparer.Ordinal)
    {
        "MD5", "SHA1", "RIPEMD160", "SHA256", "SHA384", "SHA512", "SHA224", "SHA3-256", "SHA3-512",
    };

    private const string SignedMessageHeader = "-----BEGIN PGP SIGNED MESSAGE-----";
    private const string SignatureHeader = "-----BEGIN PGP SIGNATURE-----";
    private const string SignatureFooter = "-----END PGP SIGNATURE-----";
    private const int MaxProvenanceFileBytes = 16 * 1024 * 1024;

    // Provenance files are UTF-8 without a BOM, matching Helm's Go writer. Reading or
    // hashing as ASCII would replace non-ASCII metadata bytes with '?' and invalidate
    // the signature over otherwise untouched chart text.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Signs a chart archive with a real OpenPGP signature and writes the provenance file
    /// next to the archive as <c>{archive}.prov</c>, matching Helm's clearsigned layout.
    /// </summary>
    /// <param name="chartTgzPath">Chart archive to sign.</param>
    /// <param name="signingKey">Private-key material used for signing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Path of the generated <c>.prov</c> file.</returns>
    /// <exception cref="FileNotFoundException">The chart archive does not exist.</exception>
    /// <exception cref="InvalidDataException">The archive has no readable <c>Chart.yaml</c>.</exception>
    /// <exception cref="PgpException">The signing key cannot be unlocked or signing fails.</exception>
    /// <exception cref="NotSupportedException">The signing key is not version 4, which the current signature generator cannot encode.</exception>
    public static async Task<string> SignAsync(
        string chartTgzPath,
        HelmProvenanceSigningKey signingKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chartTgzPath);
        ArgumentNullException.ThrowIfNull(signingKey);

        if (!File.Exists(chartTgzPath))
            throw new FileNotFoundException($"Chart archive not found: {chartTgzPath}", chartTgzPath);

        // --- 1. Hash the archive and read chart metadata under the shared archive budget ---
        var archiveLength = new FileInfo(chartTgzPath).Length;
        var archiveLimits = HelmChartArchiveLimits.Default;
        if (archiveLength > archiveLimits.MaxCompressedBytes)
            throw new ChartArchiveLimitExceededException(
                ChartArchiveLimitKind.CompressedBytes,
                archiveLimits.MaxCompressedBytes,
                archiveLength);

        await using var archive = new FileStream(
            chartTgzPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(archive, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        archive.Position = 0;
        var archiveName = Path.GetFileName(chartTgzPath);
        var metadata = await ReadArchiveMetadataAsync(archive, archiveLength, cancellationToken).ConfigureAwait(false);

        // --- 2. Build the Helm-shaped signed body ---
        var body = BuildSignedBody(metadata, archiveName, sha256);

        // --- 3. Clearsign the body with SHA-512 (Helm's provenance hash) ---
        var provContent = ClearSign(body, signingKey);

        var provPath = chartTgzPath + ".prov";
        await File.WriteAllTextAsync(provPath, provContent, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        return provPath;
    }

    /// <summary>
    /// Verifies a chart archive against its provenance file with full authenticity checks:
    /// OpenPGP signature validity, trusted-signer identity, and signed chart digest.
    /// Fails closed on tampered chart bytes, tampered signed metadata, malformed armor,
    /// unknown or untrusted keys, and invalid signatures.
    /// </summary>
    /// <param name="chartTgzPath">Chart archive to verify.</param>
    /// <param name="trustedKeys">OpenPGP public keys authorized to sign provenance for this chart. Must not be empty.</param>
    /// <param name="provPath">Provenance file path; defaults to <c>{archive}.prov</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Verification result describing each check and the signer identity.</returns>
    /// <exception cref="FileNotFoundException">The chart archive does not exist.</exception>
    /// <exception cref="ArgumentException"><paramref name="trustedKeys"/> is null or empty.</exception>
    public static async Task<HelmProvenanceVerificationResult> VerifyAsync(
        string chartTgzPath,
        IEnumerable<HelmProvenanceTrustedKey> trustedKeys,
        string? provPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chartTgzPath);
        ArgumentNullException.ThrowIfNull(trustedKeys);

        var trustedList = trustedKeys.Where(k => k is not null).ToList();
        if (trustedList.Count == 0)
            throw new ArgumentException("At least one trusted key is required for authenticity verification.", nameof(trustedKeys));

        if (!File.Exists(chartTgzPath))
            throw new FileNotFoundException($"Chart archive not found: {chartTgzPath}", chartTgzPath);

        provPath ??= chartTgzPath + ".prov";
        if (!File.Exists(provPath))
        {
            return Fail(null, null, null, null, actualSha256: null, "Provenance file not found: " + provPath);
        }

        // --- 1. Compute the archive digest and read the provenance file ---
        var archiveName = Path.GetFileName(chartTgzPath);
        await using var chartStream = new FileStream(
            chartTgzPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var chartHash = await SHA256.HashDataAsync(chartStream, cancellationToken).ConfigureAwait(false);
        var actualSha256 = Convert.ToHexString(chartHash).ToLowerInvariant();
        var provenanceBytes = await ReadBoundedFileAsync(
            provPath,
            MaxProvenanceFileBytes,
            cancellationToken).ConfigureAwait(false);
        if (provenanceBytes is null)
        {
            return Fail(null, null, null, expectedSha256: null, actualSha256,
                $"Provenance file exceeds the {MaxProvenanceFileBytes / (1024 * 1024)} MiB verification limit.");
        }

        if (!TryDecodeUtf8WithoutBom(provenanceBytes, out var provContent))
        {
            return Fail(null, null, null, expectedSha256: null, actualSha256,
                "Provenance file must use valid UTF-8 without a byte-order mark.");
        }

        // --- 2. Reject legacy pseudo-signature files before any crypto ---
        if (IsLegacyPseudoSignature(provContent))
        {
            return Fail(null, null, null, expectedSha256: ExtractSha256(provContent, archiveName), actualSha256,
                "Legacy HelmSharp pseudo-signature provenance cannot establish authenticity; re-sign with SignAsync.");
        }

        // --- 3. Parse the clearsigned message ---
        if (!TryParseClearSign(provContent, out var signedBody, out var signaturePacket))
        {
            return Fail(null, null, null, expectedSha256: ExtractSha256(provContent, archiveName), actualSha256,
                "Malformed OpenPGP clearsigned provenance armor.");
        }

        // --- 4. Verify the OpenPGP signature over the canonicalized signed body ---
        IReadOnlyList<PgpSignature> parsedSignatures;
        try
        {
            parsedSignatures = ParseSignatures(signaturePacket);
        }
        catch (PgpException)
        {
            return Fail(null, null, null, ExtractSha256(provContent, archiveName), actualSha256,
                "OpenPGP provenance signature block is malformed.");
        }
        catch (IOException)
        {
            return Fail(null, null, null, ExtractSha256(provContent, archiveName), actualSha256,
                "OpenPGP provenance signature block is malformed.");
        }

        bool signatureValid = false;
        bool weakHashSignature = false;
        bool understrengthHashSignature = false;
        bool invalidSignatureType = false;
        bool missingCreationTime = false;
        bool unsupportedCriticalSubpacket = false;
        bool trustedKeyMatchedExpiredSignature = false;
        bool trustedKeyMatchedButUnusable = false;
        string? signerFingerprint = null;
        string? signerKeyId = null;
        string? signerUserId = null;
        string? expiredFingerprint = null;
        string? expiredKeyId = null;
        string? expiredUserId = null;
        string? unusableFingerprint = null;
        string? unusableKeyId = null;
        string? unusableUserId = null;

        foreach (var parsedSignature in parsedSignatures)
        {
            // RFC 9580 permits multiple signatures in the armor. A malformed,
            // unsupported, or untrusted packet must not hide a later trusted one.
            if (parsedSignature.SignatureType != PgpSignature.CanonicalTextDocument)
            {
                invalidSignatureType = true;
                continue;
            }

            if (!HasRequiredSignatureCreationTime(parsedSignature))
            {
                missingCreationTime = true;
                continue;
            }

            if (HasUnsupportedCriticalSubpackets(
                    parsedSignature,
                    SignatureSubpacketTag.CreationTime,
                    SignatureSubpacketTag.ExpireTime,
                    SignatureSubpacketTag.IssuerFingerprint))
            {
                unsupportedCriticalSubpacket = true;
                continue;
            }

            foreach (var trusted in trustedList)
            {
                try
                {
                    if (!SignatureVersionMatchesKey(
                            parsedSignature.Version,
                            trusted.PublicKey.Version,
                            trusted.PublicKey.Algorithm))
                        continue;

                    if (!HelmProvenanceTrustedKey.IssuerFingerprintMatches(parsedSignature, trusted.PublicKey))
                        continue;

                    if (IsWeakHashAlgorithm(parsedSignature.HashAlgorithm))
                    {
                        weakHashSignature = true;
                        continue;
                    }

                    if (!HasSufficientSignatureHash(parsedSignature.HashAlgorithm, trusted.PublicKey))
                    {
                        understrengthHashSignature = true;
                        continue;
                    }

                    parsedSignature.InitVerify(trusted.PublicKey);
                    parsedSignature.Update(CanonicalizeForHash(signedBody));
                    if (parsedSignature.Verify())
                    {
                        if (IsSignatureExpired(parsedSignature, DateTime.UtcNow))
                        {
                            trustedKeyMatchedExpiredSignature = true;
                            expiredFingerprint ??= trusted.Fingerprint;
                            expiredKeyId ??= trusted.KeyId;
                            expiredUserId ??= trusted.UserId;
                            continue;
                        }

                        // A mathematical signature is not enough: OpenPGP policy must also
                        // authorize this key to sign. Fail closed on revoked, expired, and
                        // non-signing keys (e.g. encryption-only subkeys).
                        if (!trusted.IsUsableSigningKey)
                        {
                            trustedKeyMatchedButUnusable = true;
                            unusableFingerprint ??= trusted.Fingerprint;
                            unusableKeyId ??= trusted.KeyId;
                            unusableUserId ??= trusted.UserId;
                            continue;
                        }

                        signatureValid = true;
                        signerFingerprint = trusted.Fingerprint;
                        signerKeyId = trusted.KeyId;
                        signerUserId = trusted.UserId;
                        break;
                    }
                }
                catch (PgpException)
                {
                    // Wrong key or malformed signature for this candidate; try the next trusted key.
                }
                catch (IOException)
                {
                    // Same: not this key's signature.
                }
            }

            if (signatureValid)
                break;
        }

        if (!signatureValid)
        {
            if (weakHashSignature)
            {
                return Fail(null, null, null,
                    ExtractSha256(provContent, archiveName), actualSha256,
                    "OpenPGP provenance signatures using MD5, SHA-1, or RIPEMD-160 are not accepted.");
            }

            if (understrengthHashSignature)
            {
                return Fail(null, null, null,
                    ExtractSha256(provContent, archiveName), actualSha256,
                    "OpenPGP provenance signature hash is too weak for the signing key algorithm and curve.");
            }

            if (trustedKeyMatchedExpiredSignature)
            {
                var expiredExpectedSha256 = ExtractSha256(signedBody, archiveName);
                var expiredDigestMatches = expiredExpectedSha256 is not null &&
                    string.Equals(expiredExpectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase);
                return new HelmProvenanceVerificationResult(
                    signatureValid: true,
                    signerTrusted: true,
                    expiredDigestMatches,
                    expiredFingerprint,
                    expiredKeyId,
                    expiredUserId,
                    expiredExpectedSha256,
                    actualSha256,
                    "Provenance signature has expired.");
            }

            if (trustedKeyMatchedButUnusable)
            {
                var unusableExpectedSha256 = ExtractSha256(signedBody, archiveName);
                var unusableDigestMatches = unusableExpectedSha256 is not null &&
                    string.Equals(unusableExpectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase);
                return new HelmProvenanceVerificationResult(
                    signatureValid: true,
                    signerTrusted: true,
                    unusableDigestMatches,
                    unusableFingerprint,
                    unusableKeyId,
                    unusableUserId,
                    unusableExpectedSha256,
                    actualSha256,
                    "Signature is cryptographically valid but the signing key is revoked, expired, or not authorized to sign.");
            }

            if (missingCreationTime)
            {
                return Fail(null, null, null, ExtractSha256(provContent, archiveName), actualSha256,
                    "OpenPGP provenance signature is missing its hashed creation time.");
            }

            if (unsupportedCriticalSubpacket)
            {
                return Fail(null, null, null, ExtractSha256(provContent, archiveName), actualSha256,
                    "OpenPGP provenance signature contains an unsupported critical hashed subpacket.");
            }

            if (invalidSignatureType)
            {
                return Fail(null, null, null, ExtractSha256(provContent, archiveName), actualSha256,
                    "OpenPGP clear-signed provenance must use a canonical-text document signature.");
            }

            // Distinguish "valid signature by an untrusted key" from "no valid signature at all".
            foreach (var parsedSignature in parsedSignatures)
            {
                using var signatureStream = new MemoryStream();
                parsedSignature.Encode(signatureStream);
                if (TryVerifyAgainstEmbeddedIssuer(signedBody, signatureStream.ToArray(), out var untrustedFp, out var untrustedId, out var untrustedUid))
                {
                    return Fail(untrustedFp, untrustedId, untrustedUid, ExtractSha256(provContent, archiveName), actualSha256,
                        "Signature is cryptographically valid but the signing key is not in the trusted key set.");
                }
            }

            return Fail(null, null, null, ExtractSha256(provContent, archiveName), actualSha256,
                "OpenPGP signature is invalid or does not match any trusted key.");
        }

        // --- 5. Bind the chart digest to the signed metadata ---
        var expectedSha256 = ExtractSha256(signedBody, archiveName);
        if (expectedSha256 is null)
        {
            return new HelmProvenanceVerificationResult(
                signatureValid: true,
                signerTrusted: true,
                digestMatches: false,
                signerFingerprint,
                signerKeyId,
                signerUserId,
                expectedSha256: null,
                actualSha256,
                "Signed provenance body does not contain a chart digest in the files section.");
        }

        var digestMatches = string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase);
        if (!digestMatches)
        {
            // Signature is valid and the signer is trusted; only the chart bytes diverged.
            return new HelmProvenanceVerificationResult(
                signatureValid: true,
                signerTrusted: true,
                digestMatches: false,
                signerFingerprint,
                signerKeyId,
                signerUserId,
                expectedSha256,
                actualSha256,
                "Chart archive digest does not match the signed digest; the archive has been modified.");
        }

        return new HelmProvenanceVerificationResult(
            signatureValid: true,
            signerTrusted: true,
            digestMatches: true,
            signerFingerprint,
            signerKeyId,
            signerUserId,
            expectedSha256,
            actualSha256,
            failureReason: null);
    }

    /// <summary>Whether a document signature has its required authenticated timestamp.</summary>
    private static bool HasRequiredSignatureCreationTime(PgpSignature signature)
    {
        if (signature.Version == 3)
            return true;

        if (signature.Version != 4)
            return false;

        var hashed = signature.GetHashedSubPackets();
        return hashed is not null && hashed.HasSubpacket(SignatureSubpacketTag.CreationTime);
    }

    /// <summary>Whether the signature version is valid for the signing key's version and algorithm.</summary>
    internal static bool SignatureVersionMatchesKey(
        int signatureVersion,
        int keyVersion,
        PublicKeyAlgorithmTag keyAlgorithm) =>
        (keyVersion, signatureVersion) switch
        {
            (3, 3) => !IsEllipticCurveSignatureAlgorithm(keyAlgorithm),
            // RFC 9580 retains V3 signatures from V4 keys for historical verification,
            // but explicitly prohibits V3 signatures with ECDSA and EdDSA algorithms.
            (4, 3) => !IsEllipticCurveSignatureAlgorithm(keyAlgorithm),
            (4, 4) => true,
            _ => false,
        };

    private static bool IsEllipticCurveSignatureAlgorithm(PublicKeyAlgorithmTag algorithm) =>
        algorithm is PublicKeyAlgorithmTag.ECDsa
            or PublicKeyAlgorithmTag.EdDsa_Legacy
            or PublicKeyAlgorithmTag.Ed25519
            or PublicKeyAlgorithmTag.Ed448;

    /// <summary>
    /// Compares the chart archive SHA-256 against the digest recorded in a <c>.prov</c> file.
    /// This is a <b>non-authenticating integrity check only</b>: the digest is plaintext and
    /// forgeable by anyone who can replace the chart. It does not verify any signature and
    /// must not be presented as proof of origin — use <see cref="VerifyAsync"/> for authenticity.
    /// </summary>
    /// <param name="chartTgzPath">Chart archive to check.</param>
    /// <param name="provPath">Provenance file path; defaults to <c>{archive}.prov</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the recorded digest matches the archive bytes.</returns>
    public static async Task<bool> CheckDigestAsync(
        string chartTgzPath,
        string? provPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chartTgzPath);
        provPath ??= chartTgzPath + ".prov";
        if (!File.Exists(provPath))
            return false;

        var chartBytes = await File.ReadAllBytesAsync(chartTgzPath, cancellationToken).ConfigureAwait(false);
        var actualHash = Convert.ToHexString(SHA256.HashData(chartBytes)).ToLowerInvariant();
        var provContent = await File.ReadAllTextAsync(provPath, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        var expectedHash = ExtractSha256(provContent, Path.GetFileName(chartTgzPath));

        return expectedHash is not null &&
               string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the chart archive SHA-256 from a <c>.prov</c> file body: the digest in the
    /// <c>files:</c> section (selected by archive filename), or the legacy top-level
    /// <c>sha256:</c> key. Chart metadata scalars that merely look like digests (for
    /// example an annotation value <c>sha256:&lt;64 hex&gt;</c>) are never returned,
    /// because only the <c>files:</c> map binds an archive name to its digest.
    /// Non-authenticating: the value is plaintext and not protected against tampering.
    /// </summary>
    /// <param name="provContent">Full text of a provenance file.</param>
    /// <param name="archiveName">
    /// Archive filename (for example <c>mychart-1.0.0.tgz</c>) selecting the entry in a
    /// multi-file <c>files:</c> map. When null, a single-entry map is used and multi-entry
    /// maps are treated as ambiguous rather than guessing the wrong digest.
    /// </param>
    /// <returns>Lowercase hex digest, or null when no digest is present for the archive.</returns>
    public static string? ExtractSha256(string provContent, string? archiveName = null)
    {
        if (string.IsNullOrEmpty(provContent))
            return null;

        // Prefer the Helm-style files section; fall back to the legacy top-level key.
        var fromFiles = ExtractSha256FromFilesSection(provContent, archiveName, out var sawFilesSection);
        if (fromFiles is not null)
            return fromFiles;

        // A files section without a digest for this archive must not fall through to a
        // legacy or metadata scalar: that would reintroduce the wrong-digest confusion.
        return sawFilesSection ? null : ExtractLegacyTopLevelSha256(provContent);
    }

    /// <summary>
    /// Extracts chart metadata key/value pairs from a <c>.prov</c> file body: the chart
    /// metadata YAML fields plus the archive digest from the <c>files:</c> section.
    /// Armor headers and the signature block are excluded. Keys are compared case-insensitively.
    /// Non-authenticating: values are plaintext and forgeable alongside the chart.
    /// </summary>
    /// <param name="provContent">Full text of a provenance file.</param>
    /// <returns>Metadata pairs from the signed-message body.</returns>
    public static Dictionary<string, string> ExtractMetadata(string provContent)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(provContent))
            return result;

        var body = ExtractSignedBodyText(provContent);
        if (body is null)
            return result;

        var sawDocumentEnd = false;
        var inFilesSection = false;
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            // Helm starts its provenance digest map only after the metadata document ends.
            // Requiring the marker and section header at column zero keeps block-scalar
            // metadata that mentions an indented "files:" from entering digest-map mode.
            if (line == "...")
            {
                sawDocumentEnd = true;
                continue;
            }

            if (sawDocumentEnd && line == "files:")
            {
                inFilesSection = true;
                continue;
            }

            if (inFilesSection)
            {
                // "  archive-name.tgz: sha256:<hex>" -> record as files entry + sha256 key.
                if (TryParseFilesEntry(trimmed, out var fileKey, out var fileValue))
                {
                    result["files." + fileKey] = fileValue;
                    if (fileValue.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        result["sha256"] = fileValue["sha256:".Length..].Trim();
                }

                continue;
            }

            var sep = trimmed.IndexOf(':');
            if (sep > 0)
            {
                var key = trimmed[..sep].Trim();
                var value = trimmed[(sep + 1)..].Trim();
                // Strip simple YAML quoting so callers see the scalar value.
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                    value = value[1..^1];
                result[key] = value;
            }
        }

        return result;
    }

    // --- Signing internals ---

    private static string BuildSignedBody(
        IReadOnlyDictionary<string, object?> metadata,
        string archiveName,
        string sha256)
    {
        // Helm's provenance body is the chart metadata as YAML, a document-end marker,
        // then a files map binding the archive name to its SHA-256.
        var metadataYaml = HelmYaml.Serialize(metadata).TrimEnd('\n', '\r');
        var sb = new StringBuilder();
        sb.Append(metadataYaml);
        sb.Append("\n\n...\n");
        sb.Append("files:\n");
        sb.Append("  ").Append(JsonSerializer.Serialize(archiveName)).Append(": sha256:").Append(sha256).Append('\n');
        return sb.ToString();
    }

    private static string ClearSign(string body, HelmProvenanceSigningKey signingKey)
    {
        var secretKey = signingKey.GetSecretKey();
        if (secretKey.PublicKey.Version != 4)
            throw new NotSupportedException("Only OpenPGP version 4 signing keys are supported by the current provenance signature generator.");

        var privateKey = secretKey.ExtractPrivateKeyUtf8(signingKey.Passphrase)
                         ?? throw new PgpException("Signing key could not be unlocked; check the passphrase.");

        // --- Hash input: trailing-whitespace-stripped lines joined with CRLF (RFC 4880 §7.1) ---
        var canonical = CanonicalizeForHash(body);

        var signatureGenerator = new PgpSignatureGenerator(
            secretKey.PublicKey.Algorithm,
            HashAlgorithmTag.Sha512);
        signatureGenerator.InitSign(PgpSignature.CanonicalTextDocument, privateKey);

        var creationTime = new PgpSignatureSubpacketGenerator();
        creationTime.SetSignatureCreationTime(false, DateTime.UtcNow);
        signatureGenerator.SetHashedSubpackets(creationTime.Generate());
        signatureGenerator.Update(canonical, 0, canonical.Length);
        var signature = signatureGenerator.Generate();

        // --- Assemble clearsigned armor matching Helm's output shape ---
        var sb = new StringBuilder();
        sb.Append(SignedMessageHeader).Append('\n');
        sb.Append("Hash: SHA512\n");
        sb.Append('\n');
        // Dash-escape the written cleartext (RFC 4880 §7.1); the hash above covers the
        // unescaped text, so YAML sequence items like "- name: Bob" survive as "- - name: Bob".
        var escapedBody = EscapeDashLines(body);
        sb.Append(escapedBody);
        // The line ending that terminates the signed text sits before the signature block.
        if (!escapedBody.EndsWith("\n", StringComparison.Ordinal))
            sb.Append('\n');
        sb.Append(SignatureHeader).Append('\n');
        sb.Append('\n');

        using (var sigStream = new MemoryStream())
        {
            signature.Encode(sigStream);
            var signatureBytes = sigStream.ToArray();
            sb.Append(Base64Armor(signatureBytes));
            // RFC 4880 §6.1 armor checksum; GnuPG-standard armor always carries it.
            sb.Append('=').Append(Convert.ToBase64String(ComputeCrc24(signatureBytes))).Append('\n');
        }

        sb.Append(SignatureFooter).Append('\n');
        return sb.ToString();
    }

    private static string Base64Armor(byte[] data)
    {
        var base64 = Convert.ToBase64String(data);
        var sb = new StringBuilder();
        for (var i = 0; i < base64.Length; i += 64)
        {
            var len = Math.Min(64, base64.Length - i);
            sb.Append(base64.AsSpan(i, len)).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Dash-escapes cleartext lines for armor output: every line starting with <c>-</c>
    /// gains a <c>- </c> prefix so YAML sequence items cannot be mistaken for armor
    /// framing (RFC 4880 §7.1). The signature covers the unescaped text.
    /// </summary>
    private static string EscapeDashLines(string body) => TransformDashLines(body, escape: true);

    /// <summary>
    /// Reverses <see cref="EscapeDashLines"/> on cleartext read from armor: strips a leading
    /// <c>- </c> so the recovered text is exactly what the signer hashed (RFC 4880 §7.1).
    /// </summary>
    private static string UnescapeDashLines(string body) => TransformDashLines(body, escape: false);

    private static string TransformDashLines(string body, bool escape)
    {
        if (body.Length == 0)
            return body;

        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sb = new StringBuilder(body.Length + 16);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (escape)
            {
                if (line.StartsWith('-'))
                    sb.Append("- ");
                sb.Append(line);
            }
            else
            {
                // Only "- " (dash + space) is the escape prefix; "-x" is ordinary text.
                sb.Append(line.Length >= 2 && line[0] == '-' && line[1] == ' ' ? line[2..] : line);
            }

            if (i < lines.Length - 1)
                sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Computes the OpenPGP armor CRC-24 (RFC 4880 §6.1): init 0xB704CE, poly 0x1864CFB.
    /// </summary>
    private static byte[] ComputeCrc24(byte[] data)
    {
        uint crc = 0xB704CE;
        foreach (var b in data)
        {
            crc ^= (uint)b << 16;
            for (var i = 0; i < 8; i++)
            {
                crc <<= 1;
                if ((crc & 0x1000000) != 0)
                    crc ^= 0x1864CFB;
            }
        }

        return [(byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
    }

    // --- Verification internals ---

    private static byte[] CanonicalizeForHash(string body)
    {
        // RFC 4880 §7.1: hash the signed text with canonical CRLF line endings and
        // trailing whitespace stripped. The line ending that terminates the signed text
        // before BEGIN PGP SIGNATURE is not part of the hash input — strip exactly one.
        // The body is dash-unescaped text; encode as UTF-8 so non-ASCII metadata keeps
        // its original bytes instead of collapsing to ASCII '?'.
        var text = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.EndsWith("\n", StringComparison.Ordinal))
            text = text[..^1];

        var lines = text.Split('\n');
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            sb.Append(lines[i].TrimEnd(' ', '\t'));
            if (i < lines.Length - 1)
                sb.Append("\r\n");
        }

        return Utf8NoBom.GetBytes(sb.ToString());
    }

    private static bool TryDecodeUtf8WithoutBom(byte[] bytes, out string text)
    {
        text = string.Empty;
        var content = bytes.AsSpan();
        if (content.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ||
            content.StartsWith(new byte[] { 0xFF, 0xFE }) ||
            content.StartsWith(new byte[] { 0xFE, 0xFF }) ||
            content.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
            return false;

        try
        {
            text = Utf8NoBom.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static async Task<byte[]?> ReadBoundedFileAsync(
        string path,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maxBytes)
            return null;

        using var content = new MemoryStream((int)stream.Length);
        var buffer = new byte[81920];
        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
                return content.ToArray();
            if (content.Length + bytesRead > maxBytes)
                return null;

            await content.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TryParseClearSign(string provContent, out string signedBody, out byte[] signaturePacket)
    {
        signedBody = string.Empty;
        signaturePacket = Array.Empty<byte>();

        var text = provContent.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!TryGetClearTextBodyStart(text, out var bodyStart))
            return false;

        var sigStart = FindLine(text, SignatureHeader, bodyStart);
        if (sigStart < 0)
            return false;

        signedBody = text[bodyStart..sigStart];

        var base64Start = text.IndexOf('\n', sigStart);
        if (base64Start < 0)
            return false;
        var footerStart = FindLine(text, SignatureFooter, base64Start);
        if (footerStart < 0)
            return false;

        var footerEnd = footerStart + SignatureFooter.Length;
        if (text[footerEnd..].Any(character => !char.IsWhiteSpace(character)))
            return false;

        var base64Region = text[base64Start..footerStart];
        var base64 = new StringBuilder();
        foreach (var line in base64Region.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.Contains(':'))
                continue;
            // RFC 9580 §6.1 makes the CRC-24 advisory: ignore it whether it is missing,
            // malformed, or mismatched. It is not part of the signed packet payload.
            if (trimmed[0] == '=')
                continue;

            base64.Append(trimmed);
        }

        try
        {
            signaturePacket = Convert.FromBase64String(base64.ToString());
        }
        catch (FormatException)
        {
            return false;
        }

        if (signaturePacket.Length == 0)
            return false;

        // Helm/GPG emit dash-escaped cleartext; the signature covers the unescaped text.
        signedBody = UnescapeDashLines(signedBody);
        return true;
    }

    private static bool TryGetClearTextBodyStart(string text, out int bodyStart)
    {
        // Only the armor header is outside the signature; accepting any leading text or
        // unknown fields would let an attacker attach unsigned claims to valid metadata.
        bodyStart = 0;
        var firstLineEnd = text.IndexOf('\n');
        if (firstLineEnd < 0 ||
            !text.AsSpan(0, firstLineEnd).SequenceEqual(SignedMessageHeader.AsSpan()))
        {
            return false;
        }

        var position = firstLineEnd + 1;
        while (position <= text.Length)
        {
            var lineEnd = text.IndexOf('\n', position);
            if (lineEnd < 0)
                return false;

            var line = text.AsSpan(position, lineEnd - position);
            if (line.IsEmpty)
            {
                // RFC 9580 §7.1 makes legacy Hash headers optional; the signature
                // packet itself identifies the hash algorithm used for verification.
                bodyStart = lineEnd + 1;
                return true;
            }

            const string hashHeader = "Hash: ";
            if (!line.StartsWith(hashHeader, StringComparison.OrdinalIgnoreCase))
                return false;

            var hashValue = line[hashHeader.Length..];
            if (hashValue.IsEmpty || char.IsWhiteSpace(hashValue[0]) || char.IsWhiteSpace(hashValue[^1]))
                return false;

            var algorithms = hashValue.ToString().Split(',');
            if (algorithms.Length == 0 || algorithms.Any(algorithm =>
                    !RegisteredArmorHashNames.Contains(algorithm.Trim())))
            {
                return false;
            }

            position = lineEnd + 1;
        }

        return false;
    }

    private static IReadOnlyList<PgpSignature> ParseSignatures(byte[] signaturePacket)
    {
        using var stream = new MemoryStream(signaturePacket, writable: false);
        var factory = new PgpObjectFactory(stream);
        var signatures = new List<PgpSignature>();
        foreach (var obj in factory.AllPgpObjects())
        {
            switch (obj)
            {
                case PgpSignatureList list:
                    signatures.AddRange(Enumerable.Range(0, list.Count).Select(index => list[index]));
                    break;
                case PgpMarker:
                case PgpPadding:
                    // RFC 9580 permits these ignorable packets between detached signatures.
                    break;
                default:
                    throw new PgpException("Signature block contains an unexpected OpenPGP packet.");
            }
        }

        if (signatures.Count == 0)
            throw new PgpException("Signature block does not contain an OpenPGP signature packet.");

        return signatures;
    }

    internal static bool HasUnsupportedCriticalSubpackets(
        PgpSignature signature,
        params SignatureSubpacketTag[] supportedTags)
    {
        var criticalTags = signature.GetHashedSubPackets()?.GetCriticalTags();
        if (criticalTags is null)
            return false;

        return criticalTags.Any(tag => !supportedTags.Contains(tag));
    }

    private static bool IsWeakHashAlgorithm(HashAlgorithmTag algorithm)
        => algorithm is HashAlgorithmTag.MD5 or HashAlgorithmTag.Sha1 or HashAlgorithmTag.RipeMD160;

    internal static bool HasSufficientSignatureHash(HashAlgorithmTag hash, PgpPublicKey key)
        => GetHashOutputBits(hash) >= GetMinimumSignatureHashBits(key);

    private static int GetMinimumSignatureHashBits(PgpPublicKey key) => key.Algorithm switch
    {
        PublicKeyAlgorithmTag.RsaGeneral or PublicKeyAlgorithmTag.RsaSign => 256,
        // DSA needs a digest at least as large as q; retain the 256-bit floor used for
        // other modern signing policies, and fail closed when the DSA parameters are absent.
        PublicKeyAlgorithmTag.Dsa => key.PublicKeyPacket.Key is DsaPublicBcpgKey dsaKey
            ? Math.Max(256, dsaKey.Q.BitLength)
            : int.MaxValue,
        PublicKeyAlgorithmTag.ECDsa or PublicKeyAlgorithmTag.EdDsa_Legacy => key.BitStrength switch
        {
            <= 256 => 256,
            <= 384 => 384,
            _ => 512,
        },
        PublicKeyAlgorithmTag.Ed25519 => 256,
        PublicKeyAlgorithmTag.Ed448 => 512,
        _ => int.MaxValue,
    };

    private static int GetHashOutputBits(HashAlgorithmTag hash) => hash switch
    {
        HashAlgorithmTag.MD5 or HashAlgorithmTag.MD2 => 128,
        HashAlgorithmTag.Sha1 or HashAlgorithmTag.RipeMD160 or HashAlgorithmTag.DoubleSha
            or HashAlgorithmTag.Haval5pass160 => 160,
        HashAlgorithmTag.Tiger192 => 192,
        HashAlgorithmTag.Sha224 => 224,
        HashAlgorithmTag.Sha256 or HashAlgorithmTag.Sha3_256 => 256,
        HashAlgorithmTag.Sha384 => 384,
        HashAlgorithmTag.Sha512 => 512,
        _ => 0,
    };

    /// <summary>
    /// Attempts verification against the key that actually produced the signature, using
    /// public keys embedded in the provenance file when present. Used only to distinguish
    /// "valid but untrusted" from "invalid" in failure diagnostics; never grants trust.
    /// </summary>
    private static bool TryVerifyAgainstEmbeddedIssuer(
        string signedBody,
        byte[] signaturePacket,
        out string? fingerprint,
        out string? keyId,
        out string? userId)
    {
        fingerprint = null;
        keyId = null;
        userId = null;
        try
        {
            var sig = ParseSignatures(signaturePacket).FirstOrDefault();
            if (sig is null)
                return false;
            // Without the issuer public key we cannot verify; report the key ID only.
            if (sig.KeyId != 0)
            {
                keyId = sig.KeyId.ToString("x16");
                return false;
            }

            return false;
        }
        catch (PgpException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool IsLegacyPseudoSignature(string provContent)
    {
        // Legacy files put Base64(SHA512(chartBytes)) — exactly one 88-char base64 line of
        // a 64-byte digest — inside the PGP SIGNATURE block with a "comment:" header.
        // A real OpenPGP signature packet never has that shape.
        var text = provContent.Replace("\r\n", "\n", StringComparison.Ordinal);
        var sigStart = FindLine(text, SignatureHeader);
        if (sigStart < 0)
            return false;

        var footerStart = text.IndexOf(SignatureFooter, sigStart, StringComparison.Ordinal);
        if (footerStart < 0)
            return false;

        var region = text[sigStart..footerStart];
        if (!region.Contains("comment:", StringComparison.OrdinalIgnoreCase))
            return false;

        var base64Lines = new List<string>();
        foreach (var line in region.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("-----", StringComparison.Ordinal) || trimmed.Contains(':'))
                continue;
            if (trimmed[0] == '=')
                continue; // armor checksum line, not signature payload
            base64Lines.Add(trimmed);
        }

        // Legacy payload is a single base64 blob of a 64-byte SHA-512 digest (88 chars + '=').
        return base64Lines.Count == 1
               && base64Lines[0].Length is >= 86 and <= 90
               && !region.Contains("wsC", StringComparison.Ordinal); // real armor rarely starts this way is not a reliable check; length+comment is
    }

    private static string? ExtractSignedBodyText(string provContent)
    {
        var text = provContent.Replace("\r\n", "\n", StringComparison.Ordinal);
        var msgStart = text.IndexOf(SignedMessageHeader, StringComparison.Ordinal);
        if (msgStart < 0)
            return null;

        var headerEnd = text.IndexOf("\n\n", msgStart, StringComparison.Ordinal);
        if (headerEnd < 0)
            return null;

        var sigStart = FindLine(text, SignatureHeader, headerEnd + 2);
        if (sigStart < 0)
            return UnescapeDashLines(text[(headerEnd + 2)..]);

        return UnescapeDashLines(text[(headerEnd + 2)..sigStart]);
    }

    private static int FindLine(string text, string expectedLine, int startIndex = 0)
    {
        var searchFrom = startIndex;
        while (searchFrom < text.Length)
        {
            var candidate = text.IndexOf(expectedLine, searchFrom, StringComparison.Ordinal);
            if (candidate < 0)
                return -1;

            var beginsLine = candidate == 0 || text[candidate - 1] == '\n';
            var lineEnd = candidate + expectedLine.Length;
            var endsLine = lineEnd == text.Length || text[lineEnd] == '\n';
            if (beginsLine && endsLine)
                return candidate;

            searchFrom = candidate + 1;
        }

        return -1;
    }

    /// <summary>
    /// Reads the archive digest from the <c>files:</c> map of a provenance body. The map
    /// is located after the YAML document-end marker (<c>...</c>) that Helm writes between
    /// chart metadata and the files map, so an earlier metadata scalar such as an
    /// annotation value <c>sha256:&lt;64 hex&gt;</c> can never be mistaken for the digest.
    /// </summary>
    private static string? ExtractSha256FromFilesSection(string text, string? archiveName, out bool sawFilesSection)
    {
        sawFilesSection = false;

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = FindFilesSectionStart(lines);
        if (start < 0)
            return null;

        sawFilesSection = true;
        string? sole = null;
        var entryCount = 0;
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
                continue;

            // Entries are indented "  name.tgz: sha256:<hex>"; any non-indented line
            // (the signature block, a further section) ends the map.
            if (!char.IsWhiteSpace(line[0]))
                break;

            var trimmed = line.Trim();
            if (!TryParseFilesEntry(trimmed, out var key, out var value))
                continue;

            if (!value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                continue;

            // Helm files values are "sha256:" followed by exactly 64 hex characters.
            var digest = value["sha256:".Length..].Trim();
            if (digest.Length != 64)
                continue;

            entryCount++;
            sole = digest.ToLowerInvariant();
            if (archiveName is not null && string.Equals(key, archiveName, StringComparison.Ordinal))
                return sole;
        }

        // Without a filename a single-entry map is unambiguous; a multi-entry map is
        // ambiguous and the caller must name the archive instead of guessing.
        return archiveName is null && entryCount == 1 ? sole : null;
    }

    private static bool TryParseFilesEntry(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        if (line.StartsWith('"'))
        {
            var escaped = false;
            for (var index = 1; index < line.Length; index++)
            {
                var current = line[index];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (current != '"')
                    continue;

                if (index + 1 >= line.Length || line[index + 1] != ':')
                    return false;

                try
                {
                    key = JsonSerializer.Deserialize<string>(line[..(index + 1)]) ?? string.Empty;
                }
                catch (JsonException)
                {
                    return false;
                }

                value = line[(index + 2)..].Trim();
                return key.Length > 0;
            }

            return false;
        }

        var separator = line.IndexOf(": ", StringComparison.Ordinal);
        if (separator <= 0)
            return false;

        key = line[..separator].Trim();
        value = line[(separator + 2)..].Trim();
        return key.Length > 0;
    }

    /// <summary>
    /// Index of the first entry line under the top-level <c>files:</c> map, or -1 when the
    /// body has no such map. Anchors on the YAML document-end marker (<c>...</c>) that
    /// Helm writes before <c>files:</c> so chart metadata keys cannot impersonate the map.
    /// </summary>
    private static int FindFilesSectionStart(string[] lines)
    {
        var searchFrom = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i] == "...")
            {
                searchFrom = i + 1;
                break;
            }
        }

        for (var i = searchFrom; i < lines.Length; i++)
        {
            if (lines[i] == "files:")
                return i + 1;
        }

        return -1;
    }

    /// <summary>
    /// Legacy provenance recorded a single unindented <c>sha256: &lt;hex&gt;</c> key with
    /// no <c>files:</c> map. Requiring column 0 keeps nested metadata scalars out of the
    /// match.
    /// </summary>
    private static string? ExtractLegacyTopLevelSha256(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
                continue;

            var trimmed = line.Trim();
            if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                return trimmed["sha256:".Length..].Trim();
        }

        return null;
    }

    private static HelmProvenanceVerificationResult Fail(
        string? signerFingerprint,
        string? signerKeyId,
        string? signerUserId,
        string? expectedSha256,
        string? actualSha256,
        string failureReason)
        => new(
            signatureValid: false,
            signerTrusted: signerFingerprint is not null,
            digestMatches: false,
            signerFingerprint,
            signerKeyId,
            signerUserId,
            expectedSha256,
            actualSha256,
            failureReason);

    private static bool IsSignatureExpired(PgpSignature signature, DateTime at)
    {
        var validitySeconds = signature.GetHashedSubPackets()?.GetSignatureExpirationTime() ?? 0;
        if (validitySeconds <= 0)
            return false;

        try
        {
            return at >= signature.CreationTime.AddSeconds(validitySeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    // --- Metadata extraction from the chart archive ---

    private static async Task<Dictionary<string, object?>> ReadArchiveMetadataAsync(
        Stream archive,
        long compressedBytes,
        CancellationToken cancellationToken)
    {
        var budget = new ChartArchiveBudget(HelmChartArchiveLimits.Default);
        var scope = budget.BeginArchive(compressedBytes);
        await using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
        await using var metered = scope.MeterDecompressedStream(gzip);
        await using var guarded = scope.GuardTarStream(metered);
        await using var reader = new TarReader(guarded, leaveOpen: true);
        Dictionary<string, object?>? chartMetadata = null;
        var rootChartMetadataCount = 0;
        string? nestedChartRoot = null;
        var archiveEntryNames = new List<string>();
        while ((await reader.GetNextEntryAsync(copyData: false, cancellationToken: cancellationToken).ConfigureAwait(false)) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Directory records are structural; common tar writers add a trailing slash.
            if (entry.EntryType is TarEntryType.Directory)
                continue;

            var name = NormalizeArchiveEntryName(entry.Name);
            if (entry.DataStream is null)
                continue;
            archiveEntryNames.Add(name);

            var chartFileName = name.Equals("Chart.yaml", StringComparison.OrdinalIgnoreCase);
            var rootChartFileName = name.EndsWith("/Chart.yaml", StringComparison.OrdinalIgnoreCase) &&
                name.IndexOf('/') == name.LastIndexOf('/');
            if (!chartFileName && !rootChartFileName)
            {
                // Signing still validates every entry against the same per-entry and
                // total extraction budgets used when a chart is loaded.
                _ = await scope.ReadEntryAsync(entry.DataStream, cancellationToken).ConfigureAwait(false);
                continue;
            }

            rootChartMetadataCount++;
            if (rootChartMetadataCount > 1)
                throw new InvalidDataException("Chart archive contains multiple root Chart.yaml entries.");

            if (rootChartFileName)
                nestedChartRoot = name[..name.IndexOf('/', StringComparison.Ordinal)];

            var chartYaml = await scope.ReadEntryAsync(entry.DataStream, cancellationToken).ConfigureAwait(false);
            var yaml = Encoding.UTF8.GetString(chartYaml);
            chartMetadata = HelmYaml.DeserializeDictionary(yaml);
        }

        if (nestedChartRoot is not null && archiveEntryNames.Any(name =>
                !name.StartsWith(nestedChartRoot + "/", StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Chart archive entries do not share the root directory containing Chart.yaml.");
        }

        if (chartMetadata is not null)
            return chartMetadata;

        throw new InvalidDataException("Chart archive does not contain Chart.yaml.");
    }

    private static string NormalizeArchiveEntryName(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
            throw new InvalidDataException("Chart archive contains an entry with an empty name.");

        var normalized = entryName.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' &&
             (normalized.Length == 2 || normalized[2] == '/')))
        {
            throw new InvalidDataException($"Chart archive entry '{entryName}' uses an unsafe absolute path.");
        }

        if (normalized.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException($"Chart archive entry '{entryName}' uses an unsafe relative path.");

        return normalized;
    }
}
