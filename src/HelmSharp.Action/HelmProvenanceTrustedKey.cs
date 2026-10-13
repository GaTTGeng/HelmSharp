using System.Security.Cryptography;
using Org.BouncyCastle.Asn1.Nist;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.Sig;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Security;

namespace HelmSharp.Action;

/// <summary>
/// A trusted OpenPGP public key used to authenticate <c>.prov</c> signatures.
/// Verification fails closed unless the signer matches one of the supplied trusted keys.
/// </summary>
/// <remarks>
/// Only keys that OpenPGP policy authorizes to sign become trusted keys: revoked keys,
/// expired keys, and encryption-only or otherwise non-signing material are rejected at
/// import, and <see cref="HelmProvenance.VerifyAsync"/> refuses signatures from any key
/// that is not a valid signing key even when the signature mathematics check out.
/// </remarks>
public sealed class HelmProvenanceTrustedKey
{
    private static readonly HashSet<string> SupportedEcdsaCurveOids =
    [
        NistNamedCurves.GetOid("P-256")!.Id,
        NistNamedCurves.GetOid("P-384")!.Id,
        NistNamedCurves.GetOid("P-521")!.Id,
    ];

    private readonly PgpPublicKey _publicKey;
    private readonly PgpPublicKey? _master;
    private readonly DateTime? _expiresAt;
    private readonly IReadOnlyList<PgpPublicKey> _certificateKeys;

    private HelmProvenanceTrustedKey(
        PgpPublicKey publicKey,
        PgpPublicKey? master,
        DateTime? expiresAt,
        IReadOnlyList<PgpPublicKey> certificateKeys)
    {
        _publicKey = publicKey;
        _master = master;
        _expiresAt = expiresAt;
        _certificateKeys = certificateKeys;
    }

    /// <summary>Hex-encoded OpenPGP V4 key fingerprint (40 hex characters).</summary>
    public string Fingerprint => Convert.ToHexString(_publicKey.GetFingerprint()).ToLowerInvariant();

    /// <summary>Hex-encoded 64-bit OpenPGP key ID.</summary>
    public string KeyId => _publicKey.KeyId.ToString("x16");

    /// <summary>Primary user ID on the key, when present.</summary>
    public string? UserId => GetUserId(_master ?? _publicKey);

    internal PgpPublicKey PublicKey => _publicKey;

    /// <summary>
    /// Whether this key is a valid OpenPGP signing key right now: it is not revoked, has
    /// not expired, and its key flags (or algorithm) permit signing. Verification must
    /// fail closed when this is false.
    /// </summary>
    internal bool IsUsableSigningKey => !IsExpiredAt(DateTime.UtcNow) &&
        IsUsableSigningKeyFor(_publicKey, _master, _certificateKeys);

    internal static bool IsUsableSigningKeyFor(PgpPublicKey key, PgpPublicKey? master)
        => IsUsableSigningKeyFor(key, master, master is null ? [key] : [key, master]);

    internal static bool IsUsableSigningKeyFor(
        PgpPublicKey key,
        PgpPublicKey? master,
        IReadOnlyList<PgpPublicKey> certificateKeys)
        => QualifiesAsSigningKey(key, master, certificateKeys);

    /// <summary>
    /// Wraps binary or ASCII-armored OpenPGP public-key material (a single public key
    /// or a public keyring).
    /// </summary>
    /// <param name="publicKeyData">Encoded public key bytes.</param>
    /// <returns>Trusted key wrapper.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="publicKeyData"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="publicKeyData"/> is empty or contains no usable signing key.</exception>
    public static HelmProvenanceTrustedKey FromPublicKeyData(byte[] publicKeyData)
    {
        var keys = LoadTrustedSigningKeys(publicKeyData, nameof(publicKeyData));
        return keys[0];
    }

    /// <summary>
    /// Loads binary or ASCII-armored OpenPGP public-key material from a file.
    /// When the file is a keyring containing several keys, the first usable signing key
    /// is returned; use <see cref="FromKeyringFile"/> to load every key.
    /// </summary>
    /// <param name="path">Path to a public key or keyring file.</param>
    /// <returns>Trusted key wrapper.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="ArgumentException">The file contains no usable signing key.</exception>
    public static HelmProvenanceTrustedKey FromPublicKeyFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Public key file not found: {path}", path);

        return FromPublicKeyData(File.ReadAllBytes(path));
    }

    /// <summary>
    /// Loads every usable OpenPGP signing key from a keyring file (binary or ASCII-armored).
    /// Secret keyrings are also accepted; only their public halves are retained.
    /// Revoked, expired, and non-signing keys (for example encryption-only subkeys) are
    /// skipped so they can never act as trusted signers.
    /// </summary>
    /// <param name="path">Path to a keyring file.</param>
    /// <returns>One wrapper per usable signing key found.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="ArgumentException">The file contains no usable signing key.</exception>
    public static IReadOnlyList<HelmProvenanceTrustedKey> FromKeyringFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Keyring file not found: {path}", path);

        return LoadTrustedSigningKeys(File.ReadAllBytes(path), nameof(path));
    }

    /// <summary>
    /// Loads every usable OpenPGP signing key from keyring bytes (binary or ASCII-armored).
    /// Revoked, expired, and non-signing keys are skipped so they can never act as
    /// trusted signers.
    /// </summary>
    /// <param name="keyringData">Encoded keyring bytes.</param>
    /// <returns>One wrapper per usable signing key found.</returns>
    /// <exception cref="ArgumentException">The data contains no usable signing key.</exception>
    public static IReadOnlyList<HelmProvenanceTrustedKey> FromKeyringData(byte[] keyringData)
    {
        return LoadTrustedSigningKeys(keyringData, nameof(keyringData));
    }

    /// <summary>
    /// Imports only keys OpenPGP policy authorizes to sign. Fails closed with
    /// <see cref="ArgumentException"/> when the material contains keys but none of them
    /// is a valid signing key (revoked, expired, or encryption-only).
    /// </summary>
    private static List<HelmProvenanceTrustedKey> LoadTrustedSigningKeys(byte[] data, string paramName)
    {
        ArgumentNullException.ThrowIfNull(data);

        var candidates = MergeKeyCandidates(ReadKeyCandidates(data));
        if (candidates.Count == 0)
            throw new ArgumentException("No OpenPGP public key found in the supplied key material.", paramName);

        var certificateKeys = candidates
            .SelectMany(candidate => candidate.Master is null
                ? new[] { candidate.Key }
                : new[] { candidate.Key, candidate.Master })
            .DistinctBy(key => Convert.ToHexString(key.GetFingerprint()), StringComparer.Ordinal)
            .ToArray();
        var trusted = new List<HelmProvenanceTrustedKey>();
        foreach (var candidate in candidates)
        {
            if (!QualifiesAsSigningKey(candidate.Key, candidate.Master, certificateKeys))
                continue;
            trusted.Add(new HelmProvenanceTrustedKey(
                candidate.Key,
                candidate.Master,
                ComputeExpiry(candidate.Key, candidate.Master, certificateKeys),
                certificateKeys));
        }

        if (trusted.Count == 0)
            throw new ArgumentException(
                "No usable OpenPGP signing key found in the supplied key material; revoked, expired, and non-signing keys cannot be trusted signers.",
                paramName);

        return trusted;
    }

    /// <summary>OpenPGP policy check: may this key produce a trusted signature at all?</summary>
    private static bool QualifiesAsSigningKey(
        PgpPublicKey key,
        PgpPublicKey? master,
        IReadOnlyList<PgpPublicKey> certificateKeys)
    {
        // Bouncy Castle 2.7.0 parses only V2, V3, and V4 signature packets. V5/V6
        // public keys cannot establish their policy without the corresponding support.
        if (key.Version is 5 or 6 || master?.Version is 5 or 6)
            return false;

        // A mathematical signature from a revoked or expired key, or from material that
        // is not authorized to sign (encryption-only subkeys), must never be trusted.
        if (HasValidRevocation(key, master, certificateKeys) ||
            (master is not null && HasValidRevocation(master, null, certificateKeys)))
            return false;
        if (!key.IsMasterKey && (master is null || !master.IsMasterKey))
            return false;
        // RFC 9580 requires a subkey to use its primary key's version; otherwise,
        // even a mathematically valid binding cannot establish a valid certificate.
        if (master is not null && key.Version != master.Version)
            return false;
        if (!AlgorithmCanSign(key))
            return false;
        if (IsExpiredAt(ComputeEffectiveExpiry(key, master, certificateKeys), DateTime.UtcNow))
            return false;
        return KeyFlagsPermitSigning(key, master, certificateKeys);
    }

    /// <summary>
    /// Algorithms and key sizes permitted for OpenPGP signing. RSA keys below 2048 bits,
    /// ECDSA curves outside NIST P-256/P-384/P-521, DSA, and encryption-only algorithms
    /// are excluded even when key flags claim otherwise.
    /// </summary>
    private static bool AlgorithmCanSign(PgpPublicKey key) => key.Algorithm switch
    {
        PublicKeyAlgorithmTag.RsaGeneral or PublicKeyAlgorithmTag.RsaSign => key.BitStrength >= 2048,
        PublicKeyAlgorithmTag.ECDsa => key.PublicKeyPacket.Key is ECPublicBcpgKey ecKey &&
            SupportedEcdsaCurveOids.Contains(ecKey.CurveOid.Id),
        PublicKeyAlgorithmTag.EdDsa_Legacy or
        PublicKeyAlgorithmTag.Ed25519 or
        PublicKeyAlgorithmTag.Ed448 => true,
        _ => false,
    };

    private static bool HasSupportedPolicySubpackets(PgpSignature signature)
    {
        if (signature.Version is not (3 or 4))
            return false;

        // V3 signatures carry creation time in the signature packet itself. Modern
        // signatures require the timestamp in the hashed area so policy ordering cannot
        // be influenced by an unhashed or missing value.
        if (signature.Version == 4)
        {
            var hashed = signature.GetHashedSubPackets();
            if (hashed is null || !hashed.HasSubpacket(SignatureSubpacketTag.CreationTime))
                return false;
        }

        var supportedTags = signature.SignatureType switch
        {
            PgpSignature.PositiveCertification or PgpSignature.CasualCertification or
                PgpSignature.NoCertification or PgpSignature.DefaultCertification =>
                new[]
                {
                    SignatureSubpacketTag.CreationTime,
                    SignatureSubpacketTag.ExpireTime,
                    SignatureSubpacketTag.KeyExpireTime,
                    SignatureSubpacketTag.KeyFlags,
                    SignatureSubpacketTag.PrimaryUserId,
                    SignatureSubpacketTag.Revocable,
                    SignatureSubpacketTag.RevocationKey,
                    SignatureSubpacketTag.IssuerFingerprint,
                },
            PgpSignature.DirectKey =>
                new[]
                {
                    SignatureSubpacketTag.CreationTime,
                    SignatureSubpacketTag.ExpireTime,
                    SignatureSubpacketTag.KeyExpireTime,
                    SignatureSubpacketTag.KeyFlags,
                    SignatureSubpacketTag.Revocable,
                    SignatureSubpacketTag.RevocationKey,
                    SignatureSubpacketTag.IssuerFingerprint,
                },
            PgpSignature.SubkeyBinding =>
                new[]
                {
                    SignatureSubpacketTag.CreationTime,
                    SignatureSubpacketTag.ExpireTime,
                    SignatureSubpacketTag.KeyExpireTime,
                    SignatureSubpacketTag.KeyFlags,
                    SignatureSubpacketTag.EmbeddedSignature,
                    SignatureSubpacketTag.IssuerFingerprint,
                },
            PgpSignature.PrimaryKeyBinding =>
                new[]
                {
                    SignatureSubpacketTag.CreationTime,
                    SignatureSubpacketTag.ExpireTime,
                    SignatureSubpacketTag.IssuerFingerprint,
                },
            PgpSignature.KeyRevocation or PgpSignature.SubkeyRevocation or PgpSignature.CertificationRevocation =>
                [
                    SignatureSubpacketTag.CreationTime,
                    SignatureSubpacketTag.ExpireTime,
                    SignatureSubpacketTag.RevocationReason,
                    SignatureSubpacketTag.IssuerFingerprint,
                    SignatureSubpacketTag.SignatureTarget,
                ],
            _ => Array.Empty<SignatureSubpacketTag>(),
        };

        return IsValidRevocationReason(signature) &&
            !HelmProvenance.HasUnsupportedCriticalSubpackets(signature, supportedTags);
    }

    private static bool IsPolicySignatureVersionCompatible(PgpPublicKey signingKey, PgpSignature signature)
        => HelmProvenance.SignatureVersionMatchesKey(signature.Version, signingKey.Version, signingKey.Algorithm);

    private static bool IsValidRevocationReason(PgpSignature signature)
    {
        if (signature.SignatureType is not (PgpSignature.KeyRevocation or PgpSignature.SubkeyRevocation or PgpSignature.CertificationRevocation))
            return true;

        var hashed = signature.GetHashedSubPackets();
        if (hashed is null)
            return true;

        foreach (var subpacket in hashed.GetSubpackets(SignatureSubpacketTag.RevocationReason))
        {
            var data = subpacket.GetData();
            if (data.Length == 0)
                return false;

            var code = data[0];
            var validCode = code is >= 100 and <= 110 || signature.SignatureType switch
            {
                PgpSignature.KeyRevocation or PgpSignature.SubkeyRevocation => code is 0 or 1 or 2 or 3,
                PgpSignature.CertificationRevocation => code is 0 or 32,
                _ => false,
            };
            if (!validCode)
                return false;

            try
            {
                _ = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(data, 1, data.Length - 1);
            }
            catch (System.Text.DecoderFallbackException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool RevocationTargetsSignature(PgpSignature revocation, PgpSignature targetSignature)
    {
        var hashed = revocation.GetHashedSubPackets();
        if (hashed is null || !hashed.HasSubpacket(SignatureSubpacketTag.SignatureTarget))
            return true;

        var targetPackets = hashed.GetSubpackets(SignatureSubpacketTag.SignatureTarget);
        if (targetPackets.Length != 1 || targetPackets[0] is not SignatureTarget signatureTarget ||
            signatureTarget.PublicKeyAlgorithm != (int)targetSignature.KeyAlgorithm ||
            !TryGetTargetDigestParameters(signatureTarget.HashAlgorithm, out var digestAlgorithm, out var expectedLength))
            return false;

        var targetDigest = signatureTarget.GetHashData();
        if (targetDigest.Length != expectedLength || !TryGetTargetSignatureBody(targetSignature, out var signatureBody))
            return false;

        // The target digest identifies a signature packet; it is independent of the
        // revocation signature's own hash, which is validated separately.
        var actualDigest = DigestUtilities.CalculateDigest(digestAlgorithm, signatureBody);

        return actualDigest.Length == expectedLength &&
            CryptographicOperations.FixedTimeEquals(actualDigest, targetDigest);
    }

    private static bool TryGetTargetDigestParameters(int hashAlgorithm, out string algorithm, out int digestLength)
    {
        algorithm = string.Empty;
        digestLength = 0;
        switch ((HashAlgorithmTag)hashAlgorithm)
        {
            case HashAlgorithmTag.MD5:
                algorithm = "MD5";
                digestLength = 16;
                return true;
            case HashAlgorithmTag.Sha1:
                algorithm = "SHA1";
                digestLength = 20;
                return true;
            case HashAlgorithmTag.RipeMD160:
                algorithm = "RIPEMD160";
                digestLength = 20;
                return true;
            case HashAlgorithmTag.Sha224:
                algorithm = "SHA224";
                digestLength = 28;
                return true;
            case HashAlgorithmTag.Sha256:
                algorithm = "SHA256";
                digestLength = 32;
                return true;
            case HashAlgorithmTag.Sha384:
                algorithm = "SHA384";
                digestLength = 48;
                return true;
            case HashAlgorithmTag.Sha512:
                algorithm = "SHA512";
                digestLength = 64;
                return true;
            case HashAlgorithmTag.Sha3_256:
                algorithm = "SHA3-256";
                digestLength = 32;
                return true;
            case HashAlgorithmTag.Sha3_512:
                algorithm = "SHA3-512";
                digestLength = 64;
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetTargetSignatureBody(PgpSignature signature, out byte[] signatureBody)
    {
        var packetEncoding = signature.GetEncoded();
        signatureBody = Array.Empty<byte>();
        if (packetEncoding.Length < 2 || (packetEncoding[0] & 0x80) == 0)
            return false;

        int packetTag;
        int bodyOffset;
        long bodyLength;
        if ((packetEncoding[0] & 0x40) != 0)
        {
            packetTag = packetEncoding[0] & 0x3f;
            var firstLengthOctet = packetEncoding[1];
            if (firstLengthOctet < 192)
            {
                bodyOffset = 2;
                bodyLength = firstLengthOctet;
            }
            else if (firstLengthOctet < 224 && packetEncoding.Length >= 3)
            {
                bodyOffset = 3;
                bodyLength = ((firstLengthOctet - 192) << 8) + packetEncoding[2] + 192;
            }
            else if (firstLengthOctet == 255 && packetEncoding.Length >= 6)
            {
                bodyOffset = 6;
                bodyLength = ((long)packetEncoding[2] << 24) |
                    ((long)packetEncoding[3] << 16) |
                    ((long)packetEncoding[4] << 8) |
                    packetEncoding[5];
            }
            else
            {
                // Bouncy Castle emits definite lengths here; reject partial or truncated framing.
                return false;
            }
        }
        else
        {
            packetTag = (packetEncoding[0] >> 2) & 0x0f;
            switch (packetEncoding[0] & 0x03)
            {
                case 0:
                    bodyOffset = 2;
                    bodyLength = packetEncoding[1];
                    break;
                case 1 when packetEncoding.Length >= 3:
                    bodyOffset = 3;
                    bodyLength = (packetEncoding[1] << 8) | packetEncoding[2];
                    break;
                case 2 when packetEncoding.Length >= 5:
                    bodyOffset = 5;
                    bodyLength = ((long)packetEncoding[1] << 24) |
                        ((long)packetEncoding[2] << 16) |
                        ((long)packetEncoding[3] << 8) |
                        packetEncoding[4];
                    break;
                default:
                    return false;
            }
        }

        if (packetTag != 2 || bodyLength <= 0 || bodyLength > int.MaxValue ||
            bodyOffset + bodyLength > packetEncoding.Length)
            return false;

        // Signature Target identifies the signature packet body. Exclude packet framing
        // and any following local trust packet so the digest is independent of transport.
        signatureBody = packetEncoding.AsSpan(bodyOffset, (int)bodyLength).ToArray();
        return true;
    }

    /// <summary>
    /// Whether the governing key-flags subpacket authorizes data signing.
    /// Flags are asserted by verified direct-key or user-ID signatures on a primary
    /// key and binding signatures on a subkey; the most recent assertion wins. Keys
    /// without a key-flags subpacket keep their historical unrestricted usage, gated
    /// only by <see cref="AlgorithmCanSign"/>.
    /// </summary>
    private static bool KeyFlagsPermitSigning(
        PgpPublicKey key,
        PgpPublicKey? master,
        IReadOnlyList<PgpPublicKey> certificateKeys)
    {
        // Third-party certifications must not widen what a key may do, so a primary key
        // only trusts key flags it asserted itself; a subkey is governed by its binding
        // signature. A subkey is governed only by a cryptographically verified binding
        // signature issued by its primary key; raw packet metadata is attacker-controlled.
        var assertions = key.IsMasterKey
            ? key.GetSignatures()
            : master is null
                ? Enumerable.Empty<PgpSignature>()
                : GetValidSubkeyBindings(key, master);

        PgpSignature? governing = null;
        foreach (var sig in assertions)
        {
            if (key.IsMasterKey)
            {
                var validPrimaryAssertion = sig.SignatureType == PgpSignature.DirectKey
                    ? HasValidDirectKeySignature(sig, key, certificateKeys)
                    : sig.SignatureType is (PgpSignature.PositiveCertification
                        or PgpSignature.CasualCertification or PgpSignature.NoCertification
                        or PgpSignature.DefaultCertification)
                        && HasValidSelfCertification(sig, key, certificateKeys);
                if (!validPrimaryAssertion)
                    continue;
            }

            if (governing is null || sig.CreationTime > governing.CreationTime)
                governing = sig;
        }

        if (governing is null)
            return false;

        var hashed = governing.GetHashedSubPackets();
        if (hashed is null || !hashed.HasSubpacket(SignatureSubpacketTag.KeyFlags))
            return AlgorithmCanSign(key);

        var flags = hashed.GetKeyFlags();
        return (flags & PgpKeyFlags.CanSign) != 0;
    }

    private static bool HasValidSubkeyBinding(PgpSignature signature, PgpPublicKey master, PgpPublicKey subkey)
    {
        try
        {
            if (master.Version != subkey.Version ||
                !IsPolicySignatureVersionCompatible(master, signature) ||
                !HasSupportedPolicySubpackets(signature) ||
                !IssuerFingerprintMatches(signature, master) ||
                IsWeakHashAlgorithm(signature.HashAlgorithm) ||
                !HelmProvenance.HasSufficientSignatureHash(signature.HashAlgorithm, master))
                return false;

            signature.InitVerify(master);
            if (!signature.VerifyCertification(master, subkey))
                return false;

            if (IsSignatureExpired(signature, DateTime.UtcNow))
                return false;

            var hashed = signature.GetHashedSubPackets();
            var keyFlags = hashed is not null && hashed.HasSubpacket(SignatureSubpacketTag.KeyFlags)
                ? hashed.GetKeyFlags()
                : 0;
            var permitsSigning = (keyFlags & PgpKeyFlags.CanSign) != 0
                || (keyFlags == 0 && AlgorithmCanSign(subkey));
            return !permitsSigning || HasValidBackSignature(signature, master, subkey);
        }
        catch (Exception exception) when (exception is PgpException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool HasValidBackSignature(PgpSignature binding, PgpPublicKey master, PgpPublicKey subkey)
    {
        try
        {
            var embeddedSignatures = binding.GetHashedSubPackets()?.GetEmbeddedSignatures();
            if (embeddedSignatures is null)
                return false;

            for (var index = 0; index < embeddedSignatures.Count; index++)
            {
                var embedded = embeddedSignatures[index];
                if (embedded.SignatureType != PgpSignature.PrimaryKeyBinding ||
                    !IsPolicySignatureVersionCompatible(subkey, embedded) ||
                    !HasSupportedPolicySubpackets(embedded) ||
                    !IssuerFingerprintMatches(embedded, subkey) ||
                    IsWeakHashAlgorithm(embedded.HashAlgorithm) ||
                    !HelmProvenance.HasSufficientSignatureHash(embedded.HashAlgorithm, subkey) ||
                    IsSignatureExpired(embedded, DateTime.UtcNow))
                    continue;

                embedded.InitVerify(subkey);
                if (embedded.VerifyCertification(master, subkey))
                    return true;
            }
        }
        catch (Exception exception) when (exception is PgpException or ArgumentOutOfRangeException
            or IOException or InvalidOperationException or ArgumentException)
        {
            // A missing, malformed, expired, or forged back-signature cannot authorize a signing subkey.
        }

        return false;
    }

    private static IEnumerable<PgpSignature> GetValidSubkeyBindings(PgpPublicKey subkey, PgpPublicKey master)
        => subkey.GetKeySignatures().Where(signature => signature.SignatureType == PgpSignature.SubkeyBinding
            && HasValidSubkeyBinding(signature, master, subkey));

    private static bool HasValidSelfCertification(
        PgpSignature signature,
        PgpPublicKey key,
        IReadOnlyList<PgpPublicKey> certificateKeys)
    {
        foreach (var userId in key.GetUserIds())
        {
            if (HasValidUserIdSignature(signature, userId, key))
                return !IsCertificationRevoked(key, userId, signature, certificateKeys);
        }

        return false;
    }

    private static bool IsCertificationRevoked(
        PgpPublicKey key,
        string userId,
        PgpSignature certification,
        IReadOnlyList<PgpPublicKey> certificateKeys)
    {
        if (IsNonRevocable(certification))
            return false;

        var revokers = GetAuthorizedRevokers(key, certificateKeys);
        foreach (var signature in key.GetSignaturesForId(userId))
        {
            if (signature.SignatureType != PgpSignature.CertificationRevocation ||
                signature.CreationTime < certification.CreationTime ||
                !RevocationTargetsSignature(signature, certification))
                continue;

            if (revokers.Any(revoker => HasValidUserIdSignature(signature, userId, key, revoker)))
                return true;
        }

        // A later self-certification can reauthorize the UID after a targetless legacy
        // revocation; a targeted revocation only invalidates its exact assertion.
        return false;
    }

    /// <summary>
    /// Absolute expiry of the key, or null when it never expires. The master-key overload
    /// of <c>GetValidSeconds</c> verifies a subkey's binding signature before trusting
    /// its expiration, so a forged binding packet cannot extend a subkey's lifetime.
    /// </summary>
    private static DateTime? ComputeExpiry(
        PgpPublicKey key,
        PgpPublicKey? master,
        IReadOnlyList<PgpPublicKey>? certificateKeys = null)
    {
        if (key.IsMasterKey)
        {
            // V2/V3 public-key packets store validity directly as days on the key,
            // whereas V4+ keys derive expiry from a verified self-signature.
            if (key.Version is 2 or 3)
            {
                var packetValiditySeconds = key.GetValidSeconds();
                return packetValiditySeconds <= 0
                    ? null
                    : key.CreationTime.AddSeconds(packetValiditySeconds);
            }

            // Bouncy Castle's parameterless GetValidSeconds reads packet metadata without
            // proving the self-signature. Only a certification verified by the primary
            // key may define its lifetime.
            var primarySelfSignature = GetLatestValidPrimarySelfSignature(key, certificateKeys ?? [key]);
            var primaryLifetime = primarySelfSignature?.GetHashedSubPackets()?.GetKeyExpirationTime() ?? 0;
            return primaryLifetime <= 0 ? null : key.CreationTime.AddSeconds(primaryLifetime);
        }

        // Use the same current, verified binding set as key-flag authorization. The
        // Bouncy Castle validity overload can consume an expired signature's lifetime.
        var latestBinding = master is null
            ? null
            : GetValidSubkeyBindings(key, master).MaxBy(signature => signature.CreationTime);
        // Zero means the key never expires (RFC 4880 §5.2.3.3).
        long validSeconds = latestBinding?.GetHashedSubPackets()?.GetKeyExpirationTime() ?? 0;
        return validSeconds <= 0 ? null : key.CreationTime.AddSeconds(validSeconds);
    }

    private static PgpSignature? GetLatestValidPrimarySelfSignature(
        PgpPublicKey key,
        IReadOnlyList<PgpPublicKey> certificateKeys)
    {
        PgpSignature? latest = null;
        foreach (var signature in key.GetSignatures())
        {
            var isCertification = signature.SignatureType is PgpSignature.PositiveCertification
                or PgpSignature.CasualCertification or PgpSignature.NoCertification
                or PgpSignature.DefaultCertification;
            if (!isCertification && signature.SignatureType != PgpSignature.DirectKey)
                continue;

            var isValid = isCertification
                ? HasValidSelfCertification(signature, key, certificateKeys)
                : HasValidDirectKeySignature(signature, key, certificateKeys);
            if (!isValid)
                continue;
            if (latest is null || signature.CreationTime > latest.CreationTime)
                latest = signature;
        }

        return latest;
    }

    private static bool HasValidDirectKeySignature(
        PgpSignature signature,
        PgpPublicKey key,
        IReadOnlyList<PgpPublicKey> certificateKeys)
        => HasValidPrimaryKeySignature(signature, key, PgpSignature.DirectKey)
            && !IsDirectKeySignatureRevoked(signature, key, certificateKeys);

    private static bool IsDirectKeySignatureRevoked(
        PgpSignature directKeySignature,
        PgpPublicKey key,
        IReadOnlyList<PgpPublicKey> certificateKeys)
        // A Certification Revocation is hashed over the same primary-key data as the
        // Direct Key assertion it cancels, and revocation wins equal creation times.
        => IsDirectKeySignatureRevokedByItsDeclaration(directKeySignature, key, certificateKeys);

    private static bool HasValidPrimaryKeySignature(PgpSignature signature, PgpPublicKey key, int signatureType)
        => HasValidPrimaryKeySignature(signature, key, signatureType, key);

    private static bool HasValidPrimaryKeySignature(
        PgpSignature signature,
        PgpPublicKey key,
        int signatureType,
        PgpPublicKey signer)
    {
        try
        {
            if (signature.SignatureType != signatureType ||
                !IsPolicySignatureVersionCompatible(signer, signature) ||
                !HasSupportedPolicySubpackets(signature) ||
                !IssuerFingerprintMatches(signature, signer) ||
                IsWeakHashAlgorithm(signature.HashAlgorithm) ||
                !HelmProvenance.HasSufficientSignatureHash(signature.HashAlgorithm, signer))
                return false;

            signature.InitVerify(signer);
            var keyContents = key.PublicKeyPacket.GetEncodedContents();
            byte[] keyPrefix;
            if (key.Version == 4 && keyContents.Length <= ushort.MaxValue)
            {
                keyPrefix = [0x99, (byte)(keyContents.Length >> 8), (byte)keyContents.Length];
            }
            else
            {
                return false;
            }

            // Bouncy Castle's public VerifyCertification(PgpPublicKey) overload accepts
            // key revocations only; a direct-key signature hashes the primary packet.
            signature.Update(keyPrefix);
            signature.Update(keyContents);
            return signature.Verify() && !IsSignatureExpired(signature, DateTime.UtcNow);
        }
        catch (Exception exception) when (exception is PgpException or IOException
            or InvalidOperationException or ArgumentException)
        {
            // A forged or malformed direct-key packet cannot set primary-key expiry.
            return false;
        }
    }

    private static DateTime? ComputeEffectiveExpiry(
        PgpPublicKey key,
        PgpPublicKey? master,
        IReadOnlyList<PgpPublicKey> certificateKeys)
    {
        var keyExpiry = ComputeExpiry(key, master, certificateKeys);
        if (master is null || key.IsMasterKey)
            return keyExpiry;

        var primaryExpiry = ComputeExpiry(master, null, certificateKeys);
        return keyExpiry is null ? primaryExpiry
            : primaryExpiry is null ? keyExpiry
            : keyExpiry < primaryExpiry ? keyExpiry : primaryExpiry;
    }

    private static bool IsSignatureExpired(PgpSignature signature, DateTime at)
    {
        var validitySeconds = signature.GetHashedSubPackets()?.GetSignatureExpirationTime() ?? 0;
        if (validitySeconds <= 0)
            return false;

        try
        {
            return IsExpiredAt(signature.CreationTime.AddSeconds(validitySeconds), at);
        }
        catch (ArgumentOutOfRangeException)
        {
            // An unrepresentable expiration timestamp cannot extend authorization.
            return true;
        }
    }

    private static bool IsExpiredAt(DateTime? expiresAt, DateTime at)
        => expiresAt is not null && at >= expiresAt.Value;

    private bool IsExpiredAt(DateTime at) => IsExpiredAt(_expiresAt, at);

    private static bool HasValidRevocation(
        PgpPublicKey key,
        PgpPublicKey? master,
        IReadOnlyList<PgpPublicKey> certificateKeys)
    {
        var primary = master ?? key;
        var revokers = GetAuthorizedRevokers(primary, certificateKeys);
        var revocationType = key.IsMasterKey ? PgpSignature.KeyRevocation : PgpSignature.SubkeyRevocation;
        foreach (var signature in key.GetKeySignatures())
        {
            if (signature.SignatureType != revocationType || !HasSupportedPolicySubpackets(signature) ||
                IsWeakHashAlgorithm(signature.HashAlgorithm) ||
                IsSignatureExpired(signature, DateTime.UtcNow))
                continue;

            foreach (var revoker in revokers)
            {
                try
                {
                    if (!IsPolicySignatureVersionCompatible(revoker, signature) ||
                        !IssuerFingerprintMatches(signature, revoker) ||
                        !HelmProvenance.HasSufficientSignatureHash(signature.HashAlgorithm, revoker))
                        continue;

                    signature.InitVerify(revoker);
                    var valid = key.IsMasterKey
                        ? signature.VerifyCertification(key)
                        : master is not null && signature.VerifyCertification(master, key);
                    if (valid)
                        return true;
                }
                catch (Exception exception) when (exception is PgpException or IOException
                    or InvalidOperationException or ArgumentException)
                {
                    // Packet presence alone cannot revoke a trusted key.
                }
            }
        }

        return false;
    }

    internal static bool IssuerFingerprintMatches(PgpSignature signature, PgpPublicKey revoker)
    {
        // Unhashed issuer metadata is advisory; only accept a fingerprint bound by the signature.
        var hashed = signature.GetHashedSubPackets();
        if (hashed is null || !hashed.HasSubpacket(SignatureSubpacketTag.IssuerFingerprint))
            return true;

        var expectedFingerprint = revoker.GetFingerprint();
        var expectedLength = signature.Version switch
        {
            4 => 20,
            _ => 0,
        };
        if (expectedLength == 0 || expectedFingerprint.Length != expectedLength)
            return false;

        var issuerFingerprints = hashed.GetSubpackets(SignatureSubpacketTag.IssuerFingerprint);
        if (issuerFingerprints.Length == 0)
            return false;

        foreach (var subpacket in issuerFingerprints)
        {
            if (subpacket is not IssuerFingerprint issuerFingerprint)
                return false;

            var data = issuerFingerprint.GetData();
            if (data.Length != expectedLength + 1 ||
                data[0] != signature.Version ||
                issuerFingerprint.KeyVersion != signature.Version)
                return false;

            var fingerprint = issuerFingerprint.GetFingerprint();
            if (fingerprint.Length != expectedLength ||
                !fingerprint.AsSpan().SequenceEqual(expectedFingerprint))
                return false;
        }

        return true;
    }

    private static IReadOnlyList<PgpPublicKey> GetAuthorizedRevokers(
        PgpPublicKey primary,
        IReadOnlyList<PgpPublicKey> certificateKeys)
    {
        var authorizedFingerprints = GetAuthorizedRevokerFingerprints(primary, certificateKeys);
        var primaryFingerprint = Convert.ToHexString(primary.GetFingerprint());
        return certificateKeys
            .Append(primary)
            .DistinctBy(candidate => Convert.ToHexString(candidate.GetFingerprint()), StringComparer.Ordinal)
            .Where(candidate =>
            {
                var fingerprint = Convert.ToHexString(candidate.GetFingerprint());
                return fingerprint == primaryFingerprint ||
                    (authorizedFingerprints.TryGetValue(fingerprint, out var algorithms) && algorithms.Contains(candidate.Algorithm));
            })
            .ToArray();
    }

    private static Dictionary<string, HashSet<PublicKeyAlgorithmTag>> GetAuthorizedRevokerFingerprints(
        PgpPublicKey primary,
        IReadOnlyList<PgpPublicKey> certificateKeys)
    {
        // RFC 9580 places Revocation Key declarations on Direct Key signatures and
        // requires the most recent valid self-signature on an object to govern it.
        // Ignore older declarations so a replacement can remove a former revoker.
        var latestDirectKey = GetLatestUnrevokedDirectKeySignature(primary, certificateKeys);
        return latestDirectKey?.GetHashedSubPackets()?.GetRevocationKeys()
            .Where(IsValidRevocationKeyDeclaration)
            .GroupBy(revocationKey => Convert.ToHexString(revocationKey.GetFingerprint()), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(revocationKey => revocationKey.Algorithm).ToHashSet(),
                StringComparer.Ordinal) ?? new Dictionary<string, HashSet<PublicKeyAlgorithmTag>>(StringComparer.Ordinal);
    }

    private static bool IsValidRevocationKeyDeclaration(RevocationKey revocationKey)
        => ((int)revocationKey.SignatureClass & 0x80) != 0 &&
            ((int)revocationKey.SignatureClass & 0x3f) == 0 &&
            revocationKey.Algorithm is PublicKeyAlgorithmTag.RsaGeneral or PublicKeyAlgorithmTag.RsaSign or
                PublicKeyAlgorithmTag.Dsa or PublicKeyAlgorithmTag.ECDsa or PublicKeyAlgorithmTag.EdDsa_Legacy or
                PublicKeyAlgorithmTag.Ed25519 or PublicKeyAlgorithmTag.Ed448;

    private static PgpSignature? GetLatestUnrevokedDirectKeySignature(
        PgpPublicKey primary,
        IReadOnlyList<PgpPublicKey> certificateKeys)
    {
        PgpSignature? latest = null;
        foreach (var signature in primary.GetSignatures())
        {
            if (signature.SignatureType != PgpSignature.DirectKey ||
                !HasValidPrimaryKeySignature(signature, primary, PgpSignature.DirectKey) ||
                IsDirectKeySignatureRevokedByItsDeclaration(signature, primary, certificateKeys))
                continue;

            if (latest is null || signature.CreationTime > latest.CreationTime)
                latest = signature;
        }

        return latest;
    }

    private static bool IsDirectKeySignatureRevokedByItsDeclaration(
        PgpSignature directKeySignature,
        PgpPublicKey primary,
        IReadOnlyList<PgpPublicKey> certificateKeys)
    {
        if (IsNonRevocable(directKeySignature))
            return false;

        var authorizedFingerprints = directKeySignature.GetHashedSubPackets()?.GetRevocationKeys()
            .Where(IsValidRevocationKeyDeclaration)
            .GroupBy(revocationKey => Convert.ToHexString(revocationKey.GetFingerprint()), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(revocationKey => revocationKey.Algorithm).ToHashSet(),
                StringComparer.Ordinal)
            ?? new Dictionary<string, HashSet<PublicKeyAlgorithmTag>>(StringComparer.Ordinal);
        var primaryFingerprint = Convert.ToHexString(primary.GetFingerprint());
        var revokers = certificateKeys
            .Append(primary)
            .DistinctBy(candidate => Convert.ToHexString(candidate.GetFingerprint()), StringComparer.Ordinal)
            .Where(candidate =>
            {
                var fingerprint = Convert.ToHexString(candidate.GetFingerprint());
                return fingerprint == primaryFingerprint ||
                    (authorizedFingerprints.TryGetValue(fingerprint, out var algorithms) && algorithms.Contains(candidate.Algorithm));
            })
            .ToArray();
        return primary.GetSignatures().Any(revocation =>
            revocation.SignatureType == PgpSignature.CertificationRevocation &&
            revocation.CreationTime >= directKeySignature.CreationTime &&
            RevocationTargetsSignature(revocation, directKeySignature) &&
            revokers.Any(revoker => HasValidPrimaryKeySignature(
                revocation,
                primary,
                PgpSignature.CertificationRevocation,
                revoker)));
    }

    private static bool IsNonRevocable(PgpSignature signature)
        => signature.GetHashedSubPackets()?.GetSubpackets(SignatureSubpacketTag.Revocable)
            .OfType<Revocable>()
            .Any(revocable => !revocable.IsRevocable()) == true;

    private sealed record KeyCandidate(PgpPublicKey Key, PgpPublicKey? Master);

    private static List<KeyCandidate> ReadKeyCandidates(byte[] data)
    {
        var result = new List<KeyCandidate>();
        foreach (var obj in ReadPgpObjects(data))
        {
            switch (obj)
            {
                case PgpPublicKeyRing ring:
                    AddRing(result, ring.GetPublicKeys());
                    break;
                case PgpPublicKey key:
                    result.Add(new KeyCandidate(key, key.IsMasterKey ? key : null));
                    break;
                case PgpSecretKeyRing secretRing:
                    AddRing(result, secretRing.GetSecretKeys().Select(s => s.PublicKey));
                    break;
                case PgpSecretKey secretKey:
                    result.Add(new KeyCandidate(secretKey.PublicKey, secretKey.PublicKey.IsMasterKey ? secretKey.PublicKey : null));
                    break;
            }
        }

        return result;
    }

    private static List<KeyCandidate> MergeKeyCandidates(List<KeyCandidate> candidates)
    {
        // Renewed copies of one certificate can carry different verified self-signatures.
        // Merge by full fingerprints so a stale packet copy cannot hide the renewal and
        // distinct keys that collide on the short key ID remain separate.
        return candidates
            .GroupBy(candidate => string.Concat(
                candidate.Master is null ? string.Empty : Convert.ToHexString(candidate.Master.GetFingerprint()),
                ":",
                Convert.ToHexString(candidate.Key.GetFingerprint())), StringComparer.Ordinal)
            .Select(group =>
            {
                var key = MergePublicKeys(group.Select(candidate => candidate.Key));
                var masters = group.Where(candidate => candidate.Master is not null)
                    .Select(candidate => candidate.Master!)
                    .ToArray();
                var master = key.IsMasterKey ? key : masters.Length == 0 ? null : MergePublicKeys(masters);
                return new KeyCandidate(key, master);
            })
            .ToList();
    }

    internal static PgpPublicKey MergePublicKeys(IEnumerable<PgpPublicKey> keys)
    {
        using var enumerator = keys.GetEnumerator();
        if (!enumerator.MoveNext())
            throw new InvalidOperationException("A key candidate must contain public key material.");

        var merged = enumerator.Current;
        while (enumerator.MoveNext())
            merged = PgpPublicKey.Join(merged, enumerator.Current, joinTrustPackets: false, allowSubkeySigsOnNonSubkey: false);
        return merged;
    }

    private static void AddRing(List<KeyCandidate> result, IEnumerable<PgpPublicKey> keys)
    {
        var ringKeys = keys.ToList();
        var master = ringKeys.FirstOrDefault(k => k.IsMasterKey) ?? (ringKeys.Count > 0 ? ringKeys[0] : null);
        foreach (var key in ringKeys)
            result.Add(new KeyCandidate(key, master));
    }

    private static IEnumerable<object> ReadPgpObjects(byte[] data)
    {
        Stream input = new MemoryStream(data, writable: false);
        var isArmored = data.Length >= 27 && data[0] == (byte)'-' && data[1] == (byte)'-';
        Stream packetInput = isArmored ? new ArmoredInputStream(input) : input;
        var factory = new PgpObjectFactory(packetInput);
        while (true)
        {
            var next = factory.NextPgpObject();
            if (next is null)
                break;
            if (next is PgpMarker)
                continue;
            yield return next;
        }
    }

    private static string? GetUserId(PgpPublicKey key)
    {
        var validUserIds = new List<(string UserId, PgpSignature Certification, bool IsPrimary)>();
        foreach (var userId in key.GetUserIds())
        {
            PgpSignature? latestCertification = null;
            PgpSignature? latestRevocation = null;
            foreach (var signature in key.GetSignaturesForId(userId))
            {
                if (!HasValidUserIdSignature(signature, userId, key))
                    continue;

                if (signature.SignatureType == PgpSignature.CertificationRevocation)
                {
                    if (latestRevocation is null || signature.CreationTime > latestRevocation.CreationTime)
                        latestRevocation = signature;
                }
                else if (signature.SignatureType is PgpSignature.PositiveCertification
                    or PgpSignature.CasualCertification or PgpSignature.NoCertification
                    or PgpSignature.DefaultCertification)
                {
                    if (latestCertification is null || signature.CreationTime > latestCertification.CreationTime)
                        latestCertification = signature;
                }
            }

            // A UID is useful only when its self-certification is authentic and has not
            // subsequently been revoked. Targeted revocations affect only their named
            // certification, and a non-revocable certification cannot be revoked.
            var isRevoked = latestCertification is not null &&
                !IsNonRevocable(latestCertification) &&
                latestRevocation is not null &&
                latestRevocation.CreationTime >= latestCertification.CreationTime &&
                RevocationTargetsSignature(latestRevocation, latestCertification);
            if (latestCertification is not null && !isRevoked)
            {
                var isPrimary = latestCertification.GetHashedSubPackets()?.IsPrimaryUserId() == true;
                validUserIds.Add((userId, latestCertification, isPrimary));
            }
        }

        // Honor the newest valid certification that explicitly marks its UID primary;
        // when a certificate has no primary marker, retain the first valid packet order.
        return validUserIds
            .Where(candidate => candidate.IsPrimary)
            .OrderByDescending(candidate => candidate.Certification.CreationTime)
            .Select(candidate => candidate.UserId)
            .FirstOrDefault()
            ?? validUserIds.FirstOrDefault().UserId;
    }

    private static bool HasValidUserIdSignature(PgpSignature signature, string userId, PgpPublicKey key)
        => HasValidUserIdSignature(signature, userId, key, key);

    private static bool HasValidUserIdSignature(
        PgpSignature signature,
        string userId,
        PgpPublicKey key,
        PgpPublicKey signer)
    {
        try
        {
            if (!IsPolicySignatureVersionCompatible(signer, signature) ||
                !HasSupportedPolicySubpackets(signature) ||
                !IssuerFingerprintMatches(signature, signer) ||
                IsWeakHashAlgorithm(signature.HashAlgorithm) ||
                !HelmProvenance.HasSufficientSignatureHash(signature.HashAlgorithm, signer))
                return false;

            signature.InitVerify(signer);
            var verified = signer.GetFingerprint().AsSpan().SequenceEqual(key.GetFingerprint())
                ? signature.VerifyCertification(userId, key)
                : VerifyUserIdCertification(signature, userId, key);
            return verified && !IsSignatureExpired(signature, DateTime.UtcNow);
        }
        catch (PgpException)
        {
            return false;
        }
    }

    private static bool VerifyUserIdCertification(PgpSignature signature, string userId, PgpPublicKey key)
    {
        // The library helper assumes the certified key also signed the UID; designated
        // revokers sign the same certification data with their own key instead.
        var keyContents = key.PublicKeyPacket.GetEncodedContents();
        byte[] keyPrefix;
        if (key.Version == 4 && keyContents.Length <= ushort.MaxValue)
        {
            keyPrefix = [0x99, (byte)(keyContents.Length >> 8), (byte)keyContents.Length];
        }
        else
        {
            return false;
        }

        var userIdBytes = System.Text.Encoding.UTF8.GetBytes(userId);
        signature.Update(keyPrefix);
        signature.Update(keyContents);
        signature.Update(0xB4);
        signature.Update((byte)(userIdBytes.Length >> 24));
        signature.Update((byte)(userIdBytes.Length >> 16));
        signature.Update((byte)(userIdBytes.Length >> 8));
        signature.Update((byte)userIdBytes.Length);
        signature.Update(userIdBytes);
        return signature.Verify();
    }

    private static bool IsWeakHashAlgorithm(HashAlgorithmTag algorithm)
        => algorithm is HashAlgorithmTag.MD5 or HashAlgorithmTag.Sha1 or HashAlgorithmTag.RipeMD160;
}
