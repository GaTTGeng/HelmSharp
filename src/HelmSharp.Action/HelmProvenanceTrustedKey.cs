using Org.BouncyCastle.Bcpg;
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
    private readonly PgpPublicKey _publicKey;
    private readonly PgpPublicKey? _master;
    private readonly DateTime? _expiresAt;

    private HelmProvenanceTrustedKey(PgpPublicKey publicKey, PgpPublicKey? master, DateTime? expiresAt)
    {
        _publicKey = publicKey;
        _master = master;
        _expiresAt = expiresAt;
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
    internal bool IsUsableSigningKey => IsUsableSigningKeyFor(_publicKey, _master);

    internal static bool IsUsableSigningKeyFor(PgpPublicKey key, PgpPublicKey? master)
        => QualifiesAsSigningKey(key, master);

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

        var candidates = ReadKeyCandidates(data);
        if (candidates.Count == 0)
            throw new ArgumentException("No OpenPGP public key found in the supplied key material.", paramName);

        var trusted = new List<HelmProvenanceTrustedKey>();
        foreach (var candidate in candidates)
        {
            if (!QualifiesAsSigningKey(candidate.Key, candidate.Master))
                continue;
            trusted.Add(new HelmProvenanceTrustedKey(candidate.Key, candidate.Master, ComputeExpiry(candidate.Key, candidate.Master)));
        }

        if (trusted.Count == 0)
            throw new ArgumentException(
                "No usable OpenPGP signing key found in the supplied key material; revoked, expired, and non-signing keys cannot be trusted signers.",
                paramName);

        return trusted;
    }

    /// <summary>OpenPGP policy check: may this key produce a trusted signature at all?</summary>
    private static bool QualifiesAsSigningKey(PgpPublicKey key, PgpPublicKey? master)
    {
        // A mathematical signature from a revoked or expired key, or from material that
        // is not authorized to sign (encryption-only subkeys), must never be trusted.
        if (key.HasRevocation() || (master is not null && master.HasRevocation()))
            return false;
        if (!key.IsMasterKey && (master is null || !master.IsMasterKey))
            return false;
        if (!AlgorithmCanSign(key.Algorithm))
            return false;
        if (IsExpiredAt(ComputeEffectiveExpiry(key, master), DateTime.UtcNow))
            return false;
        return KeyFlagsPermitSigning(key, master);
    }

    /// <summary>
    /// Algorithms whose keys can produce OpenPGP signatures. Encryption-only algorithms
    /// (ElGamal, ECDH, RSA encrypt-only, X25519/X448) are excluded so an encryption
    /// subkey cannot be mistaken for a signer even without key flags.
    /// </summary>
    private static bool AlgorithmCanSign(PublicKeyAlgorithmTag algorithm) => algorithm switch
    {
        PublicKeyAlgorithmTag.RsaGeneral or
        PublicKeyAlgorithmTag.RsaSign or
        PublicKeyAlgorithmTag.Dsa or
        PublicKeyAlgorithmTag.ECDsa or
        PublicKeyAlgorithmTag.EdDsa_Legacy or
        PublicKeyAlgorithmTag.Ed25519 or
        PublicKeyAlgorithmTag.Ed448 => true,
        _ => false,
    };

    /// <summary>
    /// Whether the governing key-flags subpacket authorizes data signing.
    /// Flags are asserted by self-signatures (user-ID certifications on a primary key,
    /// binding signatures on a subkey); the most recent assertion wins. Keys without a
    /// key-flags subpacket keep their historical unrestricted usage, gated only by
    /// <see cref="AlgorithmCanSign"/>.
    /// </summary>
    private static bool KeyFlagsPermitSigning(PgpPublicKey key, PgpPublicKey? master)
    {
        // Third-party certifications must not widen what a key may do, so a primary key
        // only trusts key flags it asserted itself; a subkey is governed by its binding
        // signature. A subkey is governed only by a cryptographically verified binding
        // signature issued by its primary key; raw packet metadata is attacker-controlled.
        var assertions = key.IsMasterKey
            ? key.GetSignatures().Where(s => s.KeyId == key.KeyId)
            : master is null
                ? Enumerable.Empty<PgpSignature>()
                : key.GetKeySignatures().Where(s => s.KeyId == master.KeyId
                    && s.SignatureType == PgpSignature.SubkeyBinding
                    && HasValidSubkeyBinding(s, master, key));

        PgpSignature? governing = null;
        var hasValidatedAssertion = false;
        foreach (var sig in assertions)
        {
            if (key.IsMasterKey && sig.SignatureType is not (PgpSignature.PositiveCertification
                    or PgpSignature.CasualCertification or PgpSignature.NoCertification
                    or PgpSignature.DefaultCertification))
                continue;
            if (key.IsMasterKey && !HasValidSelfCertification(sig, key))
                continue;
            hasValidatedAssertion = true;
            var hashed = sig.GetHashedSubPackets();
            if (hashed is null || !hashed.HasSubpacket(SignatureSubpacketTag.KeyFlags))
                continue;
            if (governing is null || sig.CreationTime > governing.CreationTime)
                governing = sig;
        }

        if (governing is null)
            return hasValidatedAssertion;

        var flags = governing.GetHashedSubPackets()!.GetKeyFlags();
        return (flags & PgpKeyFlags.CanSign) != 0;
    }

    private static bool HasValidSubkeyBinding(PgpSignature signature, PgpPublicKey master, PgpPublicKey subkey)
    {
        try
        {
            signature.InitVerify(master);
            return signature.VerifyCertification(master, subkey);
        }
        catch (PgpException)
        {
            return false;
        }
    }

    private static bool HasValidSelfCertification(PgpSignature signature, PgpPublicKey key)
    {
        foreach (var userId in key.GetUserIds())
        {
            try
            {
                signature.InitVerify(key);
                if (signature.VerifyCertification(userId, key))
                    return true;
            }
            catch (PgpException)
            {
                // Malformed or forged self-signatures do not authorize key usage.
            }
        }

        return false;
    }

    /// <summary>
    /// Absolute expiry of the key, or null when it never expires. The master-key overload
    /// of <c>GetValidSeconds</c> verifies a subkey's binding signature before trusting
    /// its expiration, so a forged binding packet cannot extend a subkey's lifetime.
    /// </summary>
    private static DateTime? ComputeExpiry(PgpPublicKey key, PgpPublicKey? master)
    {
        // Zero means the key never expires (RFC 4880 §5.2.3.3).
        long validSeconds = master is null ? key.GetValidSeconds() : key.GetValidSeconds(master);
        return validSeconds <= 0 ? null : key.CreationTime.AddSeconds(validSeconds);
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

    private static bool IsExpiredAt(DateTime? expiresAt, DateTime at)
        => expiresAt is not null && at >= expiresAt.Value;

    private bool IsExpiredAt(DateTime at) => IsExpiredAt(_expiresAt, at);

    private bool HasRevocation() => _publicKey.HasRevocation();

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

        // De-duplicate by key ID; keyrings can repeat the primary key across packets.
        return result
            .GroupBy(c => c.Key.KeyId)
            .Select(g => g.First())
            .ToList();
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
        foreach (var userId in key.GetUserIds())
            return userId;
        return null;
    }
}
