using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Security;

namespace HelmSharp.Action;

/// <summary>
/// OpenPGP private-key material used to produce authenticating <c>.prov</c> signatures.
/// Holds key bytes and an optional passphrase in memory only; never logs or persists secrets.
/// </summary>
public sealed class HelmProvenanceSigningKey : IDisposable
{
    private readonly byte[] _secretKeyData;
    private char[]? _passphrase;
    private bool _disposed;

    private HelmProvenanceSigningKey(byte[] secretKeyData, char[]? passphrase)
    {
        _secretKeyData = secretKeyData;
        _passphrase = passphrase;
    }

    /// <summary>
    /// Wraps binary or ASCII-armored OpenPGP secret-key material (a single secret key,
    /// secret key ring, or GPG secring export).
    /// </summary>
    /// <param name="secretKeyData">Encoded secret key bytes; copied immediately.</param>
    /// <param name="passphrase">Optional passphrase protecting the secret key. The caller retains ownership of the buffer; this type copies it and clears its copy on dispose.</param>
    /// <returns>Signing key wrapper.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="secretKeyData"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="secretKeyData"/> is empty.</exception>
    /// <exception cref="PgpException">The data does not contain a usable secret key.</exception>
    public static HelmProvenanceSigningKey FromSecretKeyData(byte[] secretKeyData, char[]? passphrase = null)
    {
        ArgumentNullException.ThrowIfNull(secretKeyData);
        if (secretKeyData.Length == 0)
            throw new ArgumentException("Secret key data must not be empty.", nameof(secretKeyData));

        // Validate parseability up front so callers get a clear error at construction.
        _ = LoadSecretKey(secretKeyData, passphrase);
        return new HelmProvenanceSigningKey((byte[])secretKeyData.Clone(), passphrase is null ? null : (char[])passphrase.Clone());
    }

    /// <summary>
    /// Loads binary or ASCII-armored OpenPGP secret-key material from a file.
    /// </summary>
    /// <param name="path">Path to a secret key or keyring file.</param>
    /// <param name="passphrase">Optional passphrase protecting the secret key.</param>
    /// <returns>Signing key wrapper.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static HelmProvenanceSigningKey FromSecretKeyFile(string path, char[]? passphrase = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Secret key file not found: {path}", path);

        return FromSecretKeyData(File.ReadAllBytes(path), passphrase);
    }

    /// <summary>Loads and unlocks the secret key for signing.</summary>
    internal PgpSecretKey GetSecretKey()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return LoadSecretKey(_secretKeyData, _passphrase);
    }

    /// <summary>Passphrase buffer for unlocking the secret key during signing; null when unprotected.</summary>
    internal char[]? Passphrase
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _passphrase;
        }
    }

    private static PgpSecretKey LoadSecretKey(byte[] data, char[]? passphrase)
    {
        var keys = ReadSecretKeys(data);
        if (keys.Count == 0)
            throw new PgpException("No OpenPGP secret key found in the supplied key material.");

        foreach (var key in keys)
        {
            // ExtractPrivateKey returns null for passphrase-protected keys when the
            // passphrase is missing or wrong; an unencrypted key ignores the passphrase.
            try
            {
                if (key.ExtractPrivateKey(passphrase) is not null)
                    return key;
            }
            catch (PgpException)
            {
                // Wrong passphrase or corrupt key material; try the next candidate.
            }
        }

        throw new PgpException(
            passphrase is null
                ? "Secret key is passphrase-protected; a passphrase is required."
                : "Secret key could not be unlocked with the supplied passphrase.");
    }

    private static List<PgpSecretKey> ReadSecretKeys(byte[] data)
    {
        var result = new List<PgpSecretKey>();
        foreach (var obj in ReadPgpObjects(data))
        {
            switch (obj)
            {
                case PgpSecretKeyRing ring:
                    result.AddRange(ring.GetSecretKeys());
                    break;
                case PgpSecretKey key:
                    result.Add(key);
                    break;
                case PgpPublicKeyRing pubRing:
                    // Public-only material is never a valid signing key.
                    break;
            }
        }

        return result;
    }

    private static IEnumerable<object> ReadPgpObjects(byte[] data)
    {
        Stream input = new MemoryStream(data, writable: false);
        try
        {
            // Armored input is text; binary keyrings are raw packets. Detect armor by header.
            if (data.Length >= 27 && data[0] == (byte)'-' && data[1] == (byte)'-')
            {
                var armored = new ArmoredInputStream(input);
                var factory = new PgpObjectFactory(armored);
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

                    if (next is null or PgpMarker)
                        break;
                    yield return next;
                }
            }
            else
            {
                var factory = new PgpObjectFactory(input);
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

                    if (next is null or PgpMarker)
                        break;
                    yield return next;
                }
            }
        }
        finally
        {
            // MemoryStream over a caller-owned buffer: nothing to release beyond the wrapper.
        }
    }

    /// <summary>Zeroes the in-memory passphrase copy.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_passphrase is not null)
        {
            Array.Clear(_passphrase);
            _passphrase = null;
        }
    }
}
