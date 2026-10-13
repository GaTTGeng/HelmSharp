using Org.BouncyCastle.Asn1.Nist;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.Sig;
using Org.BouncyCastle.Bcpg.OpenPgp;

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
    internal bool IsUsableSigningKey => IsUsableSigningKeyFor(_publicKey, _master, _certificateKeys);

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
                ComputeExpiry(candidate.Key, candidate.Master),
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
        // RFC 9580 requires a valid Direct Key signature before a V6 primary is used;
        // a surviving UID certification cannot replace one stripped from the certificate.
        var primary = master ?? key;
        if (primary.IsMasterKey && primary.Version == 6 && GetLatestValidPrimaryDirectKeySignature(primary) is null)
            return false;
        if (!AlgorithmCanSign(key))
            return false;
        if (IsExpiredAt(ComputeEffectiveExpiry(key, master), DateTime.UtcNow))
            return false;
        return KeyFlagsPermitSigning(key, master);
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
        // V3 signatures carry creation time in the signature packet itself. Modern
        // signatures require the timestamp in the hashed area so policy ordering cannot
        // be influenced by an unhashed or missing value.
        if (signature.Version is 4 or 5 or 6)
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
                    SignatureSubpacketTag.RevocationKey,
                },
            PgpSignature.DirectKey =>
                new[]
                {
                    SignatureSubpacketTag.CreationTime,
                    SignatureSubpacketTag.ExpireTime,
                    SignatureSubpacketTag.KeyExpireTime,
                    SignatureSubpacketTag.KeyFlags,
                    SignatureSubpacketTag.RevocationKey,
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
                },
            PgpSignature.KeyRevocation or PgpSignature.SubkeyRevocation or PgpSignature.CertificationRevocation =>
                [
                    SignatureSubpacketTag.CreationTime,
                    SignatureSubpacketTag.RevocationReason,
                    SignatureSubpacketTag.IssuerFingerprint,
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

    /// <summary>
    /// Whether the governing key-flags subpacket authorizes data signing.
    /// Flags are asserted by verified direct-key or user-ID signatures on a primary
    /// key and binding signatures on a subkey; the most recent assertion wins. Keys
    /// without a key-flags subpacket keep their historical unrestricted usage, gated
    /// only by <see cref="AlgorithmCanSign"/>.
    /// </summary>
    private static bool KeyFlagsPermitSigning(PgpPublicKey key, PgpPublicKey? master)
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
                    ? HasValidDirectKeySignature(sig, key)
                    : sig.SignatureType is (PgpSignature.PositiveCertification
                        or PgpSignature.CasualCertification or PgpSignature.NoCertification
                        or PgpSignature.DefaultCertification)
                        && HasValidSelfCertification(sig, key);
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

    private static bool HasValidSelfCertification(PgpSignature signature, PgpPublicKey key)
    {
        foreach (var userId in key.GetUserIds())
        {
            if (HasValidUserIdSignature(signature, userId, key))
                return !IsCertificationRevoked(key, userId, signature.CreationTime);
        }

        return false;
    }

    private static bool IsCertificationRevoked(PgpPublicKey key, string userId, DateTime certificationTime)
    {
        PgpSignature? latestRevocation = null;
        foreach (var signature in key.GetSignaturesForId(userId))
        {
            if (signature.SignatureType != PgpSignature.CertificationRevocation ||
                !HasValidUserIdSignature(signature, userId, key))
                continue;

            if (latestRevocation is null || signature.CreationTime > latestRevocation.CreationTime)
                latestRevocation = signature;
        }

        // Revocation wins equal timestamps; a later self-certification can reauthorize
        // the UID while an older or revoked assertion cannot supply policy flags.
        return latestRevocation is not null && latestRevocation.CreationTime >= certificationTime;
    }

    /// <summary>
    /// Absolute expiry of the key, or null when it never expires. The master-key overload
    /// of <c>GetValidSeconds</c> verifies a subkey's binding signature before trusting
    /// its expiration, so a forged binding packet cannot extend a subkey's lifetime.
    /// </summary>
    private static DateTime? ComputeExpiry(PgpPublicKey key, PgpPublicKey? master)
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
            // V6 stores primary-key-wide expiration in a Direct Key signature; a later
            // certification for one UID must not replace that certificate-wide policy.
            var primarySelfSignature = key.Version == 6
                ? GetLatestValidPrimaryDirectKeySignature(key)
                : GetLatestValidPrimarySelfSignature(key);
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

    private static PgpSignature? GetLatestValidPrimarySelfSignature(PgpPublicKey key)
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
                ? HasValidSelfCertification(signature, key)
                : HasValidDirectKeySignature(signature, key);
            if (!isValid)
                continue;
            if (latest is null || signature.CreationTime > latest.CreationTime)
                latest = signature;
        }

        return latest;
    }

    private static PgpSignature? GetLatestValidPrimaryDirectKeySignature(PgpPublicKey key)
    {
        PgpSignature? latest = null;
        foreach (var signature in key.GetSignatures())
        {
            if (signature.SignatureType != PgpSignature.DirectKey || !HasValidDirectKeySignature(signature, key))
                continue;
            if (latest is null || signature.CreationTime > latest.CreationTime)
                latest = signature;
        }

        return latest;
    }

    private static bool HasValidDirectKeySignature(PgpSignature signature, PgpPublicKey key)
        => HasValidPrimaryKeySignature(signature, key, PgpSignature.DirectKey)
            && !IsDirectKeySignatureRevoked(signature, key);

    private static bool IsDirectKeySignatureRevoked(PgpSignature directKeySignature, PgpPublicKey key)
        // A Certification Revocation is hashed over the same primary-key data as the
        // Direct Key assertion it cancels, and revocation wins equal creation times.
        => key.GetSignatures().Any(revocation =>
            revocation.SignatureType == PgpSignature.CertificationRevocation &&
            revocation.CreationTime >= directKeySignature.CreationTime &&
            HasValidPrimaryKeySignature(revocation, key, PgpSignature.CertificationRevocation));

    private static bool HasValidPrimaryKeySignature(PgpSignature signature, PgpPublicKey key, int signatureType)
    {
        try
        {
            if (signature.SignatureType != signatureType ||
                !IsPolicySignatureVersionCompatible(key, signature) ||
                !HasSupportedPolicySubpackets(signature) ||
                !IssuerFingerprintMatches(signature, key) ||
                IsWeakHashAlgorithm(signature.HashAlgorithm) ||
                !HelmProvenance.HasSufficientSignatureHash(signature.HashAlgorithm, key))
                return false;

            signature.InitVerify(key);
            var keyContents = key.PublicKeyPacket.GetEncodedContents();
            byte[] keyPrefix;
            if (key.Version == 4 && keyContents.Length <= ushort.MaxValue)
            {
                keyPrefix = [(byte)0x99, (byte)(keyContents.Length >> 8), (byte)keyContents.Length];
            }
            else if (key.Version is 5 or 6)
            {
                var packetTag = key.Version == 5 ? (byte)0x9A : (byte)0x9B;
                keyPrefix = [
                    packetTag,
                    (byte)(keyContents.Length >> 24),
                    (byte)(keyContents.Length >> 16),
                    (byte)(keyContents.Length >> 8),
                    (byte)keyContents.Length,
                ];
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

    private static DateTime? ComputeEffectiveExpiry(PgpPublicKey key, PgpPublicKey? master)
    {
        var keyExpiry = ComputeExpiry(key, master);
        if (master is null || key.IsMasterKey)
            return keyExpiry;

        var primaryExpiry = ComputeExpiry(master, null);
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
        var allowedFingerprints = GetAuthorizedRevokerFingerprints(primary);
        allowedFingerprints.Add(Convert.ToHexString(primary.GetFingerprint()));

        var revokers = certificateKeys
            .Where(candidate => allowedFingerprints.Contains(Convert.ToHexString(candidate.GetFingerprint())))
            .ToArray();
        var revocationType = key.IsMasterKey ? PgpSignature.KeyRevocation : PgpSignature.SubkeyRevocation;
        foreach (var signature in key.GetKeySignatures())
        {
            if (signature.SignatureType != revocationType || !HasSupportedPolicySubpackets(signature) ||
                IsWeakHashAlgorithm(signature.HashAlgorithm))
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

    private static bool IssuerFingerprintMatches(PgpSignature signature, PgpPublicKey revoker)
    {
        // Unhashed issuer metadata is advisory; only accept a fingerprint bound by the signature.
        var hashed = signature.GetHashedSubPackets();
        if (hashed is null || !hashed.HasSubpacket(SignatureSubpacketTag.IssuerFingerprint))
            return true;

        var expectedFingerprint = revoker.GetFingerprint();
        var expectedLength = signature.Version switch
        {
            4 => 20,
            6 => 32,
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

    private static HashSet<string> GetAuthorizedRevokerFingerprints(PgpPublicKey primary)
    {
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var signature in primary.GetSignatures())
        {
            var isValidAssertion = signature.SignatureType == PgpSignature.DirectKey
                ? HasValidDirectKeySignature(signature, primary)
                : (signature.SignatureType is PgpSignature.PositiveCertification
                    or PgpSignature.CasualCertification
                    or PgpSignature.NoCertification
                    or PgpSignature.DefaultCertification)
                    && HasValidSelfCertification(signature, primary);
            if (!isValidAssertion)
                continue;

            foreach (var revocationKey in signature.GetHashedSubPackets()?.GetRevocationKeys() ?? [])
                fingerprints.Add(Convert.ToHexString(revocationKey.GetFingerprint()));
        }

        return fingerprints;
    }

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

    private static PgpPublicKey MergePublicKeys(IEnumerable<PgpPublicKey> keys)
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
            object? next;
            try
            {
                next = factory.NextPgpObject();
            }
            catch (EndOfStreamException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }

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
            // subsequently been revoked. On ties, prefer revocation and fail closed.
            if (latestCertification is not null &&
                (latestRevocation is null || latestCertification.CreationTime > latestRevocation.CreationTime))
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
    {
        try
        {
            if (!IsPolicySignatureVersionCompatible(key, signature) ||
                !HasSupportedPolicySubpackets(signature) ||
                !IssuerFingerprintMatches(signature, key) ||
                IsWeakHashAlgorithm(signature.HashAlgorithm) ||
                !HelmProvenance.HasSufficientSignatureHash(signature.HashAlgorithm, key))
                return false;

            signature.InitVerify(key);
            return signature.VerifyCertification(userId, key) && !IsSignatureExpired(signature, DateTime.UtcNow);
        }
        catch (PgpException)
        {
            return false;
        }
    }

    private static bool IsWeakHashAlgorithm(HashAlgorithmTag algorithm)
        => algorithm is HashAlgorithmTag.MD5 or HashAlgorithmTag.Sha1 or HashAlgorithmTag.RipeMD160;
}
