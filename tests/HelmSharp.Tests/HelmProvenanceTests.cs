using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using HelmSharp.Action;

namespace HelmSharp.Tests;

/// <summary>
/// Authenticating provenance tests: OpenPGP sign/verify round-trips, tamper rejection,
/// legacy pseudo-signature rejection, and Helm CLI interoperability. All key material is
/// ephemeral and generated in-memory or in temp directories.
/// </summary>
public class HelmProvenanceTests
{
    // --- Ephemeral key fixtures ---

    private static (PgpSecretKeyRing SecretRing, PgpPublicKeyRing PublicRing, HelmProvenanceSigningKey SigningKey, HelmProvenanceTrustedKey TrustedKey) CreateTestKeys(
        string userId = "HelmSharp Test <test@example.com>",
        char[]? passphrase = null,
        long? validitySeconds = null,
        DateTime? createdAt = null)
    {
        var kpg = new RsaKeyPairGenerator();
        kpg.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var kp = kpg.GenerateKeyPair();

        var hashed = new PgpSignatureSubpacketGenerator();
        hashed.SetKeyFlags(true, PgpKeyFlags.CanSign | PgpKeyFlags.CanCertify);
        hashed.SetPreferredSymmetricAlgorithms(true, [(int)SymmetricKeyAlgorithmTag.Aes256]);
        hashed.SetPreferredHashAlgorithms(true, [(int)HashAlgorithmTag.Sha512, (int)HashAlgorithmTag.Sha256]);
        if (validitySeconds is not null)
            hashed.SetKeyExpirationTime(false, validitySeconds.Value);

        var keyPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, kp, createdAt ?? DateTime.UtcNow);
        var encAlgo = passphrase is null ? SymmetricKeyAlgorithmTag.Null : SymmetricKeyAlgorithmTag.Aes256;
        var krg = new PgpKeyRingGenerator(
            (int)PgpSignature.PositiveCertification,
            keyPair,
            userId,
            encAlgo,
            true,
            passphrase ?? (char[])null!,
            false,
            hashed.Generate(),
            null,
            new SecureRandom());

        var secretRing = krg.GenerateSecretKeyRing();
        var publicRing = krg.GeneratePublicKeyRing();

        byte[] secretBytes;
        using (var ms = new MemoryStream())
        {
            secretRing.Encode(ms);
            secretBytes = ms.ToArray();
        }

        byte[] publicBytes;
        using (var ms = new MemoryStream())
        {
            publicRing.Encode(ms);
            publicBytes = ms.ToArray();
        }

        var signingKey = HelmProvenanceSigningKey.FromSecretKeyData(secretBytes, passphrase);
        var trustedKey = HelmProvenanceTrustedKey.FromPublicKeyData(publicBytes);
        return (secretRing, publicRing, signingKey, trustedKey);
    }

    /// <summary>
    /// Primary key authorized to sign, plus an encryption-only subkey. The subkey's
    /// private half is re-wrapped as a standalone secret key so tests can craft
    /// signatures that are mathematically valid under the subkey but not authorized by
    /// OpenPGP key flags (the attack under test).
    /// </summary>
    private static (PgpSecretKeyRing SecretRing, PgpPublicKeyRing PublicRing, byte[] EncryptionSubkeySecret) CreateRingWithEncryptionSubkey(
        string userId = "Primary <primary@example.com>")
    {
        var kpg = new RsaKeyPairGenerator();
        kpg.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var createdAt = DateTime.UtcNow;
        var primaryPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, kpg.GenerateKeyPair(), createdAt);

        var primaryHashed = new PgpSignatureSubpacketGenerator();
        primaryHashed.SetKeyFlags(true, PgpKeyFlags.CanSign | PgpKeyFlags.CanCertify);
        var krg = new PgpKeyRingGenerator(
            (int)PgpSignature.PositiveCertification,
            primaryPair,
            userId,
            SymmetricKeyAlgorithmTag.Null,
            (char[])null!,
            false,
            primaryHashed.Generate(),
            null,
            new SecureRandom());

        // Encryption-only subkey: the key flags deny signing, which OpenPGP policy must
        // honor even though the RSA key material can produce a mathematical signature.
        var subKp = kpg.GenerateKeyPair();
        var encPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, subKp, createdAt);
        var encHashed = new PgpSignatureSubpacketGenerator();
        encHashed.SetKeyFlags(true, PgpKeyFlags.CanEncryptCommunications | PgpKeyFlags.CanEncryptStorage);
        krg.AddSubKey(encPair, encHashed.Generate(), null);

        var secretRing = krg.GenerateSecretKeyRing();
        var publicRing = krg.GeneratePublicKeyRing();

        // A bare subkey secret encoding does not parse as a secret key ring, so the
        // subkey's private material is re-wrapped as a standalone secret key. The RSA
        // key pair (and creation time) is identical, so the resulting signature is
        // mathematically valid under the encryption-only subkey in the ring above.
        var attackPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, subKp, createdAt);
        var attackSecret = new PgpSecretKey(
            (int)PgpSignature.PositiveCertification,
            attackPair,
            "attacker",
            SymmetricKeyAlgorithmTag.Null,
            (char[])null!,
            false,
            encHashed.Generate(),
            null,
            new SecureRandom());

        byte[] attackSecretBytes;
        using (var ms = new MemoryStream())
        {
            attackSecret.Encode(ms);
            attackSecretBytes = ms.ToArray();
        }

        return (secretRing, publicRing, attackSecretBytes);
    }

    private static (PgpSecretKeyRing SecretRing, PgpPublicKeyRing PublicRing) CreateRingWithSigningSubkey(
        string userId = "Signing subkey <subkey@example.com>",
        bool expiredPrimary = false)
    {
        var kpg = new RsaKeyPairGenerator();
        kpg.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var createdAt = expiredPrimary ? DateTime.UtcNow.AddSeconds(-10) : DateTime.UtcNow;
        var primaryPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, kpg.GenerateKeyPair(), createdAt);
        var primaryFlags = new PgpSignatureSubpacketGenerator();
        primaryFlags.SetKeyFlags(true, PgpKeyFlags.CanCertify);
        if (expiredPrimary)
            primaryFlags.SetKeyExpirationTime(false, 1);
        var keyRingGenerator = new PgpKeyRingGenerator(
            (int)PgpSignature.PositiveCertification,
            primaryPair,
            userId,
            SymmetricKeyAlgorithmTag.Null,
            (char[])null!,
            false,
            primaryFlags.Generate(),
            null,
            new SecureRandom());

        var signingPair = new PgpKeyPair(
            PublicKeyAlgorithmTag.RsaGeneral,
            kpg.GenerateKeyPair(),
            createdAt);
        var signingFlags = new PgpSignatureSubpacketGenerator();
        signingFlags.SetKeyFlags(true, PgpKeyFlags.CanSign);
        if (expiredPrimary)
            signingFlags.SetKeyExpirationTime(false, 3600);
        keyRingGenerator.AddSubKey(signingPair, signingFlags.Generate(), null);
        return (keyRingGenerator.GenerateSecretKeyRing(), keyRingGenerator.GeneratePublicKeyRing());
    }

    /// <summary>Binary public keyring containing a single signing key that is revoked.</summary>
    private static byte[] CreateRevokedSigningKeyring(string userId = "Revoked <revoked@example.com>")
    {
        var kpg = new RsaKeyPairGenerator();
        kpg.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, kpg.GenerateKeyPair(), DateTime.UtcNow);
        var hashed = new PgpSignatureSubpacketGenerator();
        hashed.SetKeyFlags(true, PgpKeyFlags.CanSign | PgpKeyFlags.CanCertify);
        var krg = new PgpKeyRingGenerator(
            (int)PgpSignature.PositiveCertification,
            pair,
            userId,
            SymmetricKeyAlgorithmTag.Null,
            (char[])null!,
            false,
            hashed.Generate(),
            null,
            new SecureRandom());
        var secretRing = krg.GenerateSecretKeyRing();
        var secret = secretRing.GetSecretKeys().First();
        var privateKey = secret.ExtractPrivateKey(null)!;
        var publicKeyToRevoke = secret.PublicKey;

        var revGen = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        revGen.InitSign(PgpSignature.KeyRevocation, privateKey);
        var revSub = new PgpSignatureSubpacketGenerator();
        revSub.SetSignatureCreationTime(false, DateTime.UtcNow);
        revSub.SetRevocationReason(false, RevocationReasonTag.KeySuperseded, "test revocation");
        revGen.SetHashedSubpackets(revSub.Generate());
        var revoked = PgpPublicKey.AddCertification(publicKeyToRevoke, revGen.GenerateCertification(publicKeyToRevoke));

        using var ms = new MemoryStream();
        // Bare key encoding parses back as a single-key ring.
        revoked.Encode(ms);
        return ms.ToArray();
    }

    /// <summary>Binary public keyring containing a single signing key that is already expired.</summary>
    private static byte[] CreateExpiredSigningKeyring(string userId = "Expired <expired@example.com>")
    {
        var kpg = new RsaKeyPairGenerator();
        kpg.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        // Created 10 seconds ago with 1 second of validity: expired before import.
        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, kpg.GenerateKeyPair(), DateTime.UtcNow.AddSeconds(-10));
        var hashed = new PgpSignatureSubpacketGenerator();
        hashed.SetKeyFlags(true, PgpKeyFlags.CanSign | PgpKeyFlags.CanCertify);
        hashed.SetKeyExpirationTime(false, 1L);
        var krg = new PgpKeyRingGenerator(
            (int)PgpSignature.PositiveCertification,
            pair,
            userId,
            SymmetricKeyAlgorithmTag.Null,
            (char[])null!,
            false,
            hashed.Generate(),
            null,
            new SecureRandom());

        using var ms = new MemoryStream();
        krg.GeneratePublicKeyRing().Encode(ms);
        return ms.ToArray();
    }

    private static async Task<string> CreateTestChartArchiveAsync(string workDir, string? chartYaml = null)
        => await CreateChartArchiveAsync(workDir, "provchart", chartYaml ?? DefaultChartYaml);

    /// <summary>Chart metadata whose serialization is free of dash-leading lines and non-ASCII.</summary>
    private static string DefaultChartYaml => """
        apiVersion: v2
        name: provchart
        description: provenance test chart
        type: application
        version: 0.1.0
        appVersion: "1.0"
        """;

    /// <summary>
    /// Chart metadata whose YAML serialization contains sequence items such as
    /// <c>- name: Alice</c>, which are dash-leading cleartext lines.
    /// </summary>
    private static string ChartYamlWithMaintainers(string chartName) => $$"""
        apiVersion: v2
        name: {{chartName}}
        description: provenance test chart
        type: application
        version: 0.1.0
        appVersion: "1.0"
        maintainers:
        - name: Alice
          email: alice@example.com
        - name: Bob
        """;

    /// <summary>Chart metadata with non-ASCII description and maintainer names (UTF-8).</summary>
    private static string ChartYamlWithNonAscii(string chartName) => $$"""
        apiVersion: v2
        name: {{chartName}}
        description: 中文描述 émojis — ünïcode
        type: application
        version: 0.1.0
        appVersion: "1.0"
        maintainers:
        - name: 名字 Test
          email: zh@example.com
        """;

    private static async Task<string> CreateChartArchiveAsync(string workDir, string chartName, string chartYaml)
    {
        var chartDir = CreateChartDirectory(workDir, chartName, chartYaml);
        var tgzPath = Path.Combine(workDir, chartName + "-0.1.0.tgz");
        await PackChartAsync(chartDir, tgzPath);
        return tgzPath;
    }

    private static string CreateChartDirectory(string workDir, string chartName, string chartYaml)
    {
        var chartDir = Path.Combine(workDir, chartName);
        Directory.CreateDirectory(Path.Combine(chartDir, "templates"));
        File.WriteAllText(Path.Combine(chartDir, "Chart.yaml"), chartYaml);
        File.WriteAllText(Path.Combine(chartDir, "values.yaml"), "replicaCount: 1\n");
        File.WriteAllText(Path.Combine(chartDir, "templates", "cm.yaml"), $"""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: {chartName}-cm
            data:
              hello: world
            """);
        return chartDir;
    }

    private static async Task PackChartAsync(string chartDir, string tgzPath)
    {
        // Minimal gzipped tar matching Helm's chart archive layout (chartname/ prefix).
        var chartName = Path.GetFileName(chartDir);
        await using var fileStream = File.Create(tgzPath);
        await using var gzip = new System.IO.Compression.GZipStream(fileStream, System.IO.Compression.CompressionLevel.Optimal);
        using var tar = new System.Formats.Tar.TarWriter(gzip);
        foreach (var file in Directory.EnumerateFiles(chartDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(chartDir, file).Replace('\\', '/');
            var entry = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, $"{chartName}/{rel}")
            {
                DataStream = new MemoryStream(await File.ReadAllBytesAsync(file))
            };
            await tar.WriteEntryAsync(entry);
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "helmsharp-prov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // --- Success path ---

    [Fact]
    public async Task SignAndVerify_RoundTrip_Succeeds()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, trustedKey) = CreateTestKeys();

            var provPath = await HelmProvenance.SignAsync(tgz, signingKey);
            Assert.True(File.Exists(provPath));
            Assert.Equal(tgz + ".prov", provPath);

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.True(result.IsValid, result.FailureReason);
            Assert.True(result.SignatureValid);
            Assert.True(result.SignerTrusted);
            Assert.True(result.DigestMatches);
            Assert.Equal(trustedKey.Fingerprint, result.SignerFingerprint);
            Assert.Equal(trustedKey.KeyId, result.SignerKeyId);
            Assert.NotNull(result.ExpectedSha256);
            Assert.Equal(result.ExpectedSha256, result.ActualSha256);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task SignAndVerify_PassphraseProtectedKey_Succeeds()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var passphrase = "sécret-秘密".ToCharArray();
            var (_, _, signingKey, trustedKey) = CreateTestKeys(passphrase: passphrase);

            var provPath = await HelmProvenance.SignAsync(tgz, signingKey);
            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.True(result.IsValid, result.FailureReason);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void FromKeyringData_ExpiredPrimaryRejectsOtherwiseValidSigningSubkey()
    {
        var (_, publicRing) = CreateRingWithSigningSubkey(expiredPrimary: true);
        using var ms = new MemoryStream();
        publicRing.Encode(ms);

        var exception = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(ms.ToArray()));

        Assert.Contains("No usable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromKeyringData_SigningSubkeyUsesPrimaryUserId()
    {
        const string userId = "Signing subkey owner <owner@example.com>";
        var (_, publicRing) = CreateRingWithSigningSubkey(userId);
        using var ms = new MemoryStream();
        publicRing.Encode(ms);

        var trustedKey = Assert.Single(HelmProvenanceTrustedKey.FromKeyringData(ms.ToArray()));

        Assert.Equal(userId, trustedKey.UserId);
    }

    [Fact]
    public void FromKeyringData_MarkerBeforeKeyringIsSkipped()
    {
        var (_, publicRing) = CreateRingWithSigningSubkey();
        using var ring = new MemoryStream();
        publicRing.Encode(ring);

        var trustedKey = Assert.Single(HelmProvenanceTrustedKey.FromKeyringData(PrependMarkerPacket(ring.ToArray())));

        Assert.NotNull(trustedKey.UserId);
    }

    [Fact]
    public void FromSecretKeyData_MarkerBeforeKeyringIsSkipped()
    {
        var (secretRing, _) = CreateRingWithSigningSubkey();
        using var ring = new MemoryStream();
        secretRing.Encode(ring);

        using var signingKey = HelmProvenanceSigningKey.FromSecretKeyData(PrependMarkerPacket(ring.ToArray()));

        Assert.NotNull(signingKey.GetSecretKey());
    }

    private static byte[] PrependMarkerPacket(byte[] encoded)
    {
        // OpenPGP marker packets use tag 10 and carry the three literal bytes "PGP".
        var marker = new byte[] { 0xCA, 0x03, (byte)'P', (byte)'G', (byte)'P' };
        return [.. marker, .. encoded];
    }

    [Fact]
    public async Task Verify_MissingProvFile_FailsClosed()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, _, trustedKey) = CreateTestKeys();

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.False(result.IsValid);
            Assert.Contains("not found", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_EmptyTrustedKeys_Throws()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            await Assert.ThrowsAsync<ArgumentException>(() => HelmProvenance.VerifyAsync(tgz, []));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // --- Tamper cases ---

    [Fact]
    public async Task Verify_TamperedChartBytes_Fails()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, trustedKey) = CreateTestKeys();
            await HelmProvenance.SignAsync(tgz, signingKey);

            // Flip a byte in the archive after signing.
            var bytes = await File.ReadAllBytesAsync(tgz);
            bytes[^1] ^= 0xFF;
            await File.WriteAllBytesAsync(tgz, bytes);

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.False(result.IsValid);
            Assert.True(result.SignatureValid);
            Assert.False(result.DigestMatches);
            Assert.Contains("digest", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_TamperedSignedMetadata_Fails()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, trustedKey) = CreateTestKeys();
            var provPath = await HelmProvenance.SignAsync(tgz, signingKey);

            // Alter a signed metadata field inside the clearsigned body.
            var prov = await File.ReadAllTextAsync(provPath);
            prov = prov.Replace("provenance test chart", "tampered description", StringComparison.Ordinal);
            await File.WriteAllTextAsync(provPath, prov);

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.False(result.IsValid);
            Assert.False(result.SignatureValid);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_TamperedDigestInProv_Fails()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, trustedKey) = CreateTestKeys();
            var provPath = await HelmProvenance.SignAsync(tgz, signingKey);

            // Replace the signed digest with a different valid-looking hash.
            var prov = await File.ReadAllTextAsync(provPath);
            var fakeHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("different"))).ToLowerInvariant();
            var realHash = HelmProvenance.ExtractSha256(prov)!;
            prov = prov.Replace(realHash, fakeHash, StringComparison.Ordinal);
            await File.WriteAllTextAsync(provPath, prov);

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.False(result.IsValid);
            Assert.False(result.SignatureValid);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_MalformedArmor_Fails()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, trustedKey) = CreateTestKeys();
            var provPath = await HelmProvenance.SignAsync(tgz, signingKey);

            var prov = await File.ReadAllTextAsync(provPath);
            prov = prov.Replace("-----BEGIN PGP SIGNATURE-----", "-----BEGIN BROKEN-----", StringComparison.Ordinal);
            await File.WriteAllTextAsync(provPath, prov);

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.False(result.IsValid);
            Assert.Contains("armor", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_UnknownKey_Fails()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, _) = CreateTestKeys(userId: "Signer <signer@example.com>");
            await HelmProvenance.SignAsync(tgz, signingKey);

            // Trust a different key than the one that signed.
            var (_, _, _, otherTrusted) = CreateTestKeys(userId: "Other <other@example.com>");
            var result = await HelmProvenance.VerifyAsync(tgz, [otherTrusted]);
            Assert.False(result.IsValid);
            Assert.False(result.SignerTrusted);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_SignatureFromDifferentKey_Fails()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, keyA, _) = CreateTestKeys(userId: "A <a@example.com>");
            var (_, _, _, trustedB) = CreateTestKeys(userId: "B <b@example.com>");

            await HelmProvenance.SignAsync(tgz, keyA);
            var result = await HelmProvenance.VerifyAsync(tgz, [trustedB]);
            Assert.False(result.IsValid);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // --- Trusted-key policy: only valid signing keys may be trusted ---

    [Fact]
    public void FromKeyringData_PrimaryWithEncryptionOnlySubkey_KeepsPrimaryOnly()
    {
        var (_, publicRing, _) = CreateRingWithEncryptionSubkey();
        byte[] publicBytes;
        using (var ms = new MemoryStream())
        {
            publicRing.Encode(ms);
            publicBytes = ms.ToArray();
        }

        var trusted = HelmProvenanceTrustedKey.FromKeyringData(publicBytes);
        // The encryption-only subkey must not become a trusted signer alongside the primary.
        Assert.Single(trusted);
        Assert.Equal(
            Convert.ToHexString(publicRing.GetPublicKeys().First().GetFingerprint()).ToLowerInvariant(),
            trusted[0].Fingerprint);
    }

    [Fact]
    public void FromKeyringData_EncryptionOnlySubkeyAlone_IsRejected()
    {
        var (_, publicRing, _) = CreateRingWithEncryptionSubkey();
        var encryptionSubkey = publicRing.GetPublicKeys().Last();

        byte[] subkeyBytes;
        using (var ms = new MemoryStream())
        {
            // Bare key encoding parses back as a single-key ring.
            encryptionSubkey.Encode(ms);
            subkeyBytes = ms.ToArray();
        }

        // Possession of an encryption subkey must never produce a trusted signer.
        var ex = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(subkeyBytes));
        Assert.Contains("signing key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromKeyringData_CertificationOnlyPrimary_IsRejected()
    {
        var (secretRing, _) = CreateRingWithSigningSubkey();
        using var ms = new MemoryStream();
        secretRing.GetSecretKeys().First().PublicKey.Encode(ms);

        var ex = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(ms.ToArray()));
        Assert.Contains("signing key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromKeyringData_RevokedPrimary_InvalidatesSigningSubkeys()
    {
        var (secretRing, publicRing) = CreateRingWithSigningSubkey();
        var primarySecret = secretRing.GetSecretKeys().First();
        var primaryPublic = publicRing.GetPublicKeys().First(key => key.IsMasterKey);
        var revocation = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        revocation.InitSign(PgpSignature.KeyRevocation, primarySecret.ExtractPrivateKey(null)!);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetSignatureCreationTime(false, DateTime.UtcNow);
        revocation.SetHashedSubpackets(packets.Generate());
        var revokedPrimary = PgpPublicKey.AddCertification(
            primaryPublic,
            revocation.GenerateCertification(primaryPublic));
        var revokedRing = PgpPublicKeyRing.InsertPublicKey(publicRing, revokedPrimary);

        using var ms = new MemoryStream();
        revokedRing.Encode(ms);
        var ex = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(ms.ToArray()));
        Assert.Contains("signing key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromKeyringData_ForgedSubkeyBinding_CannotAuthorizeSigning()
    {
        var (_, publicRing, encryptionSecretBytes) = CreateRingWithEncryptionSubkey();
        var master = publicRing.GetPublicKeys().First(key => key.IsMasterKey);
        var encryptionSubkey = publicRing.GetPublicKeys().First(key => !key.IsMasterKey);
        var secretFactory = new PgpObjectFactory(new MemoryStream(encryptionSecretBytes));
        var attackerRing = Assert.IsType<PgpSecretKeyRing>(secretFactory.NextPgpObject());
        var attackerSecret = attackerRing.GetSecretKeys().Single();
        var attackerPrivate = attackerSecret.ExtractPrivateKey(null)!;

        // An attacker who controls the subkey can attach a newer apparent binding that
        // grants CanSign. Its issuer is not the primary, so the keyring importer must
        // ignore the unverified flags even though the packet names the correct subkey.
        var signature = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        signature.InitSign(PgpSignature.SubkeyBinding, attackerPrivate);
        var forgedFlags = new PgpSignatureSubpacketGenerator();
        forgedFlags.SetSignatureCreationTime(false, DateTime.UtcNow.AddMinutes(1));
        forgedFlags.SetKeyFlags(true, PgpKeyFlags.CanSign);
        signature.SetHashedSubpackets(forgedFlags.Generate());
        var forgedSubkey = PgpPublicKey.AddCertification(
            encryptionSubkey,
            signature.GenerateCertification(master, encryptionSubkey));
        var forgedRing = PgpPublicKeyRing.InsertPublicKey(publicRing, forgedSubkey);

        using var ms = new MemoryStream();
        forgedRing.Encode(ms);
        var trusted = HelmProvenanceTrustedKey.FromKeyringData(ms.ToArray());

        Assert.Single(trusted);
        Assert.Equal(Convert.ToHexString(master.GetFingerprint()).ToLowerInvariant(), trusted[0].Fingerprint);
    }

    [Fact]
    public async Task SignAsync_SelectsAuthorizedSigningSubkey()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (secretRing, publicRing) = CreateRingWithSigningSubkey();
            using var secretBytes = new MemoryStream();
            secretRing.Encode(secretBytes);
            using var publicBytes = new MemoryStream();
            publicRing.Encode(publicBytes);
            using var signingKey = HelmProvenanceSigningKey.FromSecretKeyData(secretBytes.ToArray());

            await HelmProvenance.SignAsync(tgz, signingKey);
            var trusted = HelmProvenanceTrustedKey.FromKeyringData(publicBytes.ToArray());
            var result = await HelmProvenance.VerifyAsync(tgz, trusted);

            Assert.True(result.IsValid, result.FailureReason);
            Assert.Equal(
                Convert.ToHexString(publicRing.GetPublicKeys().Last().GetFingerprint()).ToLowerInvariant(),
                result.SignerFingerprint);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void FromSecretKeyData_EncryptionOnlySubkey_IsRejected()
    {
        var (_, _, subSecretBytes) = CreateRingWithEncryptionSubkey();

        // The signing wrapper must not select secret material that its matching public
        // key policy forbids from signing provenance data.
        Assert.Throws<PgpException>(() => HelmProvenanceSigningKey.FromSecretKeyData(subSecretBytes));
    }

    [Fact]
    public void FromKeyringData_RevokedSigningKey_IsRejected()
    {
        var revokedBytes = CreateRevokedSigningKeyring();

        // A revoked key must never become a trusted signer even though its private half
        // can still produce a mathematical signature.
        var ex = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(revokedBytes));
        Assert.Contains("signing key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromKeyringData_ExpiredSigningKey_IsRejected()
    {
        var expiredBytes = CreateExpiredSigningKeyring();

        var ex = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(expiredBytes));
        Assert.Contains("signing key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Verify_ExpiredTrustedKey_FailsClosed()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            // The key is imported while still valid, then expires before verification.
            var (_, _, signingKey, trustedKey) = CreateTestKeys(validitySeconds: 4);
            await HelmProvenance.SignAsync(tgz, signingKey);

            var early = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.True(early.IsValid, early.FailureReason);

            await Task.Delay(TimeSpan.FromSeconds(4.5));

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.False(result.IsValid);
            Assert.Contains("expired", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // --- Legacy pseudo-signature rejection ---

    [Fact]
    public async Task Verify_LegacyPseudoSignature_FailsAuthenticity()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var chartBytes = await File.ReadAllBytesAsync(tgz);
            var sha256 = Convert.ToHexString(SHA256.HashData(chartBytes)).ToLowerInvariant();

            // Recreate the legacy pseudo-provenance format: Base64(SHA512) in the sig block.
            var legacy = new StringBuilder();
            legacy.AppendLine("-----BEGIN PGP SIGNED MESSAGE-----");
            legacy.AppendLine("Hash: SHA256");
            legacy.AppendLine();
            legacy.AppendLine("name: provchart-0.1.0");
            legacy.AppendLine("sha256: " + sha256);
            legacy.AppendLine("generated: 2026-01-01T00:00:00.000000Z");
            legacy.AppendLine("-----BEGIN PGP SIGNATURE-----");
            legacy.AppendLine("comment: HelmSharp managed provenance");
            legacy.AppendLine();
            legacy.AppendLine(Convert.ToBase64String(SHA512.HashData(chartBytes)));
            legacy.AppendLine("-----END PGP SIGNATURE-----");

            var provPath = tgz + ".prov";
            await File.WriteAllTextAsync(provPath, legacy.ToString());

            var (_, _, _, trustedKey) = CreateTestKeys();
            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.False(result.IsValid);
            Assert.False(result.SignatureValid);
            Assert.Contains("Legacy", result.FailureReason, StringComparison.OrdinalIgnoreCase);

            // The digest-only helper still works on legacy files (integrity, not authenticity).
            Assert.True(await HelmProvenance.CheckDigestAsync(tgz));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // --- Cancellation ---

    [Fact]
    public async Task SignAsync_Cancelled_Throws()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, _) = CreateTestKeys();

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => HelmProvenance.SignAsync(tgz, signingKey, cts.Token));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyAsync_Cancelled_Throws()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, trustedKey) = CreateTestKeys();
            await HelmProvenance.SignAsync(tgz, signingKey);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => HelmProvenance.VerifyAsync(tgz, [trustedKey], null, cts.Token));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // --- Non-authenticating helpers ---

    [Fact]
    public async Task CheckDigestAsync_MatchesSignedDigest_ButIsNotAuthenticating()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, _) = CreateTestKeys();
            await HelmProvenance.SignAsync(tgz, signingKey);

            Assert.True(await HelmProvenance.CheckDigestAsync(tgz));

            // A forged .prov with a matching plaintext digest passes CheckDigestAsync
            // but must never be treated as authenticating.
            var chartBytes = await File.ReadAllBytesAsync(tgz);
            var sha256 = Convert.ToHexString(SHA256.HashData(chartBytes)).ToLowerInvariant();
            var forged = $"""
                -----BEGIN PGP SIGNED MESSAGE-----
                Hash: SHA256

                name: provchart-0.1.0
                sha256: {sha256}

                -----BEGIN PGP SIGNATURE-----

                Zm9yZ2Vk
                -----END PGP SIGNATURE-----
                """;
            await File.WriteAllTextAsync(tgz + ".prov", forged);

            Assert.True(await HelmProvenance.CheckDigestAsync(tgz));
            var (_, _, _, trustedKey) = CreateTestKeys();
            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.False(result.IsValid);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void ExtractSha256_ReadsFilesSection()
    {
        var prov = """
            -----BEGIN PGP SIGNED MESSAGE-----
            Hash: SHA512

            name: demo
            version: 1.0.0

            ...
            files:
              demo-1.0.0.tgz: sha256:aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899

            -----BEGIN PGP SIGNATURE-----

            AAAA
            -----END PGP SIGNATURE-----
            """;
        Assert.Equal(
            "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899",
            HelmProvenance.ExtractSha256(prov));
    }

    [Fact]
    public void ExtractMetadata_ParsesChartFieldsAndFiles()
    {
        var prov = """
            -----BEGIN PGP SIGNED MESSAGE-----
            Hash: SHA512

            apiVersion: v2
            appVersion: "1.0"
            name: demo
            version: 1.2.3

            ...
            files:
              demo-1.2.3.tgz: sha256:aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899

            -----BEGIN PGP SIGNATURE-----

            AAAA
            -----END PGP SIGNATURE-----
            """;
        var meta = HelmProvenance.ExtractMetadata(prov);
        Assert.Equal("demo", meta["name"]);
        Assert.Equal("1.2.3", meta["version"]);
        Assert.Equal("1.0", meta["appVersion"]);
        Assert.Equal(
            "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899",
            meta["sha256"]);
    }

    // --- Digest extraction scoped to the files section ---

    [Fact]
    public void ExtractSha256_MetadataDigestAnnotation_DoesNotConfuseLookup()
    {
        var annotationHash = new string('a', 64);
        var archiveHash = new string('b', 64);
        // Chart metadata precedes the files section; an annotation value shaped like a
        // digest must never be returned as the archive digest.
        var prov = $"""
            -----BEGIN PGP SIGNED MESSAGE-----
            Hash: SHA512

            annotations:
              example.com/digest: sha256:{annotationHash}
            name: demo
            version: 1.0.0

            ...
            files:
              demo-1.0.0.tgz: sha256:{archiveHash}

            -----BEGIN PGP SIGNATURE-----

            AAAA
            -----END PGP SIGNATURE-----
            """;

        Assert.Equal(archiveHash, HelmProvenance.ExtractSha256(prov, "demo-1.0.0.tgz"));
        Assert.Equal(archiveHash, HelmProvenance.ExtractSha256(prov));
    }

    [Fact]
    public void ExtractSha256_MultiFileMap_SelectsNamedArchive()
    {
        var hashAlpha = new string('a', 64);
        var hashBeta = new string('b', 64);
        var prov = $"""
            -----BEGIN PGP SIGNED MESSAGE-----
            Hash: SHA512

            name: demo

            ...
            files:
              alpha-1.0.0.tgz: sha256:{hashAlpha}
              beta-2.0.0.tgz: sha256:{hashBeta}

            -----BEGIN PGP SIGNATURE-----

            AAAA
            -----END PGP SIGNATURE-----
            """;

        Assert.Equal(hashAlpha, HelmProvenance.ExtractSha256(prov, "alpha-1.0.0.tgz"));
        Assert.Equal(hashBeta, HelmProvenance.ExtractSha256(prov, "beta-2.0.0.tgz"));
        // Without a filename a multi-entry map is ambiguous; guessing would pick the
        // wrong archive.
        Assert.Null(HelmProvenance.ExtractSha256(prov));
    }

    [Fact]
    public async Task CheckDigestAsync_MetadataDigestAnnotation_UsesArchiveDigest()
    {
        var work = TempDir();
        try
        {
            // An annotation whose value is shaped like sha256:<64 hex> sits in the
            // metadata before the files section.
            var tgz = await CreateTestChartArchiveAsync(work, chartYaml: $$"""
                apiVersion: v2
                name: provchart
                description: provenance test chart
                type: application
                version: 0.1.0
                appVersion: "1.0"
                annotations:
                  example.com/digest: "sha256:{{new string('a', 64)}}"
                """);
            var (_, _, signingKey, _) = CreateTestKeys();
            await HelmProvenance.SignAsync(tgz, signingKey);

            Assert.True(await HelmProvenance.CheckDigestAsync(tgz));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task CheckDigestAsync_MultiFileMap_UsesNamedArchiveEntry()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var actualHash = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(tgz))).ToLowerInvariant();
            var siblingHash = new string('c', 64);

            // The sibling entry carries this archive's digest under the wrong name; the
            // entry keyed by the archive filename carries a different digest.
            var prov = $"""
                -----BEGIN PGP SIGNED MESSAGE-----
                Hash: SHA512

                name: provchart

                ...
                files:
                  sibling-0.0.1.tgz: sha256:{actualHash}
                  {Path.GetFileName(tgz)}: sha256:{siblingHash}

                -----BEGIN PGP SIGNATURE-----

                AAAA
                -----END PGP SIGNATURE-----
                """;
            await File.WriteAllTextAsync(tgz + ".prov", prov);

            Assert.False(await HelmProvenance.CheckDigestAsync(tgz));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task SignAndVerify_MetadataDigestAnnotation_RoundTrips()
    {
        var work = TempDir();
        try
        {
            var annotationHash = new string('a', 64);
            var tgz = await CreateTestChartArchiveAsync(work, chartYaml: $$"""
                apiVersion: v2
                name: provchart
                description: provenance test chart
                type: application
                version: 0.1.0
                appVersion: "1.0"
                annotations:
                  example.com/digest: "sha256:{{annotationHash}}"
                """);
            var (_, _, signingKey, trustedKey) = CreateTestKeys();
            await HelmProvenance.SignAsync(tgz, signingKey);

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.True(result.IsValid, result.FailureReason);

            // The signed digest is the archive digest, not the metadata annotation.
            var actualHash = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(tgz))).ToLowerInvariant();
            Assert.Equal(actualHash, result.ExpectedSha256);
            Assert.NotEqual(annotationHash, result.ExpectedSha256);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // --- Helm CLI interoperability ---

    [HelmCliFact]
    public async Task HelmSignedProv_VerifiesUnderHelmSharp()
    {
        var work = TempDir();
        try
        {
            // Build a minimal chart and sign it with helm itself.
            var chartDir = Path.Combine(work, "helmchart");
            Directory.CreateDirectory(Path.Combine(chartDir, "templates"));
            await File.WriteAllTextAsync(Path.Combine(chartDir, "Chart.yaml"), """
                apiVersion: v2
                name: helmchart
                description: helm interop chart
                type: application
                version: 0.1.0
                appVersion: "1.0"
                """);
            await File.WriteAllTextAsync(Path.Combine(chartDir, "values.yaml"), "x: 1\n");
            await File.WriteAllTextAsync(Path.Combine(chartDir, "templates", "cm.yaml"), """
                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: helmchart-cm
                data:
                  a: b
                """);

            var (secretRing, publicRing, _, trustedKey) = CreateTestKeys(userId: "Helm Interop <interop@example.com>");

            var secretPath = Path.Combine(work, "secret.gpg");
            using (var fs = File.Create(secretPath))
                secretRing.Encode(fs);

            var pubPath = Path.Combine(work, "public.gpg");
            using (var fs = File.Create(pubPath))
                publicRing.Encode(fs);

            var outDir = Path.Combine(work, "out");
            Directory.CreateDirectory(outDir);

            var (exitCode, stdout, stderr) = await RunHelmAsync(
                "package", chartDir, "--destination", outDir,
                "--sign", "--key", "Helm Interop", "--keyring", secretPath);
            Assert.Equal(0, exitCode);

            var tgz = Directory.GetFiles(outDir, "*.tgz").Single();
            Assert.True(File.Exists(tgz + ".prov"));

            // HelmSharp must accept the helm-produced provenance file.
            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.True(result.IsValid, result.FailureReason);
            Assert.True(result.SignatureValid);
            Assert.True(result.SignerTrusted);
            Assert.True(result.DigestMatches);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [HelmCliFact]
    public async Task HelmSharpSignedProv_VerifiesUnderHelm()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (secretRing, publicRing, signingKey, _) = CreateTestKeys(userId: "HelmSharp Signer <signer@example.com>");

            await HelmProvenance.SignAsync(tgz, signingKey);

            // Export the public key in the binary keyring form helm reads.
            var pubPath = Path.Combine(work, "public.gpg");
            using (var fs = File.Create(pubPath))
                publicRing.Encode(fs);

            var (exitCode, stdout, stderr) = await RunHelmAsync("verify", tgz, "--keyring", pubPath);
            Assert.True(exitCode == 0, $"helm verify failed ({exitCode}): {stderr}\n{stdout}");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [HelmCliFact]
    public async Task HelmVerify_RejectsLegacyPseudoSignature()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var chartBytes = await File.ReadAllBytesAsync(tgz);
            var sha256 = Convert.ToHexString(SHA256.HashData(chartBytes)).ToLowerInvariant();

            var legacy = new StringBuilder();
            legacy.AppendLine("-----BEGIN PGP SIGNED MESSAGE-----");
            legacy.AppendLine("Hash: SHA256");
            legacy.AppendLine();
            legacy.AppendLine("name: provchart-0.1.0");
            legacy.AppendLine("sha256: " + sha256);
            legacy.AppendLine("-----BEGIN PGP SIGNATURE-----");
            legacy.AppendLine("comment: HelmSharp managed provenance");
            legacy.AppendLine();
            legacy.AppendLine(Convert.ToBase64String(SHA512.HashData(chartBytes)));
            legacy.AppendLine("-----END PGP SIGNATURE-----");
            await File.WriteAllTextAsync(tgz + ".prov", legacy.ToString());

            var (_, publicRing, _, _) = CreateTestKeys();
            var pubPath = Path.Combine(work, "public.gpg");
            using (var fs = File.Create(pubPath))
                publicRing.Encode(fs);

            var (exitCode, _, _) = await RunHelmAsync("verify", tgz, "--keyring", pubPath);
            Assert.NotEqual(0, exitCode);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // --- Armor checksum (CRC-24) ---

    [HelmCliFact]
    public async Task HelmSignedProv_WithArmorChecksum_VerifiesUnderHelmSharp()
    {
        var work = TempDir();
        try
        {
            // Real helm output first, then completed with the RFC 4880 CRC-24 armor
            // checksum line that GnuPG-standard armor always carries (Helm 4 omits it,
            // which is why a checksum-less interop test alone cannot catch the decoder bug).
            var chartDir = CreateChartDirectory(work, "helmchart", ChartYamlWithMaintainers("helmchart"));
            var (secretRing, publicRing, _, trustedKey) = CreateTestKeys(userId: "Helm Interop <interop@example.com>");
            var secretPath = Path.Combine(work, "secret.gpg");
            using (var fs = File.Create(secretPath))
                secretRing.Encode(fs);

            var outDir = Path.Combine(work, "out");
            Directory.CreateDirectory(outDir);
            var (exitCode, stdout, stderr) = await RunHelmAsync(
                "package", chartDir, "--destination", outDir,
                "--sign", "--key", "Helm Interop", "--keyring", secretPath);
            Assert.Equal(0, exitCode);

            var tgz = Directory.GetFiles(outDir, "*.tgz").Single();
            var provPath = tgz + ".prov";
            var raw = await File.ReadAllTextAsync(provPath);
            Assert.DoesNotContain("\n=", raw, StringComparison.Ordinal); // helm 4 omits the checksum

            var withChecksum = InsertArmorChecksum(raw);
            await File.WriteAllTextAsync(provPath, withChecksum);
            Assert.Contains("\n=", withChecksum, StringComparison.Ordinal);

            // HelmSharp must decode the checksum-bearing armor and verify the signature.
            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.True(result.IsValid, result.FailureReason);
            Assert.True(result.SignatureValid);
            Assert.True(result.DigestMatches);

            // helm must still accept its own signature once the standard checksum is present.
            var pubPath = Path.Combine(work, "public.gpg");
            using (var fs = File.Create(pubPath))
                publicRing.Encode(fs);
            var (verifyExit, verifyOut, verifyErr) = await RunHelmAsync("verify", tgz, "--keyring", pubPath);
            Assert.True(verifyExit == 0, $"helm verify failed ({verifyExit}): {verifyErr}\n{verifyOut}");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task SignAsync_EmitsArmorChecksumMatchingBouncyCastle()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, _) = CreateTestKeys();
            var provPath = await HelmProvenance.SignAsync(tgz, signingKey);

            var prov = await File.ReadAllTextAsync(provPath);
            var checksumLine = prov.Split('\n')
                .Select(l => l.Trim())
                .Single(l => l.StartsWith('='));
            var payload = ExtractSignaturePayload(prov);

            Assert.Equal(BouncyCastleChecksumLine(payload), checksumLine);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_ArmorChecksumMismatch_Fails()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, trustedKey) = CreateTestKeys();
            var provPath = await HelmProvenance.SignAsync(tgz, signingKey);

            // Corrupt the CRC-24 line only; the signature payload itself stays intact.
            var prov = await File.ReadAllTextAsync(provPath);
            var checksumLine = prov.Split('\n')
                .Select(l => l.Trim())
                .Single(l => l.StartsWith('='));
            prov = prov.Replace(checksumLine, "=AAAA", StringComparison.Ordinal);
            await File.WriteAllTextAsync(provPath, prov);

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.False(result.IsValid);
            Assert.Contains("armor", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // --- Dash-escaped cleartext lines ---

    [HelmCliFact]
    public async Task HelmSignedProv_WithDashEscapedLines_VerifiesUnderHelmSharp()
    {
        var work = TempDir();
        try
        {
            // helm dash-escapes YAML sequence items ("- name: Alice" -> "- - name: Alice");
            // HelmSharp must unescape before hashing or the signature check fails.
            var chartDir = CreateChartDirectory(work, "helmchart", ChartYamlWithMaintainers("helmchart"));
            var (secretRing, _, _, trustedKey) = CreateTestKeys(userId: "Helm Interop <interop@example.com>");
            var secretPath = Path.Combine(work, "secret.gpg");
            using (var fs = File.Create(secretPath))
                secretRing.Encode(fs);

            var outDir = Path.Combine(work, "out");
            Directory.CreateDirectory(outDir);
            var (exitCode, _, stderr) = await RunHelmAsync(
                "package", chartDir, "--destination", outDir,
                "--sign", "--key", "Helm Interop", "--keyring", secretPath);
            Assert.Equal(0, exitCode);

            var tgz = Directory.GetFiles(outDir, "*.tgz").Single();
            var prov = await File.ReadAllTextAsync(tgz + ".prov");
            // Sequence items are dash-escaped; the nested "name:" line stays indented.
            Assert.Contains("\n- - email: alice@example.com", prov, StringComparison.Ordinal);
            Assert.Contains("\n- - name: Bob", prov, StringComparison.Ordinal);
            Assert.DoesNotContain("\n- email: alice@example.com", prov, StringComparison.Ordinal);

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.True(result.IsValid, result.FailureReason);
            Assert.True(result.SignatureValid);
            Assert.True(result.DigestMatches);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [HelmCliFact]
    public async Task SignAsync_YamlSequenceLines_DashEscapedAndVerifiedUnderHelm()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateChartArchiveAsync(work, "provchart", ChartYamlWithMaintainers("provchart"));
            var (secretRing, publicRing, signingKey, _) = CreateTestKeys(userId: "HelmSharp Signer <signer@example.com>");

            var provPath = await HelmProvenance.SignAsync(tgz, signingKey);

            // The armor must dash-escape sequence items while the hash covers the original text.
            var prov = await File.ReadAllTextAsync(provPath);
            Assert.Contains("\n- - email: alice@example.com", prov, StringComparison.Ordinal);
            Assert.Contains("\n- - name: Bob", prov, StringComparison.Ordinal);
            Assert.DoesNotContain("\n- email: alice@example.com", prov, StringComparison.Ordinal);

            var pubPath = Path.Combine(work, "public.gpg");
            using (var fs = File.Create(pubPath))
                publicRing.Encode(fs);
            var (exitCode, stdout, stderr) = await RunHelmAsync("verify", tgz, "--keyring", pubPath);
            Assert.True(exitCode == 0, $"helm verify failed ({exitCode}): {stderr}\n{stdout}");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task SignAndVerify_YamlSequenceMetadata_RoundTrips()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateChartArchiveAsync(work, "provchart", ChartYamlWithMaintainers("provchart"));
            var (_, _, signingKey, trustedKey) = CreateTestKeys();
            await HelmProvenance.SignAsync(tgz, signingKey);

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.True(result.IsValid, result.FailureReason);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // --- Non-ASCII (UTF-8) metadata ---

    [HelmCliFact]
    public async Task HelmSignedProv_NonAsciiMetadata_VerifiesUnderHelmSharp()
    {
        var work = TempDir();
        try
        {
            var chartDir = CreateChartDirectory(work, "helmchart", ChartYamlWithNonAscii("helmchart"));
            var (secretRing, _, _, trustedKey) = CreateTestKeys(userId: "Helm Interop <interop@example.com>");
            var secretPath = Path.Combine(work, "secret.gpg");
            using (var fs = File.Create(secretPath))
                secretRing.Encode(fs);

            var outDir = Path.Combine(work, "out");
            Directory.CreateDirectory(outDir);
            var (exitCode, _, stderr) = await RunHelmAsync(
                "package", chartDir, "--destination", outDir,
                "--sign", "--key", "Helm Interop", "--keyring", secretPath);
            Assert.Equal(0, exitCode);

            var tgz = Directory.GetFiles(outDir, "*.tgz").Single();
            var prov = await File.ReadAllTextAsync(tgz + ".prov");
            Assert.Contains("中文描述 émojis", prov, StringComparison.Ordinal);
            Assert.Contains("名字 Test", prov, StringComparison.Ordinal);

            // ASCII decoding would turn the metadata into '?' and invalidate the signature.
            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.True(result.IsValid, result.FailureReason);
            Assert.True(result.SignatureValid);
            Assert.True(result.DigestMatches);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [HelmCliFact]
    public async Task SignAsync_NonAsciiMetadata_VerifiesUnderHelm()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateChartArchiveAsync(work, "provchart", ChartYamlWithNonAscii("provchart"));
            var (secretRing, publicRing, signingKey, _) = CreateTestKeys(userId: "HelmSharp Signer <signer@example.com>");

            var provPath = await HelmProvenance.SignAsync(tgz, signingKey);

            // ASCII writing would corrupt the metadata before signing; assert the bytes survive.
            var provBytes = await File.ReadAllBytesAsync(provPath);
            var prov = Encoding.UTF8.GetString(provBytes);
            Assert.Contains("中文描述 émojis", prov, StringComparison.Ordinal);
            Assert.Contains("名字 Test", prov, StringComparison.Ordinal);

            var pubPath = Path.Combine(work, "public.gpg");
            using (var fs = File.Create(pubPath))
                publicRing.Encode(fs);
            var (exitCode, stdout, stderr) = await RunHelmAsync("verify", tgz, "--keyring", pubPath);
            Assert.True(exitCode == 0, $"helm verify failed ({exitCode}): {stderr}\n{stdout}");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task SignAndVerify_NonAsciiMetadata_RoundTrips()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateChartArchiveAsync(work, "provchart", ChartYamlWithNonAscii("provchart"));
            var (_, _, signingKey, trustedKey) = CreateTestKeys();
            var provPath = await HelmProvenance.SignAsync(tgz, signingKey);

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);
            Assert.True(result.IsValid, result.FailureReason);
            Assert.True(result.SignatureValid);

            // The signed body keeps the original UTF-8 text rather than replacement characters.
            var prov = await File.ReadAllTextAsync(provPath);
            Assert.Contains("名字 Test", prov, StringComparison.Ordinal);
            var metadata = HelmProvenance.ExtractMetadata(prov);
            Assert.Equal("中文描述 émojis — ünïcode", metadata["description"]);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // --- Armor helpers ---

    /// <summary>Decoded signature payload of a <c>.prov</c> file, excluding armor headers and the checksum.</summary>
    private static byte[] ExtractSignaturePayload(string provContent)
    {
        var text = provContent.Replace("\r\n", "\n", StringComparison.Ordinal);
        var sigStart = text.IndexOf("-----BEGIN PGP SIGNATURE-----", StringComparison.Ordinal);
        var footerStart = text.IndexOf("-----END PGP SIGNATURE-----", sigStart, StringComparison.Ordinal);
        var base64 = new StringBuilder();
        foreach (var line in text[sigStart..footerStart].Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.Contains(':') || trimmed.StartsWith('=') || trimmed.StartsWith("-----", StringComparison.Ordinal))
                continue;
            base64.Append(trimmed);
        }

        return Convert.FromBase64String(base64.ToString());
    }

    /// <summary>The Base64(CRC-24) armor checksum line BouncyCastle writes for <paramref name="payload"/>.</summary>
    private static string BouncyCastleChecksumLine(byte[] payload)
    {
        using var ms = new MemoryStream();
        using (var armored = new ArmoredOutputStream(ms))
            armored.Write(payload, 0, payload.Length);
        foreach (var line in Encoding.ASCII.GetString(ms.ToArray()).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('='))
                return trimmed;
        }

        throw new InvalidOperationException("BouncyCastle armor did not emit a checksum line.");
    }

    /// <summary>Inserts the standard CRC-24 armor checksum line before the signature footer.</summary>
    private static string InsertArmorChecksum(string provContent)
    {
        var checksum = BouncyCastleChecksumLine(ExtractSignaturePayload(provContent));
        return provContent.Replace(
            "-----END PGP SIGNATURE-----",
            checksum + "\n-----END PGP SIGNATURE-----",
            StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunHelmAsync(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "helm",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);
    }
}
