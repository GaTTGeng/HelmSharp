using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using HelmSharp.Chart;

namespace HelmSharp.Action;

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
/// covers the unescaped text, and the signature block carries the RFC 4880 CRC-24 armor
/// checksum. Verification accepts Helm's checksum-less armor as well.
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
    private const string SignedMessageHeader = "-----BEGIN PGP SIGNED MESSAGE-----";
    private const string SignatureHeader = "-----BEGIN PGP SIGNATURE-----";
    private const string SignatureFooter = "-----END PGP SIGNATURE-----";

    // Provenance files are UTF-8 without a BOM, matching Helm's Go writer. Reading or
    // hashing as ASCII would replace non-ASCII metadata bytes with '?' and invalidate
    // the signature over otherwise untouched chart text.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

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
    public static async Task<string> SignAsync(
        string chartTgzPath,
        HelmProvenanceSigningKey signingKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chartTgzPath);
        ArgumentNullException.ThrowIfNull(signingKey);

        if (!File.Exists(chartTgzPath))
            throw new FileNotFoundException($"Chart archive not found: {chartTgzPath}", chartTgzPath);

        // --- 1. Hash the archive and read chart metadata ---
        var chartBytes = await File.ReadAllBytesAsync(chartTgzPath, cancellationToken).ConfigureAwait(false);
        var sha256 = Convert.ToHexString(SHA256.HashData(chartBytes)).ToLowerInvariant();
        var archiveName = Path.GetFileName(chartTgzPath);
        var metadata = await ReadArchiveMetadataAsync(chartBytes, cancellationToken).ConfigureAwait(false);

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
        var chartBytes = await File.ReadAllBytesAsync(chartTgzPath, cancellationToken).ConfigureAwait(false);
        var actualSha256 = Convert.ToHexString(SHA256.HashData(chartBytes)).ToLowerInvariant();
        var provContent = await File.ReadAllTextAsync(provPath, Utf8NoBom, cancellationToken).ConfigureAwait(false);

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
        PgpSignature? parsedSignature = null;
        bool signatureValid = false;
        string? signerFingerprint = null;
        string? signerKeyId = null;
        string? signerUserId = null;

        foreach (var trusted in trustedList)
        {
            try
            {
                parsedSignature = ParseSignature(signaturePacket);
                parsedSignature.InitVerify(trusted.PublicKey);
                parsedSignature.Update(CanonicalizeForHash(signedBody));
                if (parsedSignature.Verify())
                {
                    // A mathematical signature is not enough: OpenPGP policy must also
                    // authorize this key to sign. Fail closed on revoked, expired, and
                    // non-signing keys (e.g. encryption-only subkeys).
                    if (!trusted.IsUsableSigningKey)
                    {
                        return Fail(trusted.Fingerprint, trusted.KeyId, trusted.UserId,
                            ExtractSha256(provContent, archiveName), actualSha256,
                            "Signature is cryptographically valid but the signing key is revoked, expired, or not authorized to sign.");
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

        if (!signatureValid)
        {
            // Distinguish "valid signature by an untrusted key" from "no valid signature at all".
            if (TryVerifyAgainstEmbeddedIssuer(signedBody, signaturePacket, out var untrustedFp, out var untrustedId, out var untrustedUid))
            {
                return Fail(untrustedFp, untrustedId, untrustedUid, ExtractSha256(provContent, archiveName), actualSha256,
                    "Signature is cryptographically valid but the signing key is not in the trusted key set.");
            }

            return Fail(null, null, null, ExtractSha256(provContent, archiveName), actualSha256,
                "OpenPGP signature is invalid or does not match any trusted key.");
        }

        // --- 5. Bind the chart digest to the signed metadata ---
        var expectedSha256 = ExtractSha256(signedBody, archiveName);
        if (expectedSha256 is null)
        {
            return Fail(signerFingerprint, signerKeyId, signerUserId, null, actualSha256,
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

        var inFilesSection = false;
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed == "...")
                continue;

            if (trimmed == "files:")
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
        var privateKey = secretKey.ExtractPrivateKeyUtf8(signingKey.Passphrase)
                         ?? throw new PgpException("Signing key could not be unlocked; check the passphrase.");

        // --- Hash input: trailing-whitespace-stripped lines joined with CRLF (RFC 4880 §7.1) ---
        var canonical = CanonicalizeForHash(body);

        var signatureGenerator = new PgpSignatureGenerator(
            secretKey.PublicKey.Algorithm,
            HashAlgorithmTag.Sha512);
        signatureGenerator.InitSign(PgpSignature.BinaryDocument, privateKey);

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

    /// <summary>
    /// Validates the optional <c>=</c>-prefixed armor checksum line against the decoded
    /// payload. Absence of a checksum is legal (Helm omits it); presence of a wrong one
    /// means the armor was corrupted in transit.
    /// </summary>
    private static bool ArmorChecksumMatches(byte[] payload, string checksumLine)
    {
        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(checksumLine[1..]);
        }
        catch (FormatException)
        {
            return false;
        }

        return expected.AsSpan().SequenceEqual(ComputeCrc24(payload));
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

    private static bool TryParseClearSign(string provContent, out string signedBody, out byte[] signaturePacket)
    {
        signedBody = string.Empty;
        signaturePacket = Array.Empty<byte>();

        var text = provContent.Replace("\r\n", "\n", StringComparison.Ordinal);
        var msgStart = text.IndexOf(SignedMessageHeader, StringComparison.Ordinal);
        if (msgStart < 0)
            return false;

        var headerEnd = text.IndexOf("\n\n", msgStart, StringComparison.Ordinal);
        if (headerEnd < 0)
            return false;

        var sigStart = FindLine(text, SignatureHeader, headerEnd + 2);
        if (sigStart < 0)
            return false;

        signedBody = text[(headerEnd + 2)..sigStart];

        var base64Start = text.IndexOf('\n', sigStart);
        if (base64Start < 0)
            return false;
        var footerStart = text.IndexOf(SignatureFooter, base64Start, StringComparison.Ordinal);
        if (footerStart < 0)
            return false;

        var base64Region = text[base64Start..footerStart];
        var base64 = new StringBuilder();
        string? checksumLine = null;
        foreach (var line in base64Region.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.Contains(':'))
                continue;
            // RFC 4880 §6.1: the armor checksum is a single '='-prefixed Base64(CRC-24)
            // line. Keep it out of the signature payload — mixing it in breaks decoding.
            if (trimmed[0] == '=')
            {
                checksumLine = trimmed;
                continue;
            }

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

        if (checksumLine is not null && !ArmorChecksumMatches(signaturePacket, checksumLine))
            return false;

        // Helm/GPG emit dash-escaped cleartext; the signature covers the unescaped text.
        signedBody = UnescapeDashLines(signedBody);
        return true;
    }

    private static PgpSignature ParseSignature(byte[] signaturePacket)
    {
        using var stream = new MemoryStream(signaturePacket, writable: false);
        var factory = new PgpObjectFactory(stream);
        var obj = factory.NextPgpObject();
        if (obj is PgpSignatureList list && list.Count > 0)
            return list[0];
        throw new PgpException("Signature block does not contain an OpenPGP signature packet.");
    }

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
            var sig = ParseSignature(signaturePacket);
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
            if (archiveName is not null && string.Equals(key, archiveName, StringComparison.OrdinalIgnoreCase))
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

    // --- Metadata extraction from the chart archive ---

    private static async Task<Dictionary<string, object?>> ReadArchiveMetadataAsync(
        byte[] archiveBytes,
        CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(archiveBytes, writable: false);
        await using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new System.Formats.Tar.TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.EntryType is System.Formats.Tar.TarEntryType.Directory || entry.DataStream is null)
                continue;

            var name = entry.Name.Replace('\\', '/');
            if (!name.EndsWith("/Chart.yaml", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("Chart.yaml", StringComparison.OrdinalIgnoreCase))
                continue;

            using var memory = new MemoryStream();
            await entry.DataStream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            var yaml = Encoding.UTF8.GetString(memory.ToArray());
            return HelmYaml.DeserializeDictionary(yaml);
        }

        throw new InvalidDataException("Chart archive does not contain Chart.yaml.");
    }
}
