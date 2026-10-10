using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace HelmSharp.Action;

/// <summary>
/// A trusted OpenPGP public key used to authenticate <c>.prov</c> signatures.
/// Verification fails closed unless the signer matches one of the supplied trusted keys.
/// </summary>
public sealed class HelmProvenanceTrustedKey
{
    private readonly PgpPublicKey _publicKey;

    private HelmProvenanceTrustedKey(PgpPublicKey publicKey)
    {
        _publicKey = publicKey;
    }

    /// <summary>Hex-encoded OpenPGP V4 key fingerprint (40 hex characters).</summary>
    public string Fingerprint => Convert.ToHexString(_publicKey.GetFingerprint()).ToLowerInvariant();

    /// <summary>Hex-encoded 64-bit OpenPGP key ID.</summary>
    public string KeyId => _publicKey.KeyId.ToString("x16");

    /// <summary>Primary user ID on the key, when present.</summary>
    public string? UserId => GetUserId(_publicKey);

    internal PgpPublicKey PublicKey => _publicKey;

    /// <summary>
    /// Wraps binary or ASCII-armored OpenPGP public-key material (a single public key
    /// or a public keyring).
    /// </summary>
    /// <param name="publicKeyData">Encoded public key bytes.</param>
    /// <returns>Trusted key wrapper.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="publicKeyData"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="publicKeyData"/> is empty or contains no public key.</exception>
    public static HelmProvenanceTrustedKey FromPublicKeyData(byte[] publicKeyData)
    {
        var keys = ReadPublicKeys(publicKeyData);
        if (keys.Count == 0)
            throw new ArgumentException("No OpenPGP public key found in the supplied key material.", nameof(publicKeyData));

        return new HelmProvenanceTrustedKey(keys[0]);
    }

    /// <summary>
    /// Loads binary or ASCII-armored OpenPGP public-key material from a file.
    /// When the file is a keyring containing several keys, the first is returned;
    /// use <see cref="FromKeyringFile"/> to load every key.
    /// </summary>
    /// <param name="path">Path to a public key or keyring file.</param>
    /// <returns>Trusted key wrapper.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static HelmProvenanceTrustedKey FromPublicKeyFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Public key file not found: {path}", path);

        return FromPublicKeyData(File.ReadAllBytes(path));
    }

    /// <summary>
    /// Loads every OpenPGP public key from a keyring file (binary or ASCII-armored).
    /// Secret keyrings are also accepted; only their public halves are retained.
    /// </summary>
    /// <param name="path">Path to a keyring file.</param>
    /// <returns>One wrapper per public key found.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="ArgumentException">The file contains no public keys.</exception>
    public static IReadOnlyList<HelmProvenanceTrustedKey> FromKeyringFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Keyring file not found: {path}", path);

        var keys = ReadPublicKeys(File.ReadAllBytes(path));
        if (keys.Count == 0)
            throw new ArgumentException($"No OpenPGP public keys found in keyring: {path}", nameof(path));

        return keys.Select(k => new HelmProvenanceTrustedKey(k)).ToList();
    }

    /// <summary>
    /// Loads every OpenPGP public key from keyring bytes (binary or ASCII-armored).
    /// </summary>
    /// <param name="keyringData">Encoded keyring bytes.</param>
    /// <returns>One wrapper per public key found.</returns>
    public static IReadOnlyList<HelmProvenanceTrustedKey> FromKeyringData(byte[] keyringData)
    {
        var keys = ReadPublicKeys(keyringData);
        return keys.Select(k => new HelmProvenanceTrustedKey(k)).ToList();
    }

    private static List<PgpPublicKey> ReadPublicKeys(byte[] data)
    {
        var result = new List<PgpPublicKey>();
        foreach (var obj in ReadPgpObjects(data))
        {
            switch (obj)
            {
                case PgpPublicKeyRing ring:
                    result.AddRange(ring.GetPublicKeys());
                    break;
                case PgpPublicKey key:
                    result.Add(key);
                    break;
                case PgpSecretKeyRing secretRing:
                    foreach (var secret in secretRing.GetSecretKeys())
                        result.Add(secret.PublicKey);
                    break;
                case PgpSecretKey secretKey:
                    result.Add(secretKey.PublicKey);
                    break;
            }
        }

        // De-duplicate by key ID; keyrings can repeat the primary key across packets.
        return result
            .GroupBy(k => k.KeyId)
            .Select(g => g.First())
            .ToList();
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

            if (next is null or PgpMarker)
                break;
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
