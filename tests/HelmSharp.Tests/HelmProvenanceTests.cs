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
using HelmSharp.Chart;

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
        hashed.SetPreferredSymmetricAlgorithms(false, [(int)SymmetricKeyAlgorithmTag.Aes256]);
        hashed.SetPreferredHashAlgorithms(false, [(int)HashAlgorithmTag.Sha512, (int)HashAlgorithmTag.Sha256]);
        if (validitySeconds is not null)
            hashed.SetKeyExpirationTime(false, validitySeconds.Value);

        var keyPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, kp, createdAt ?? DateTime.UtcNow);
        var encAlgo = passphrase is null ? SymmetricKeyAlgorithmTag.Null : SymmetricKeyAlgorithmTag.Aes256;
        var krg = new PgpKeyRingGenerator(
            (int)PgpSignature.PositiveCertification,
            keyPair,
            userId,
            encAlgo,
            HashAlgorithmTag.Sha512,
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
            HashAlgorithmTag.Sha512,
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
        bool expiredPrimary = false,
        bool expiredBinding = false,
        bool expiredSubkeyKeyLifetime = false)
    {
        var kpg = new RsaKeyPairGenerator();
        kpg.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var createdAt = expiredPrimary
            ? DateTime.UtcNow.AddSeconds(-10)
            : expiredBinding || expiredSubkeyKeyLifetime ? DateTime.UtcNow.AddMinutes(-2) : DateTime.UtcNow;
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
            HashAlgorithmTag.Sha512,
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
        if (expiredBinding || expiredSubkeyKeyLifetime)
        {
            signingFlags.SetSignatureCreationTime(false, createdAt);
        }
        if (expiredBinding)
            signingFlags.SetSignatureExpirationTime(false, 30);
        if (expiredSubkeyKeyLifetime)
            signingFlags.SetKeyExpirationTime(false, 30);
        if (expiredPrimary)
            signingFlags.SetKeyExpirationTime(false, 3600);
        var backSignatureGenerator = new PgpSignatureGenerator(
            PublicKeyAlgorithmTag.RsaGeneral,
            HashAlgorithmTag.Sha256);
        backSignatureGenerator.InitSign(PgpSignature.PrimaryKeyBinding, signingPair.PrivateKey);
        var backSignaturePackets = new PgpSignatureSubpacketGenerator();
        backSignaturePackets.SetSignatureCreationTime(false, createdAt);
        backSignatureGenerator.SetHashedSubpackets(backSignaturePackets.Generate());
        signingFlags.SetEmbeddedSignature(
            false,
            backSignatureGenerator.GenerateCertification(primaryPair.PublicKey, signingPair.PublicKey));
        keyRingGenerator.AddSubKey(
            signingPair,
            signingFlags.Generate(),
            null,
            HashAlgorithmTag.Sha512,
            HashAlgorithmTag.Sha512);
        return (keyRingGenerator.GenerateSecretKeyRing(), keyRingGenerator.GeneratePublicKeyRing());
    }

    private static void AddUnknownCriticalSubpacket(PgpSignatureSubpacketGenerator packets)
        => AddCriticalSubpacket(packets, (SignatureSubpacketTag)100);

    private static PgpSignature RemoveHashedCreationTime(PgpSignature signature)
    {
        var packetField = typeof(PgpSignature).GetField(
            "sigPck",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find the OpenPGP signature packet field.");
        var packet = (SignaturePacket)(packetField.GetValue(signature)
            ?? throw new InvalidOperationException("The OpenPGP signature packet was missing."));
        var withoutCreationTime = new SignaturePacket(
            packet.Version,
            packet.SignatureType,
            packet.KeyId,
            packet.KeyAlgorithm,
            packet.HashAlgorithm,
            packet.GetHashedSubPackets()
                .Where(subpacket => subpacket.SubpacketType != SignatureSubpacketTag.CreationTime)
                .ToArray(),
            packet.GetUnhashedSubPackets(),
            packet.GetFingerprint(),
            packet.GetSignature());
        var signatureConstructor = typeof(PgpSignature).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            [typeof(SignaturePacket)],
            modifiers: null)
            ?? throw new InvalidOperationException("Could not find the OpenPGP signature constructor.");
        return (PgpSignature)(signatureConstructor.Invoke([withoutCreationTime])
            ?? throw new InvalidOperationException("Could not construct the malformed OpenPGP signature."));
    }

    private static bool HasSupportedPolicySubpackets(PgpSignature signature)
    {
        var method = typeof(HelmProvenanceTrustedKey).GetMethod(
            "HasSupportedPolicySubpackets",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find the trusted-key policy validator.");
        return (bool)(method.Invoke(null, [signature])
            ?? throw new InvalidOperationException("The policy validator returned no result."));
    }

    private static bool SignatureVersionMatchesKey(int signatureVersion, int keyVersion)
    {
        var method = typeof(HelmProvenance).GetMethod(
            "SignatureVersionMatchesKey",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find the signature/key version validator.");
        return (bool)(method.Invoke(null, [signatureVersion, keyVersion])
            ?? throw new InvalidOperationException("The signature/key version validator returned no result."));
    }

    private static bool HasRequiredSignatureCreationTime(PgpSignature signature)
    {
        var method = typeof(HelmProvenance).GetMethod(
            "HasRequiredSignatureCreationTime",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find the document-signature timestamp validator.");
        return (bool)(method.Invoke(null, [signature])
            ?? throw new InvalidOperationException("The document-signature timestamp validator returned no result."));
    }

    private static string RemoveHashedCreationTimeFromClearSignedProv(string prov)
    {
        const string signatureHeader = "-----BEGIN PGP SIGNATURE-----";
        const string signatureFooter = "-----END PGP SIGNATURE-----";
        var start = prov.IndexOf(signatureHeader, StringComparison.Ordinal);
        var end = prov.IndexOf(signatureFooter, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var base64 = string.Concat(prov[(start + signatureHeader.Length)..end]
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('=') && !line.StartsWith(':')));
        var parsed = new PgpObjectFactory(Convert.FromBase64String(base64)).NextPgpObject() as PgpSignatureList;
        Assert.NotNull(parsed);
        using var signatureBytes = new MemoryStream();
        RemoveHashedCreationTime(parsed![0]).Encode(signatureBytes);
        var encoded = Convert.ToBase64String(signatureBytes.ToArray());
        var wrapped = string.Join('\n', Enumerable.Range(0, (encoded.Length + 63) / 64)
            .Select(index => encoded.Substring(index * 64, Math.Min(64, encoded.Length - index * 64))));
        return prov[..(start + signatureHeader.Length)] + "\n\n" + wrapped + "\n" + prov[end..];
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
            HashAlgorithmTag.Sha512,
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
        revSub.SetRevocationReason(true, RevocationReasonTag.KeySuperseded, "test revocation");
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
            HashAlgorithmTag.Sha512,
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

    private static string ExtractCleartextBodyForTest(string provContent)
    {
        var text = provContent.Replace("\r\n", "\n", StringComparison.Ordinal);
        var bodyStart = text.IndexOf("\n\n", StringComparison.Ordinal) + 2;
        var signatureStart = text.IndexOf("-----BEGIN PGP SIGNATURE-----", bodyStart, StringComparison.Ordinal);
        Assert.True(bodyStart >= 2 && signatureStart > bodyStart);
        return string.Join('\n', text.AsSpan(bodyStart, signatureStart - bodyStart)
            .ToString()
            .Split('\n')
            .Select(line => line.StartsWith("- ", StringComparison.Ordinal) ? line[2..] : line));
    }

    private static string CreateClearSignedProvForTest(
        string body,
        HelmProvenanceSigningKey signingKey,
        DateTime creationTime,
        long? expirationSeconds = null,
        int signatureType = PgpSignature.CanonicalTextDocument,
        SignatureSubpacketTag? criticalSubpacketTag = null)
    {
        var secretKey = signingKey.GetSecretKey();
        var signatureGenerator = new PgpSignatureGenerator(secretKey.PublicKey.Algorithm, HashAlgorithmTag.Sha512);
        signatureGenerator.InitSign(signatureType, secretKey.ExtractPrivateKeyUtf8(signingKey.Passphrase)!);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetSignatureCreationTime(false, creationTime);
        if (expirationSeconds is not null)
            packets.SetSignatureExpirationTime(false, expirationSeconds.Value);
        if (criticalSubpacketTag is not null)
            AddCriticalSubpacket(packets, criticalSubpacketTag.Value);
        signatureGenerator.SetHashedSubpackets(packets.Generate());

        var canonical = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (canonical.EndsWith("\n", StringComparison.Ordinal))
            canonical = canonical[..^1];
        var lines = canonical.Split('\n');
        var signedBytes = Encoding.UTF8.GetBytes(string.Join("\r\n", lines.Select(line => line.TrimEnd(' ', '\t'))));
        signatureGenerator.Update(signedBytes, 0, signedBytes.Length);
        using var signatureBytes = new MemoryStream();
        signatureGenerator.Generate().Encode(signatureBytes);

        var armor = Convert.ToBase64String(signatureBytes.ToArray());
        var wrappedArmor = string.Join('\n', Enumerable.Range(0, (armor.Length + 63) / 64)
            .Select(index => armor.Substring(index * 64, Math.Min(64, armor.Length - index * 64))));
        var escapedBody = string.Join('\n', body.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.StartsWith("-", StringComparison.Ordinal) ? "- " + line : line));
        return "-----BEGIN PGP SIGNED MESSAGE-----\nHash: SHA512\n\n" + escapedBody +
            (escapedBody.EndsWith("\n", StringComparison.Ordinal) ? string.Empty : "\n") +
            "-----BEGIN PGP SIGNATURE-----\n\n" + wrappedArmor + "\n-----END PGP SIGNATURE-----\n";
    }

    private static void AddCriticalSubpacket(PgpSignatureSubpacketGenerator packets, SignatureSubpacketTag tag)
    {
        var subpacketConstructor = typeof(SignatureSubpacket).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            [typeof(SignatureSubpacketTag), typeof(bool), typeof(bool), typeof(byte[])],
            modifiers: null)
            ?? throw new InvalidOperationException("Could not find the OpenPGP subpacket test constructor.");
        var subpacket = (SignatureSubpacket)subpacketConstructor.Invoke(
            [tag, true, false, Array.Empty<byte>()]);
        packets.AddCustomSubpacket(subpacket);
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
    public async Task Verify_Version4SignatureByVersion6TrustedKey_FailsClosed()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, trustedV4Key) = CreateTestKeys();
            await HelmProvenance.SignAsync(tgz, signingKey);

            PgpPublicKey version6Primary = new(new PublicKeyPacket(
                (byte)6,
                trustedV4Key.PublicKey.Algorithm,
                trustedV4Key.PublicKey.CreationTime,
                trustedV4Key.PublicKey.PublicKeyPacket.Key));
            var trustedKeyConstructor = typeof(HelmProvenanceTrustedKey).GetConstructors(
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Single();
            var trustedVersion6Key = (HelmProvenanceTrustedKey)trustedKeyConstructor.Invoke(
                [version6Primary, null, null, new[] { version6Primary }]);

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedVersion6Key]);

            Assert.False(result.IsValid);
            Assert.Contains("signature is invalid", result.FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cryptographically valid but the signing key", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(3, 4, true)]
    [InlineData(4, 4, true)]
    [InlineData(6, 6, true)]
    [InlineData(6, 4, false)]
    [InlineData(4, 6, false)]
    [InlineData(3, 6, false)]
    public void SignatureVersionMatchesKey_UsesRegisteredVersionPairs(int signatureVersion, int keyVersion, bool expected)
    {
        Assert.Equal(expected, SignatureVersionMatchesKey(signatureVersion, keyVersion));
    }

    [Fact]
    public async Task Verify_DocumentSignatureWithoutHashedCreationTime_FailsClosed()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, trustedKey) = CreateTestKeys();
            await HelmProvenance.SignAsync(tgz, signingKey);
            var provPath = tgz + ".prov";
            var prov = await File.ReadAllTextAsync(provPath);
            await File.WriteAllTextAsync(provPath, RemoveHashedCreationTimeFromClearSignedProv(prov));

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);

            Assert.False(result.IsValid);
            Assert.Contains("hashed creation time", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void DocumentSignatureWithoutHashedCreationTime_IsMalformed()
    {
        var (secretRing, _, _, _) = CreateTestKeys();
        var secret = secretRing.GetSecretKeys().First();
        var generator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha512);
        generator.InitSign(PgpSignature.CanonicalTextDocument, secret.ExtractPrivateKey(null)!);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetSignatureCreationTime(false, DateTime.UtcNow);
        generator.SetHashedSubpackets(packets.Generate());

        var signature = generator.Generate();
        Assert.True(HasRequiredSignatureCreationTime(signature));
        Assert.False(HasRequiredSignatureCreationTime(RemoveHashedCreationTime(signature)));
    }

    [Fact]
    public void PolicySignatureWithMalformedCriticalRevocationReason_IsRejected()
    {
        var (secretRing, _, _, _) = CreateTestKeys();
        var secret = secretRing.GetSecretKeys().First();
        var generator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha512);
        generator.InitSign(PgpSignature.KeyRevocation, secret.ExtractPrivateKey(null)!);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetSignatureCreationTime(false, DateTime.UtcNow);
        AddCriticalSubpacket(packets, SignatureSubpacketTag.RevocationReason);
        generator.SetHashedSubpackets(packets.Generate());

        Assert.False(HasSupportedPolicySubpackets(generator.Generate()));
    }

    [Fact]
    public void SigningKeyDispose_ZeroesOwnedSecretAndPassphraseCopies()
    {
        var passphrase = "passphrase".ToCharArray();
        var (secretRing, _, setupSigningKey, _) = CreateTestKeys(passphrase: passphrase);
        setupSigningKey.Dispose();
        byte[] secretBytes;
        using (var ms = new MemoryStream())
        {
            secretRing.Encode(ms);
            secretBytes = ms.ToArray();
        }
        var originalInput = secretBytes.ToArray();
        var signingKey = HelmProvenanceSigningKey.FromSecretKeyData(secretBytes, passphrase);
        var secretCopy = (byte[])(typeof(HelmProvenanceSigningKey)
            .GetField("_secretKeyData", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(signingKey)!);
        var passphraseCopy = (char[])(typeof(HelmProvenanceSigningKey)
            .GetField("_passphrase", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(signingKey)!);

        signingKey.Dispose();
        signingKey.Dispose();

        Assert.All(secretCopy, value => Assert.Equal((byte)0, value));
        Assert.All(passphraseCopy, value => Assert.Equal('\0', value));
        Assert.Equal(originalInput, secretBytes);
        Assert.Equal("passphrase", new string(passphrase));
        Assert.Throws<ObjectDisposedException>(() => signingKey.GetSecretKey());
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
    public async Task SignAndVerify_MetadataContainingSignatureDelimiter_Succeeds()
    {
        var work = TempDir();
        try
        {
            var chartYaml = """
                apiVersion: v2
                name: provchart
                description: "-----BEGIN PGP SIGNATURE-----"
                type: application
                version: 0.1.0
                appVersion: "1.0"
                """;
            var tgz = await CreateTestChartArchiveAsync(work, chartYaml);
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
    public void FromKeyringData_UnknownCriticalSelfCertificationIsRejected()
    {
        var keyGenerator = new RsaKeyPairGenerator();
        keyGenerator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, keyGenerator.GenerateKeyPair(), DateTime.UtcNow);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetKeyFlags(true, PgpKeyFlags.CanSign | PgpKeyFlags.CanCertify);
        AddUnknownCriticalSubpacket(packets);
        var keyRingGenerator = new PgpKeyRingGenerator(
            (int)PgpSignature.PositiveCertification,
            pair,
            "Critical extension <critical@example.com>",
            SymmetricKeyAlgorithmTag.Null,
            HashAlgorithmTag.Sha512,
            (char[])null!,
            false,
            packets.Generate(),
            null,
            new SecureRandom());
        var publicRing = keyRingGenerator.GeneratePublicKeyRing();
        using var keyBytes = new MemoryStream();
        publicRing.Encode(keyBytes);

        var exception = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(keyBytes.ToArray()));

        Assert.Contains("No usable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromKeyringData_ForgedPrimaryCertificationCannotExtendExpiry()
    {
        const string userId = "Signing subkey <subkey@example.com>";
        var (secretRing, publicRing) = CreateRingWithSigningSubkey(userId, expiredPrimary: true);
        var signingSecret = secretRing.GetSecretKeys().Last();
        var primaryPublic = publicRing.GetPublicKeys().First(key => key.IsMasterKey);

        // A compromised subkey can attach an apparent newer self-certification that
        // claims the primary key ID. The issuer hint is not cryptographic proof.
        var forged = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        forged.InitSign(PgpSignature.PositiveCertification, signingSecret.ExtractPrivateKey(null)!);
        var hashed = new PgpSignatureSubpacketGenerator();
        hashed.SetSignatureCreationTime(false, DateTime.UtcNow);
        hashed.SetKeyExpirationTime(false, 3600);
        forged.SetHashedSubpackets(hashed.Generate());
        var unhashed = new PgpSignatureSubpacketGenerator();
        unhashed.SetIssuerKeyID(false, primaryPublic.KeyId);
        forged.SetUnhashedSubpackets(unhashed.Generate());
        var forgedPrimary = PgpPublicKey.AddCertification(primaryPublic, userId, forged.GenerateCertification(userId, primaryPublic));
        var forgedRing = PgpPublicKeyRing.InsertPublicKey(publicRing, forgedPrimary);

        using var ms = new MemoryStream();
        forgedRing.Encode(ms);
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
    public void FromKeyringData_ExpiredSubkeyBindingCannotAuthorizeSigning()
    {
        var (_, publicRing) = CreateRingWithSigningSubkey(expiredBinding: true);
        using var ms = new MemoryStream();
        publicRing.Encode(ms);

        var exception = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(ms.ToArray()));

        Assert.Contains("No usable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromKeyringData_RevokedUserCertificationCannotAuthorizeSigning()
    {
        const string userId = "Revoked policy <revoked-policy@example.com>";
        var (secretRing, publicRing, _, _) = CreateTestKeys(userId);
        var primarySecret = secretRing.GetSecretKeys().First();
        var primary = publicRing.GetPublicKeys().Single(key => key.IsMasterKey);
        var revocation = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        revocation.InitSign(PgpSignature.CertificationRevocation, primarySecret.ExtractPrivateKey(null)!);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetSignatureCreationTime(false, DateTime.UtcNow.AddMinutes(1));
        revocation.SetHashedSubpackets(packets.Generate());
        var revokedPrimary = PgpPublicKey.AddCertification(
            primary,
            userId,
            revocation.GenerateCertification(userId, primary));
        var revokedRing = PgpPublicKeyRing.InsertPublicKey(publicRing, revokedPrimary);
        using var ms = new MemoryStream();
        revokedRing.Encode(ms);

        var exception = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(ms.ToArray()));

        Assert.Contains("No usable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromKeyringData_ExpiredBindingCannotExtendSubkeyKeyLifetime()
    {
        var (secretRing, publicRing) = CreateRingWithSigningSubkey(expiredSubkeyKeyLifetime: true);
        var primarySecret = secretRing.GetSecretKeys().First();
        var master = publicRing.GetPublicKeys().Single(key => key.IsMasterKey);
        var subkey = publicRing.GetPublicKeys().Single(key => !key.IsMasterKey);
        var renewedBinding = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        renewedBinding.InitSign(PgpSignature.SubkeyBinding, primarySecret.ExtractPrivateKey(null)!);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetSignatureCreationTime(false, DateTime.UtcNow.AddMinutes(-1));
        packets.SetSignatureExpirationTime(false, 30);
        packets.SetKeyExpirationTime(false, 3600);
        packets.SetKeyFlags(true, PgpKeyFlags.CanSign);
        renewedBinding.SetHashedSubpackets(packets.Generate());
        var updatedSubkey = PgpPublicKey.AddCertification(
            subkey,
            renewedBinding.GenerateCertification(master, subkey));
        var updatedRing = PgpPublicKeyRing.InsertPublicKey(publicRing, updatedSubkey);
        using var ms = new MemoryStream();
        updatedRing.Encode(ms);

        var exception = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(ms.ToArray()));

        Assert.Contains("No usable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromKeyringData_ExpiredPrimaryUserCertificationCannotAuthorizeSigning()
    {
        const string userId = "Expired assertion <expired-assertion@example.com>";
        var generator = new RsaKeyPairGenerator();
        generator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var pair = new PgpKeyPair(
            PublicKeyAlgorithmTag.RsaGeneral,
            generator.GenerateKeyPair(),
            DateTime.UtcNow.AddMinutes(-2));
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetSignatureCreationTime(false, DateTime.UtcNow.AddMinutes(-1));
        packets.SetSignatureExpirationTime(false, 30);
        packets.SetKeyFlags(true, PgpKeyFlags.CanSign | PgpKeyFlags.CanCertify);
        var ringGenerator = new PgpKeyRingGenerator(
            (int)PgpSignature.PositiveCertification,
            pair,
            userId,
            SymmetricKeyAlgorithmTag.Null,
            HashAlgorithmTag.Sha512,
            true,
            (char[])null!,
            false,
            packets.Generate(),
            null,
            new SecureRandom());
        using var ms = new MemoryStream();
        ringGenerator.GeneratePublicKeyRing().Encode(ms);

        var exception = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(ms.ToArray()));

        Assert.Contains("No usable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromPublicKeyData_ExpiredVersion3PrimaryUsesPacketValidityPeriod()
    {
        const string userId = "Expired V3 <expired-v3@example.com>";
        var keyGenerator = new RsaKeyPairGenerator();
        keyGenerator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var createdAt = DateTime.UtcNow.AddDays(-3);
        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, keyGenerator.GenerateKeyPair(), createdAt);
        var packet = new PublicKeyPacket(
            (byte)3,
            pair.PublicKey.Algorithm,
            pair.PublicKey.CreationTime,
            pair.PublicKey.PublicKeyPacket.Key);
        var validityField = typeof(PublicKeyPacket).GetField(
            "m_validDays",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find the V3 key validity field.");
        validityField.SetValue(packet, 1);
        PgpPublicKey primary = new(packet);

        var certification = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha512);
        certification.InitSign(PgpSignature.PositiveCertification, pair.PrivateKey);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetSignatureCreationTime(false, DateTime.UtcNow);
        packets.SetKeyFlags(true, PgpKeyFlags.CanSign);
        certification.SetHashedSubpackets(packets.Generate());
        primary = PgpPublicKey.AddCertification(
            primary,
            userId,
            certification.GenerateCertification(userId, primary));
        using var keyBytes = new MemoryStream();
        primary.Encode(keyBytes);

        var exception = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromPublicKeyData(keyBytes.ToArray()));

        Assert.Contains("No usable", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PgpSignature.PositiveCertification)]
    [InlineData(PgpSignature.DirectKey)]
    [InlineData(PgpSignature.SubkeyBinding)]
    [InlineData(PgpSignature.PrimaryKeyBinding)]
    [InlineData(PgpSignature.KeyRevocation)]
    [InlineData(PgpSignature.SubkeyRevocation)]
    [InlineData(PgpSignature.CertificationRevocation)]
    public void PolicySignatureWithoutHashedCreationTimeIsRejected(int signatureType)
    {
        var (secretRing, _, _, _) = CreateTestKeys();
        var secret = secretRing.GetSecretKeys().First();
        var generator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha512);
        generator.InitSign(signatureType, secret.ExtractPrivateKey(null)!);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetSignatureCreationTime(false, DateTime.UtcNow);
        generator.SetHashedSubpackets(packets.Generate());
        var validSignature = generator.Generate();
        Assert.True(HasSupportedPolicySubpackets(validSignature));

        var malformedSignature = RemoveHashedCreationTime(validSignature);

        Assert.False(HasSupportedPolicySubpackets(malformedSignature));
    }

    [Fact]
    public void FromKeyringData_LatestUserCertificationWithoutKeyFlagsSupersedesOlderFlags()
    {
        const string userId = "Latest assertion <latest@example.com>";
        var (secretRing, publicRing, _, _) = CreateTestKeys(
            userId,
            createdAt: DateTime.UtcNow.AddMinutes(-5));
        var primarySecret = secretRing.GetSecretKeys().First();
        var primary = publicRing.GetPublicKeys().Single(key => key.IsMasterKey);
        var certification = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha512);
        certification.InitSign(PgpSignature.PositiveCertification, primarySecret.ExtractPrivateKey(null)!);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetSignatureCreationTime(false, DateTime.UtcNow);
        certification.SetHashedSubpackets(packets.Generate());
        primary = PgpPublicKey.AddCertification(
            primary,
            userId,
            certification.GenerateCertification(userId, primary));
        var updatedRing = PgpPublicKeyRing.InsertPublicKey(publicRing, primary);
        using var ms = new MemoryStream();
        updatedRing.Encode(ms);

        var trustedKey = Assert.Single(HelmProvenanceTrustedKey.FromKeyringData(ms.ToArray()));

        Assert.Equal(userId, trustedKey.UserId);
    }

    [Fact]
    public void FromKeyringData_LatestSubkeyBindingWithoutKeyFlagsSupersedesOlderFlags()
    {
        var (secretRing, publicRing) = CreateRingWithSigningSubkey();
        var primarySecret = secretRing.GetSecretKeys().First();
        var master = publicRing.GetPublicKeys().Single(key => key.IsMasterKey);
        var subkey = publicRing.GetPublicKeys().Single(key => !key.IsMasterKey);
        var previousBinding = subkey.GetKeySignatures()
            .Single(signature => signature.SignatureType == PgpSignature.SubkeyBinding);
        var backSignature = previousBinding.GetHashedSubPackets()!.GetEmbeddedSignatures()[0];
        subkey = PgpPublicKey.RemoveCertification(subkey, previousBinding);

        Thread.Sleep(1100);
        var binding = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha512);
        binding.InitSign(PgpSignature.SubkeyBinding, primarySecret.ExtractPrivateKey(null)!);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetSignatureCreationTime(false, DateTime.UtcNow);
        packets.AddEmbeddedSignature(false, backSignature);
        binding.SetHashedSubpackets(packets.Generate());
        subkey = PgpPublicKey.AddCertification(subkey, binding.GenerateCertification(master, subkey));
        var updatedRing = PgpPublicKeyRing.InsertPublicKey(publicRing, subkey);
        using var ms = new MemoryStream();
        updatedRing.Encode(ms);

        var trustedKey = Assert.Single(HelmProvenanceTrustedKey.FromKeyringData(ms.ToArray()));

        Assert.Equal(Convert.ToHexString(subkey.GetFingerprint()).ToLowerInvariant(), trustedKey.Fingerprint);
    }

    [Fact]
    public void FromKeyringData_ExpiredDirectKeyAssertionCannotAuthorizeSigning()
    {
        var keyGenerator = new RsaKeyPairGenerator();
        keyGenerator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var pair = new PgpKeyPair(
            PublicKeyAlgorithmTag.RsaGeneral,
            keyGenerator.GenerateKeyPair(),
            DateTime.UtcNow.AddMinutes(-2));
        var signatureGenerator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        signatureGenerator.InitSign(PgpSignature.DirectKey, pair.PrivateKey);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetSignatureCreationTime(false, DateTime.UtcNow.AddMinutes(-1));
        packets.SetSignatureExpirationTime(false, 30);
        packets.SetKeyFlags(true, PgpKeyFlags.CanSign);
        signatureGenerator.SetHashedSubpackets(packets.Generate());
        var expiredAssertionKey = PgpPublicKey.AddCertification(
            pair.PublicKey,
            signatureGenerator.GenerateCertification(pair.PublicKey));
        using var ms = new MemoryStream();
        expiredAssertionKey.Encode(ms);

        var exception = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(ms.ToArray()));

        Assert.Contains("No usable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromKeyringData_SkipsUncertifiedUserId()
    {
        const string uncertifiedUserId = "Uncertified <untrusted@example.com>";
        const string certifiedUserId = "Certified <trusted@example.com>";
        var (_, publicRing, _, _) = CreateTestKeys(certifiedUserId);
        var primary = publicRing.GetPublicKeys().Single(key => key.IsMasterKey);
        var attackerGenerator = new RsaKeyPairGenerator();
        attackerGenerator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var attackerPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, attackerGenerator.GenerateKeyPair(), DateTime.UtcNow);
        var thirdPartyCertification = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        thirdPartyCertification.InitSign(PgpSignature.PositiveCertification, attackerPair.PrivateKey);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetSignatureCreationTime(false, DateTime.UtcNow);
        thirdPartyCertification.SetHashedSubpackets(packets.Generate());
        var withUncertifiedId = PgpPublicKey.AddCertification(primary, uncertifiedUserId,
            thirdPartyCertification.GenerateCertification(uncertifiedUserId, primary));
        var updatedRing = PgpPublicKeyRing.InsertPublicKey(publicRing, withUncertifiedId);
        using var ms = new MemoryStream();
        updatedRing.Encode(ms);

        var trustedKey = Assert.Single(HelmProvenanceTrustedKey.FromKeyringData(ms.ToArray()));

        Assert.Equal(certifiedUserId, trustedKey.UserId);
    }

    [Fact]
    public void FromKeyringData_SkipsRevokedUserId()
    {
        const string revokedUserId = "Revoked <revoked@example.com>";
        const string activeUserId = "Active <active@example.com>";
        var (secretRing, publicRing, _, _) = CreateTestKeys(revokedUserId);
        var primarySecret = secretRing.GetSecretKeys().First();
        var primary = publicRing.GetPublicKeys().Single(key => key.IsMasterKey);
        var privateKey = primarySecret.ExtractPrivateKey(null)!;

        var revocation = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        revocation.InitSign(PgpSignature.CertificationRevocation, privateKey);
        var revocationPackets = new PgpSignatureSubpacketGenerator();
        revocationPackets.SetSignatureCreationTime(false, DateTime.UtcNow);
        revocation.SetHashedSubpackets(revocationPackets.Generate());
        var updated = PgpPublicKey.AddCertification(primary, revokedUserId,
            revocation.GenerateCertification(revokedUserId, primary));

        var certification = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        certification.InitSign(PgpSignature.PositiveCertification, privateKey);
        var certificationPackets = new PgpSignatureSubpacketGenerator();
        certificationPackets.SetSignatureCreationTime(false, DateTime.UtcNow.AddMinutes(1));
        certificationPackets.SetKeyFlags(true, PgpKeyFlags.CanSign | PgpKeyFlags.CanCertify);
        certification.SetHashedSubpackets(certificationPackets.Generate());
        updated = PgpPublicKey.AddCertification(updated, activeUserId,
            certification.GenerateCertification(activeUserId, updated));
        var updatedRing = PgpPublicKeyRing.InsertPublicKey(publicRing, updated);
        using var ms = new MemoryStream();
        updatedRing.Encode(ms);

        var trustedKey = Assert.Single(HelmProvenanceTrustedKey.FromKeyringData(ms.ToArray()));

        Assert.Equal(activeUserId, trustedKey.UserId);
    }

    [Fact]
    public void FromKeyringData_PrefersCertifiedPrimaryUserId()
    {
        const string secondaryUserId = "Secondary <secondary@example.com>";
        const string primaryUserId = "Primary <primary@example.com>";
        var (secretRing, publicRing, _, _) = CreateTestKeys(secondaryUserId);
        var secretKey = secretRing.GetSecretKeys().First();
        var primary = publicRing.GetPublicKeys().Single(key => key.IsMasterKey);
        var certification = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        certification.InitSign(PgpSignature.PositiveCertification, secretKey.ExtractPrivateKey(null)!);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetSignatureCreationTime(false, DateTime.UtcNow);
        packets.SetKeyFlags(true, PgpKeyFlags.CanSign | PgpKeyFlags.CanCertify);
        packets.SetPrimaryUserId(false, true);
        certification.SetHashedSubpackets(packets.Generate());
        var updated = PgpPublicKey.AddCertification(
            primary,
            primaryUserId,
            certification.GenerateCertification(primaryUserId, primary));
        var updatedRing = PgpPublicKeyRing.InsertPublicKey(publicRing, updated);
        using var ms = new MemoryStream();
        updatedRing.Encode(ms);

        var trustedKey = Assert.Single(HelmProvenanceTrustedKey.FromKeyringData(ms.ToArray()));

        Assert.Equal(primaryUserId, trustedKey.UserId);
    }

    [Fact]
    public void FromKeyringData_RejectsWeakHashUserCertification()
    {
        const string userId = "Weak hash <weak@example.com>";
        var (secretRing, publicRing, _, _) = CreateTestKeys(userId);
        var secretKey = secretRing.GetSecretKeys().First();
        var primary = publicRing.GetPublicKeys().Single(key => key.IsMasterKey);
        foreach (var existing in primary.GetSignaturesForId(userId).ToArray())
            primary = PgpPublicKey.RemoveCertification(primary, userId, existing);

        var certification = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha1);
        certification.InitSign(PgpSignature.PositiveCertification, secretKey.ExtractPrivateKey(null)!);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetSignatureCreationTime(false, DateTime.UtcNow);
        packets.SetKeyFlags(true, PgpKeyFlags.CanSign | PgpKeyFlags.CanCertify);
        certification.SetHashedSubpackets(packets.Generate());
        primary = PgpPublicKey.AddCertification(
            primary,
            userId,
            certification.GenerateCertification(userId, primary));
        var ring = PgpPublicKeyRing.InsertPublicKey(publicRing, primary);
        using var ms = new MemoryStream();
        ring.Encode(ms);

        var exception = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(ms.ToArray()));

        Assert.Contains("No usable", exception.Message, StringComparison.Ordinal);
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
    public async Task Verify_OversizedProvenanceFile_FailsWithinConfiguredLimit()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, trustedKey) = CreateTestKeys();
            await HelmProvenance.SignAsync(tgz, signingKey);
            await File.WriteAllBytesAsync(tgz + ".prov", new byte[16 * 1024 * 1024 + 1]);

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);

            Assert.False(result.IsValid);
            Assert.Contains("16 MiB verification limit", result.FailureReason, StringComparison.Ordinal);
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
    public async Task Verify_WeakHashSignature_FailsClosed()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (secretRing, _, signingKey, trustedKey) = CreateTestKeys();
            var provenancePath = await HelmProvenance.SignAsync(tgz, signingKey);
            var provenance = await File.ReadAllTextAsync(provenancePath);
            var signedBodyStart = provenance.IndexOf("\n\n", StringComparison.Ordinal) + 2;
            var signatureStart = provenance.IndexOf("-----BEGIN PGP SIGNATURE-----", signedBodyStart, StringComparison.Ordinal);
            var signedBody = provenance[signedBodyStart..signatureStart];
            var canonical = Encoding.UTF8.GetBytes(
                string.Join("\r\n", signedBody.TrimEnd('\n').Split('\n').Select(line => line.TrimEnd(' ', '\t'))));
            var privateKey = secretRing.GetSecretKeys().First().ExtractPrivateKey(null)!;
            var signatureGenerator = new PgpSignatureGenerator(
                secretRing.GetSecretKeys().First().PublicKey.Algorithm,
                HashAlgorithmTag.Sha1);
            signatureGenerator.InitSign(PgpSignature.CanonicalTextDocument, privateKey);
            signatureGenerator.SetHashedSubpackets(new PgpSignatureSubpacketGenerator().Generate());
            signatureGenerator.Update(canonical, 0, canonical.Length);
            var signature = signatureGenerator.Generate();
            using var armoredBytes = new MemoryStream();
            using (var armored = new ArmoredOutputStream(armoredBytes))
                signature.Encode(armored);
            var weakProvenance = provenance[..signatureStart] + Encoding.ASCII.GetString(armoredBytes.ToArray());
            await File.WriteAllTextAsync(provenancePath, weakProvenance);

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);

            Assert.False(result.IsValid);
            Assert.False(result.SignatureValid);
            Assert.Contains("SHA-1", result.FailureReason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_ExpiredDocumentSignature_Fails()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, trustedKey) = CreateTestKeys();
            var validProvPath = await HelmProvenance.SignAsync(tgz, signingKey);
            var body = ExtractCleartextBodyForTest(await File.ReadAllTextAsync(validProvPath));
            var expiredProv = CreateClearSignedProvForTest(
                body,
                signingKey,
                DateTime.UtcNow.AddMinutes(-2),
                expirationSeconds: 30);
            await File.WriteAllTextAsync(validProvPath, expiredProv);

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);

            Assert.False(result.IsValid);
            Assert.Contains("signature has expired", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_UnsignedTextAndUnknownArmorHeaders_FailClosed()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, trustedKey) = CreateTestKeys();
            var provenancePath = await HelmProvenance.SignAsync(tgz, signingKey);
            var validProvenance = await File.ReadAllTextAsync(provenancePath);
            var variants = new[]
            {
                "untrusted prefix\n" + validProvenance,
                validProvenance.Replace("Hash: SHA512\n\n", "Version: attacker\nHash: SHA512\n\n", StringComparison.Ordinal),
                validProvenance.Replace("Hash: SHA512\n\n", "Hash: VERIFIED-BY-ACME\n\n", StringComparison.Ordinal),
                validProvenance.Replace("Hash: SHA512\n\n", "Hash: SHA512, VERIFIED-BY-ACME\n\n", StringComparison.Ordinal),
            };

            foreach (var malformed in variants)
            {
                await File.WriteAllTextAsync(provenancePath, malformed);

                var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);

                Assert.False(result.IsValid);
                Assert.False(result.SignatureValid);
                Assert.Contains("Malformed OpenPGP clearsigned provenance armor", result.FailureReason, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_RejectsUtf16AndInvalidUtf8Provenance()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, trustedKey) = CreateTestKeys();
            var provenancePath = await HelmProvenance.SignAsync(tgz, signingKey);
            var validText = await File.ReadAllTextAsync(provenancePath);
            var utf16 = Encoding.Unicode.GetPreamble()
                .Concat(Encoding.Unicode.GetBytes(validText))
                .ToArray();
            var invalidUtf8 = Encoding.UTF8.GetBytes(validText);
            var metadataOffset = validText.IndexOf("provenance test chart", StringComparison.Ordinal);
            Assert.True(metadataOffset >= 0);
            invalidUtf8[metadataOffset] = 0xFF;

            foreach (var malformed in new[] { utf16, invalidUtf8 })
            {
                await File.WriteAllBytesAsync(provenancePath, malformed);

                var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);

                Assert.False(result.IsValid);
                Assert.False(result.SignatureValid);
                Assert.Contains("valid UTF-8 without a byte-order mark", result.FailureReason, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_BinaryDocumentSignatureInCleartextArmor_FailsClosed()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, trustedKey) = CreateTestKeys();
            var provenancePath = await HelmProvenance.SignAsync(tgz, signingKey);
            var body = ExtractCleartextBodyForTest(await File.ReadAllTextAsync(provenancePath));
            var binaryDocumentProvenance = CreateClearSignedProvForTest(
                body,
                signingKey,
                DateTime.UtcNow,
                signatureType: PgpSignature.BinaryDocument);
            await File.WriteAllTextAsync(provenancePath, binaryDocumentProvenance);

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);

            Assert.False(result.IsValid);
            Assert.False(result.SignatureValid);
            Assert.Contains("canonical-text document signature", result.FailureReason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_UnknownCriticalHashedSubpacket_FailsClosed()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, trustedKey) = CreateTestKeys();
            var provenancePath = await HelmProvenance.SignAsync(tgz, signingKey);
            var body = ExtractCleartextBodyForTest(await File.ReadAllTextAsync(provenancePath));
            var provenanceWithUnknownSubpacket = CreateClearSignedProvForTest(
                body,
                signingKey,
                DateTime.UtcNow,
                criticalSubpacketTag: (SignatureSubpacketTag)100);
            await File.WriteAllTextAsync(provenancePath, provenanceWithUnknownSubpacket);

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);

            Assert.False(result.IsValid);
            Assert.False(result.SignatureValid);
            Assert.Contains("unsupported critical hashed subpacket", result.FailureReason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_UnsignedTextAfterSignatureFooter_FailsClosed()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, trustedKey) = CreateTestKeys();
            var provenancePath = await HelmProvenance.SignAsync(tgz, signingKey);
            var validProvenance = await File.ReadAllTextAsync(provenancePath);
            await File.WriteAllTextAsync(provenancePath, validProvenance + "Unsigned appended claim\n");

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);

            Assert.False(result.IsValid);
            Assert.False(result.SignatureValid);
            Assert.Contains("Malformed OpenPGP clearsigned provenance armor", result.FailureReason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_MissingArchiveDigest_PreservesSuccessfulSignatureChecks()
    {
        var work = TempDir();
        try
        {
            var tgz = await CreateTestChartArchiveAsync(work);
            var (_, _, signingKey, trustedKey) = CreateTestKeys();
            var provPath = tgz + ".prov";
            var signedWithoutFiles = CreateClearSignedProvForTest(
                "name: provchart\nversion: 0.1.0",
                signingKey,
                DateTime.UtcNow);
            await File.WriteAllTextAsync(provPath, signedWithoutFiles);

            var result = await HelmProvenance.VerifyAsync(tgz, [trustedKey]);

            Assert.False(result.IsValid);
            Assert.True(result.SignatureValid);
            Assert.True(result.SignerTrusted);
            Assert.False(result.DigestMatches);
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
    public void FromKeyringData_RejectsRsaSigningKeyBelow2048Bits()
    {
        var keyGenerator = new RsaKeyPairGenerator();
        keyGenerator.Init(new KeyGenerationParameters(new SecureRandom(), 1024));
        var pair = new PgpKeyPair(
            PublicKeyAlgorithmTag.RsaGeneral,
            keyGenerator.GenerateKeyPair(),
            DateTime.UtcNow);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetKeyFlags(true, PgpKeyFlags.CanSign | PgpKeyFlags.CanCertify);
        var ringGenerator = new PgpKeyRingGenerator(
            (int)PgpSignature.PositiveCertification,
            pair,
            "Weak RSA <weak-rsa@example.com>",
            SymmetricKeyAlgorithmTag.Null,
            HashAlgorithmTag.Sha512,
            (char[])null!,
            false,
            packets.Generate(),
            null,
            new SecureRandom());
        using var keyBytes = new MemoryStream();
        ringGenerator.GeneratePublicKeyRing().Encode(keyBytes);

        var exception = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(keyBytes.ToArray()));

        Assert.Contains("No usable", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("P-224", false)]
    [InlineData("P-256", true)]
    public void FromKeyringData_EnforcesSupportedEcdsaCurves(string curveName, bool isSupported)
    {
        var keyGenerator = new ECKeyPairGenerator();
        var curveOid = Org.BouncyCastle.Asn1.Nist.NistNamedCurves.GetOid(curveName);
        keyGenerator.Init(new ECKeyGenerationParameters(curveOid, new SecureRandom()));
        var pair = new PgpKeyPair(
            PublicKeyAlgorithmTag.ECDsa,
            keyGenerator.GenerateKeyPair(),
            DateTime.UtcNow);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetKeyFlags(true, PgpKeyFlags.CanSign | PgpKeyFlags.CanCertify);
        var ringGenerator = new PgpKeyRingGenerator(
            (int)PgpSignature.PositiveCertification,
            pair,
            $"ECDSA {curveName} <{curveName.ToLowerInvariant()}@example.com>",
            SymmetricKeyAlgorithmTag.Null,
            HashAlgorithmTag.Sha512,
            (char[])null!,
            false,
            packets.Generate(),
            null,
            new SecureRandom());
        using var keyBytes = new MemoryStream();
        ringGenerator.GeneratePublicKeyRing().Encode(keyBytes);

        if (isSupported)
        {
            Assert.Single(HelmProvenanceTrustedKey.FromKeyringData(keyBytes.ToArray()));
        }
        else
        {
            var exception = Assert.Throws<ArgumentException>(
                () => HelmProvenanceTrustedKey.FromKeyringData(keyBytes.ToArray()));
            Assert.Contains("No usable", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SignatureHashStrength_RequiresCurveSpecificDigestSize()
    {
        var ecdsaGenerator = new ECKeyPairGenerator();
        var curveOid = Org.BouncyCastle.Asn1.Nist.NistNamedCurves.GetOid("P-521");
        ecdsaGenerator.Init(new ECKeyGenerationParameters(curveOid, new SecureRandom()));
        var ecdsaPair = new PgpKeyPair(
            PublicKeyAlgorithmTag.ECDsa,
            ecdsaGenerator.GenerateKeyPair(),
            DateTime.UtcNow);
        var ed448Generator = new Ed448KeyPairGenerator();
        ed448Generator.Init(new Ed448KeyGenerationParameters(new SecureRandom()));
        var ed448Pair = new PgpKeyPair(
            PublicKeyAlgorithmTag.Ed448,
            ed448Generator.GenerateKeyPair(),
            DateTime.UtcNow);

        Assert.False(HelmProvenance.HasSufficientSignatureHash(HashAlgorithmTag.Sha256, ecdsaPair.PublicKey));
        Assert.True(HelmProvenance.HasSufficientSignatureHash(HashAlgorithmTag.Sha512, ecdsaPair.PublicKey));
        Assert.False(HelmProvenance.HasSufficientSignatureHash(HashAlgorithmTag.Sha384, ed448Pair.PublicKey));
        Assert.True(HelmProvenance.HasSufficientSignatureHash(HashAlgorithmTag.Sha512, ed448Pair.PublicKey));
    }

    [Fact]
    public async Task FromKeyringData_MergesRenewedCertificateCopiesBeforeFiltering()
    {
        const string userId = "Renewed signer <renewed@example.com>";
        var (secretRing, publicRing, _, _) = CreateTestKeys(
            userId,
            createdAt: DateTime.UtcNow.AddSeconds(-10));
        var secret = secretRing.GetSecretKeys().First();
        var expiredPrimary = publicRing.GetPublicKeys().Single(key => key.IsMasterKey);
        foreach (var existing in expiredPrimary.GetSignaturesForId(userId).ToArray())
            expiredPrimary = PgpPublicKey.RemoveCertification(expiredPrimary, userId, existing);
        var staleCertification = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha512);
        staleCertification.InitSign(PgpSignature.PositiveCertification, secret.ExtractPrivateKey(null)!);
        var stalePackets = new PgpSignatureSubpacketGenerator();
        stalePackets.SetSignatureCreationTime(false, DateTime.UtcNow.AddSeconds(-9));
        stalePackets.SetSignatureExpirationTime(false, 1);
        stalePackets.SetKeyFlags(true, PgpKeyFlags.CanSign | PgpKeyFlags.CanCertify);
        staleCertification.SetHashedSubpackets(stalePackets.Generate());
        expiredPrimary = PgpPublicKey.AddCertification(
            expiredPrimary,
            userId,
            staleCertification.GenerateCertification(userId, expiredPrimary));
        var expiredRing = PgpPublicKeyRing.InsertPublicKey(publicRing, expiredPrimary);
        var renewal = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha512);
        renewal.InitSign(PgpSignature.PositiveCertification, secret.ExtractPrivateKey(null)!);
        var renewalPackets = new PgpSignatureSubpacketGenerator();
        renewalPackets.SetSignatureCreationTime(false, DateTime.UtcNow);
        renewalPackets.SetKeyFlags(true, PgpKeyFlags.CanSign | PgpKeyFlags.CanCertify);
        renewal.SetHashedSubpackets(renewalPackets.Generate());
        var renewedPrimary = PgpPublicKey.AddCertification(
            expiredPrimary,
            userId,
            renewal.GenerateCertification(userId, expiredPrimary));
        var renewedRing = PgpPublicKeyRing.InsertPublicKey(expiredRing, renewedPrimary);

        using var keyBytes = new MemoryStream();
        expiredRing.Encode(keyBytes);
        renewedRing.Encode(keyBytes);

        var imported = HelmProvenanceTrustedKey.FromKeyringData(keyBytes.ToArray());

        var trusted = Assert.Single(imported);
        Assert.Equal(Convert.ToHexString(renewedPrimary.GetFingerprint()).ToLowerInvariant(), trusted.Fingerprint);
    }

    [Fact]
    public void FromKeyringData_IgnoresForgedPrimaryKeyRevocation()
    {
        const string userId = "Victim <victim@example.com>";
        var (_, publicRing, _, _) = CreateTestKeys(userId);
        var primary = publicRing.GetPublicKeys().Single(key => key.IsMasterKey);
        var (attackerSecretRing, _, _, _) = CreateTestKeys("Attacker <attacker@example.com>");
        var forgedRevocation = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        forgedRevocation.InitSign(PgpSignature.KeyRevocation,
            attackerSecretRing.GetSecretKeys().First().ExtractPrivateKey(null)!);
        forgedRevocation.SetHashedSubpackets(new PgpSignatureSubpacketGenerator().Generate());
        var forgedPrimary = PgpPublicKey.AddCertification(primary, forgedRevocation.GenerateCertification(primary));
        var forgedRing = PgpPublicKeyRing.InsertPublicKey(publicRing, forgedPrimary);
        using var keyBytes = new MemoryStream();
        forgedRing.Encode(keyBytes);

        var imported = HelmProvenanceTrustedKey.FromKeyringData(keyBytes.ToArray());

        Assert.Single(imported);
        Assert.Equal(Convert.ToHexString(primary.GetFingerprint()).ToLowerInvariant(), imported[0].Fingerprint);
    }

    [Fact]
    public void FromKeyringData_IgnoresForgedSubkeyRevocation()
    {
        var (_, publicRing) = CreateRingWithSigningSubkey();
        var primary = publicRing.GetPublicKeys().First(key => key.IsMasterKey);
        var signingSubkey = publicRing.GetPublicKeys().First(key => !key.IsMasterKey);
        var (attackerSecretRing, _, _, _) = CreateTestKeys("Attacker <attacker@example.com>");
        var forgedRevocation = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        forgedRevocation.InitSign(PgpSignature.SubkeyRevocation,
            attackerSecretRing.GetSecretKeys().First().ExtractPrivateKey(null)!);
        forgedRevocation.SetHashedSubpackets(new PgpSignatureSubpacketGenerator().Generate());
        var forgedSubkey = PgpPublicKey.AddCertification(signingSubkey,
            forgedRevocation.GenerateCertification(primary, signingSubkey));
        var forgedRing = PgpPublicKeyRing.InsertPublicKey(publicRing, forgedSubkey);
        using var keyBytes = new MemoryStream();
        forgedRing.Encode(keyBytes);

        var imported = HelmProvenanceTrustedKey.FromKeyringData(keyBytes.ToArray());

        Assert.Contains(imported, key => key.Fingerprint ==
            Convert.ToHexString(signingSubkey.GetFingerprint()).ToLowerInvariant());
    }

    [Fact]
    public async Task SignAsync_RejectsOversizedChartMetadataBeforeBuffering()
    {
        var work = TempDir();
        try
        {
            var archivePath = Path.Combine(work, "oversized-chart.tgz");
            var oversizedMetadata = RandomNumberGenerator.GetBytes((int)HelmChartArchiveLimits.Default.MaxEntryBytes + 1);
            await using (var file = File.Create(archivePath))
            await using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Optimal))
            await using (var tar = new System.Formats.Tar.TarWriter(gzip))
            {
                await tar.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(
                    System.Formats.Tar.TarEntryType.RegularFile,
                    "oversized-chart/Chart.yaml")
                {
                    DataStream = new MemoryStream(oversizedMetadata, writable: false)
                });
            }

            var (_, _, signingKey, _) = CreateTestKeys();
            var exception = await Assert.ThrowsAsync<ChartArchiveLimitExceededException>(
                () => HelmProvenance.SignAsync(archivePath, signingKey));

            Assert.Equal(ChartArchiveLimitKind.EntryBytes, exception.Limit);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task SignAsync_RejectsOversizedNonMetadataEntry()
    {
        var work = TempDir();
        try
        {
            var archivePath = Path.Combine(work, "oversized-template.tgz");
            var oversizedTemplate = RandomNumberGenerator.GetBytes((int)HelmChartArchiveLimits.Default.MaxEntryBytes + 1);
            await using (var file = File.Create(archivePath))
            await using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Optimal))
            await using (var tar = new System.Formats.Tar.TarWriter(gzip))
            {
                await tar.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(
                    System.Formats.Tar.TarEntryType.RegularFile,
                    "oversized-template/templates/configmap.yaml")
                {
                    DataStream = new MemoryStream(oversizedTemplate, writable: false)
                });
                await tar.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(
                    System.Formats.Tar.TarEntryType.RegularFile,
                    "oversized-template/Chart.yaml")
                {
                    DataStream = new MemoryStream(
                        "apiVersion: v2\nname: oversized-template\nversion: 1.0.0\n"u8.ToArray(),
                        writable: false)
                });
            }

            var (_, _, signingKey, _) = CreateTestKeys();
            var exception = await Assert.ThrowsAsync<ChartArchiveLimitExceededException>(
                () => HelmProvenance.SignAsync(archivePath, signingKey));

            Assert.Equal(ChartArchiveLimitKind.EntryBytes, exception.Limit);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void FromKeyringData_DirectKeyCanSignAssertion_AuthorizesPrimary()
    {
        var keyGenerator = new RsaKeyPairGenerator();
        keyGenerator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var pair = new PgpKeyPair(
            PublicKeyAlgorithmTag.RsaGeneral,
            keyGenerator.GenerateKeyPair(),
            DateTime.UtcNow);
        var signatureGenerator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        signatureGenerator.InitSign(PgpSignature.DirectKey, pair.PrivateKey);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetSignatureCreationTime(false, DateTime.UtcNow);
        packets.SetKeyFlags(true, PgpKeyFlags.CanSign);
        signatureGenerator.SetHashedSubpackets(packets.Generate());
        var signedPrimary = PgpPublicKey.AddCertification(
            pair.PublicKey,
            signatureGenerator.GenerateCertification(pair.PublicKey));

        using var keyBytes = new MemoryStream();
        signedPrimary.Encode(keyBytes);

        var trusted = HelmProvenanceTrustedKey.FromKeyringData(keyBytes.ToArray());

        Assert.Single(trusted);
        Assert.Equal(Convert.ToHexString(signedPrimary.GetFingerprint()).ToLowerInvariant(), trusted[0].Fingerprint);
    }

    [Fact]
    public void FromKeyringData_UnknownCriticalDirectKeyAssertionIsRejected()
    {
        var keyGenerator = new RsaKeyPairGenerator();
        keyGenerator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var pair = new PgpKeyPair(
            PublicKeyAlgorithmTag.RsaGeneral,
            keyGenerator.GenerateKeyPair(),
            DateTime.UtcNow);
        var signatureGenerator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        signatureGenerator.InitSign(PgpSignature.DirectKey, pair.PrivateKey);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetSignatureCreationTime(false, DateTime.UtcNow);
        packets.SetKeyFlags(true, PgpKeyFlags.CanSign);
        AddUnknownCriticalSubpacket(packets);
        signatureGenerator.SetHashedSubpackets(packets.Generate());
        var signedPrimary = PgpPublicKey.AddCertification(
            pair.PublicKey,
            signatureGenerator.GenerateCertification(pair.PublicKey));
        using var keyBytes = new MemoryStream();
        signedPrimary.Encode(keyBytes);

        var exception = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(keyBytes.ToArray()));

        Assert.Contains("No usable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromKeyringData_RevokedDirectKeyAssertionCannotAuthorizePrimary()
    {
        var keyGenerator = new RsaKeyPairGenerator();
        keyGenerator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, keyGenerator.GenerateKeyPair(), DateTime.UtcNow);
        var primary = pair.PublicKey;
        var assertionGenerator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        assertionGenerator.InitSign(PgpSignature.DirectKey, pair.PrivateKey);
        var assertionPackets = new PgpSignatureSubpacketGenerator();
        assertionPackets.SetSignatureCreationTime(false, DateTime.UtcNow.AddMinutes(-2));
        assertionPackets.SetKeyFlags(true, PgpKeyFlags.CanSign);
        assertionGenerator.SetHashedSubpackets(assertionPackets.Generate());
        primary = PgpPublicKey.AddCertification(primary, assertionGenerator.GenerateCertification(primary));

        var revocationGenerator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        revocationGenerator.InitSign(PgpSignature.CertificationRevocation, pair.PrivateKey);
        var revocationPackets = new PgpSignatureSubpacketGenerator();
        revocationPackets.SetSignatureCreationTime(false, DateTime.UtcNow.AddMinutes(-1));
        revocationGenerator.SetHashedSubpackets(revocationPackets.Generate());
        primary = PgpPublicKey.AddCertification(primary, revocationGenerator.GenerateCertification(primary));

        using var keyBytes = new MemoryStream();
        primary.Encode(keyBytes);

        var exception = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(keyBytes.ToArray()));

        Assert.Contains("signing key", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromKeyringData_Version6PrimaryWithoutDirectKeySignatureIsRejected()
    {
        const string userId = "V6 signer <v6@example.com>";
        var keyGenerator = new RsaKeyPairGenerator();
        keyGenerator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, keyGenerator.GenerateKeyPair(), DateTime.UtcNow);
        var v6Packet = new PublicKeyPacket(
            (byte)6,
            pair.PublicKey.Algorithm,
            pair.PublicKey.CreationTime,
            pair.PublicKey.PublicKeyPacket.Key);
        PgpPublicKey primary = new(v6Packet);
        var certificationGenerator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        certificationGenerator.InitSign(PgpSignature.PositiveCertification, pair.PrivateKey);
        var certificationPackets = new PgpSignatureSubpacketGenerator();
        certificationPackets.SetSignatureCreationTime(false, DateTime.UtcNow);
        certificationPackets.SetKeyFlags(true, PgpKeyFlags.CanSign);
        certificationGenerator.SetHashedSubpackets(certificationPackets.Generate());
        primary = PgpPublicKey.AddCertification(
            primary,
            userId,
            certificationGenerator.GenerateCertification(userId, primary));

        using var keyBytes = new MemoryStream();
        primary.Encode(keyBytes);

        var exception = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(keyBytes.ToArray()));

        Assert.Contains("signing key", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromKeyringData_Version6PrimaryRejectsVersion4DirectKeySignature()
    {
        var keyGenerator = new RsaKeyPairGenerator();
        keyGenerator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, keyGenerator.GenerateKeyPair(), DateTime.UtcNow);
        var version6Primary = new PgpPublicKey(new PublicKeyPacket(
            6,
            pair.PublicKey.Algorithm,
            pair.PublicKey.CreationTime,
            pair.PublicKey.PublicKeyPacket.Key));

        // The RSA signature verifies mathematically, but a V6 key cannot use V4
        // policy signatures to establish flags or satisfy the required Direct Key assertion.
        var directKeyGenerator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        directKeyGenerator.InitSign(PgpSignature.DirectKey, pair.PrivateKey);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetSignatureCreationTime(false, DateTime.UtcNow);
        packets.SetKeyFlags(true, PgpKeyFlags.CanSign);
        directKeyGenerator.SetHashedSubpackets(packets.Generate());
        var directKeySignature = directKeyGenerator.GenerateCertification(version6Primary);
        Assert.Equal(4, directKeySignature.Version);
        version6Primary = PgpPublicKey.AddCertification(version6Primary, directKeySignature);

        using var keyBytes = new MemoryStream();
        version6Primary.Encode(keyBytes);

        var exception = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(keyBytes.ToArray()));

        Assert.Contains("signing key", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsUsableSigningKeyFor_MixedVersionPrimaryAndSubkeyAreRejected()
    {
        var (_, publicRing) = CreateRingWithSigningSubkey();
        var masterV4 = publicRing.GetPublicKeys().Single(key => key.IsMasterKey);
        var subkeyV4 = publicRing.GetPublicKeys().Single(key => !key.IsMasterKey);
        var masterV6 = new PgpPublicKey(new PublicKeyPacket(
            6,
            masterV4.Algorithm,
            masterV4.CreationTime,
            masterV4.PublicKeyPacket.Key));

        Assert.False(HelmProvenanceTrustedKey.IsUsableSigningKeyFor(subkeyV4, masterV6));
    }

    [Fact]
    public void FromKeyringData_Version6UidCertificationCannotReplaceDirectKeyExpiry()
    {
        const string userId = "V6 signer <v6@example.com>";
        var createdAt = DateTime.UtcNow.AddMinutes(-1);
        var keyGenerator = new RsaKeyPairGenerator();
        keyGenerator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, keyGenerator.GenerateKeyPair(), createdAt);
        var v6Packet = new PublicKeyPacket(
            6,
            pair.PublicKey.Algorithm,
            pair.PublicKey.CreationTime,
            pair.PublicKey.PublicKeyPacket.Key);
        PgpPublicKey primary = new(v6Packet);

        var directKeyGenerator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        directKeyGenerator.InitSign(PgpSignature.DirectKey, pair.PrivateKey);
        var directKeyPackets = new PgpSignatureSubpacketGenerator();
        directKeyPackets.SetSignatureCreationTime(false, createdAt.AddSeconds(1));
        directKeyPackets.SetKeyExpirationTime(false, 5);
        directKeyPackets.SetKeyFlags(true, PgpKeyFlags.CanSign);
        directKeyGenerator.SetHashedSubpackets(directKeyPackets.Generate());
        primary = PgpPublicKey.AddCertification(primary, directKeyGenerator.GenerateCertification(primary));

        // A later UID certification without KeyExpirationTime must not erase V6's
        // primary-key-wide expiration assertion from the Direct Key signature.
        var certificationGenerator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        certificationGenerator.InitSign(PgpSignature.PositiveCertification, pair.PrivateKey);
        var certificationPackets = new PgpSignatureSubpacketGenerator();
        certificationPackets.SetSignatureCreationTime(false, DateTime.UtcNow);
        certificationPackets.SetKeyFlags(true, PgpKeyFlags.CanSign);
        certificationGenerator.SetHashedSubpackets(certificationPackets.Generate());
        primary = PgpPublicKey.AddCertification(
            primary,
            userId,
            certificationGenerator.GenerateCertification(userId, primary));

        using var keyBytes = new MemoryStream();
        primary.Encode(keyBytes);

        var exception = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(keyBytes.ToArray()));

        Assert.Contains("signing key", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromSecretKeyData_Version6SigningKeyIsNotSupported()
    {
        var keyGenerator = new RsaKeyPairGenerator();
        keyGenerator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var originalPair = new PgpKeyPair(
            PublicKeyAlgorithmTag.RsaGeneral,
            keyGenerator.GenerateKeyPair(),
            DateTime.UtcNow);
        var version6Public = new PgpPublicKey(new PublicKeyPacket(
            (byte)6,
            originalPair.PublicKey.Algorithm,
            originalPair.PublicKey.CreationTime,
            originalPair.PublicKey.PublicKeyPacket.Key));
        var version6Pair = new PgpKeyPair(version6Public, originalPair.PrivateKey);
        var packets = new PgpSignatureSubpacketGenerator();
        packets.SetKeyFlags(true, PgpKeyFlags.CanSign | PgpKeyFlags.CanCertify);
        var secretKey = new PgpSecretKey(
            (int)PgpSignature.PositiveCertification,
            version6Pair,
            "V6 signer <v6@example.com>",
            SymmetricKeyAlgorithmTag.Null,
            HashAlgorithmTag.Sha512,
            (char[])null!,
            false,
            packets.Generate(),
            null,
            new SecureRandom());
        using var keyBytes = new MemoryStream();
        secretKey.Encode(keyBytes);

        Assert.Throws<NotSupportedException>(
            () => HelmProvenanceSigningKey.FromSecretKeyData(keyBytes.ToArray()));
    }

    [Fact]
    public void IsUsableSigningKeyFor_Version6PrimaryWithoutDirectKeySignatureRejectsSigningSubkey()
    {
        var (secretRing, publicRing) = CreateRingWithSigningSubkey();
        var secretKeys = secretRing.GetSecretKeys().ToArray();
        var primarySecret = secretKeys[0];
        var subkeySecret = secretKeys[1];
        var masterV4 = publicRing.GetPublicKeys().Single(key => key.IsMasterKey);
        var subkey = publicRing.GetPublicKeys().Single(key => !key.IsMasterKey);
        var masterV6 = new PgpPublicKey(new PublicKeyPacket(
            (byte)6,
            masterV4.Algorithm,
            masterV4.CreationTime,
            masterV4.PublicKeyPacket.Key));

        foreach (var oldBinding in subkey.GetKeySignatures()
                     .Where(signature => signature.SignatureType == PgpSignature.SubkeyBinding)
                     .ToArray())
        {
            subkey = PgpPublicKey.RemoveCertification(subkey, oldBinding);
        }

        var backSignatureGenerator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        backSignatureGenerator.InitSign(PgpSignature.PrimaryKeyBinding, subkeySecret.ExtractPrivateKey(null)!);
        backSignatureGenerator.SetHashedSubpackets(new PgpSignatureSubpacketGenerator().Generate());
        var bindingGenerator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        bindingGenerator.InitSign(PgpSignature.SubkeyBinding, primarySecret.ExtractPrivateKey(null)!);
        var bindingPackets = new PgpSignatureSubpacketGenerator();
        bindingPackets.SetSignatureCreationTime(false, DateTime.UtcNow);
        bindingPackets.SetKeyFlags(true, PgpKeyFlags.CanSign);
        bindingPackets.SetEmbeddedSignature(
            false,
            backSignatureGenerator.GenerateCertification(masterV6, subkey));
        bindingGenerator.SetHashedSubpackets(bindingPackets.Generate());
        var binding = bindingGenerator.GenerateCertification(masterV6, subkey);
        binding.InitVerify(masterV6);
        Assert.True(binding.VerifyCertification(masterV6, subkey));
        subkey = PgpPublicKey.AddCertification(subkey, binding);

        Assert.False(HelmProvenanceTrustedKey.IsUsableSigningKeyFor(subkey, masterV6));
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
    public void FromKeyringData_UnknownCriticalSubkeyBindingCannotAuthorizeSigning()
    {
        var (secretRing, publicRing) = CreateRingWithSigningSubkey();
        var masterSecret = secretRing.GetSecretKeys().First(key => key.IsMasterKey);
        var master = publicRing.GetPublicKeys().First(key => key.IsMasterKey);
        var subkey = publicRing.GetPublicKeys().First(key => !key.IsMasterKey);
        var priorBinding = subkey.GetKeySignatures().First(signature => signature.SignatureType == PgpSignature.SubkeyBinding);
        var backSignature = priorBinding.GetHashedSubPackets()!.GetEmbeddedSignatures()[0];

        var bindingGenerator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha512);
        bindingGenerator.InitSign(PgpSignature.SubkeyBinding, masterSecret.ExtractPrivateKey(null)!);
        var bindingPackets = new PgpSignatureSubpacketGenerator();
        bindingPackets.SetSignatureCreationTime(false, DateTime.UtcNow);
        bindingPackets.SetKeyFlags(true, PgpKeyFlags.CanSign);
        AddUnknownCriticalSubpacket(bindingPackets);
        bindingPackets.AddEmbeddedSignature(false, backSignature);
        bindingGenerator.SetHashedSubpackets(bindingPackets.Generate());
        var binding = bindingGenerator.GenerateCertification(master, subkey);
        foreach (var previousBinding in subkey.GetKeySignatures()
                     .Where(signature => signature.SignatureType == PgpSignature.SubkeyBinding)
                     .ToArray())
        {
            subkey = PgpPublicKey.RemoveCertification(subkey, previousBinding);
        }
        subkey = PgpPublicKey.AddCertification(subkey, binding);
        publicRing = PgpPublicKeyRing.InsertPublicKey(publicRing, subkey);
        using var keyBytes = new MemoryStream();
        publicRing.Encode(keyBytes);

        var exception = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(keyBytes.ToArray()));

        Assert.Contains("No usable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromKeyringData_UnknownCriticalEmbeddedBackSignatureCannotAuthorizeSigning()
    {
        var keyGenerator = new RsaKeyPairGenerator();
        keyGenerator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var primaryPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, keyGenerator.GenerateKeyPair(), DateTime.UtcNow);
        var signingPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, keyGenerator.GenerateKeyPair(), primaryPair.PublicKey.CreationTime);
        var primaryFlags = new PgpSignatureSubpacketGenerator();
        primaryFlags.SetKeyFlags(true, PgpKeyFlags.CanCertify);
        var keyRingGenerator = new PgpKeyRingGenerator(
            (int)PgpSignature.PositiveCertification,
            primaryPair,
            "Back signature <backsignature@example.com>",
            SymmetricKeyAlgorithmTag.Null,
            HashAlgorithmTag.Sha512,
            (char[])null!,
            false,
            primaryFlags.Generate(),
            null,
            new SecureRandom());
        var signingFlags = new PgpSignatureSubpacketGenerator();
        signingFlags.SetKeyFlags(true, PgpKeyFlags.CanSign);
        keyRingGenerator.AddSubKey(signingPair, signingFlags.Generate(), null);
        var publicRing = keyRingGenerator.GeneratePublicKeyRing();
        var master = publicRing.GetPublicKeys().First(key => key.IsMasterKey);
        var subkey = publicRing.GetPublicKeys().First(key => !key.IsMasterKey);

        var backSignatureGenerator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        backSignatureGenerator.InitSign(PgpSignature.PrimaryKeyBinding, signingPair.PrivateKey);
        var backSignaturePackets = new PgpSignatureSubpacketGenerator();
        backSignaturePackets.SetSignatureCreationTime(false, DateTime.UtcNow);
        AddUnknownCriticalSubpacket(backSignaturePackets);
        backSignatureGenerator.SetHashedSubpackets(backSignaturePackets.Generate());
        var backSignature = backSignatureGenerator.GenerateCertification(master, subkey);

        var bindingGenerator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha512);
        bindingGenerator.InitSign(PgpSignature.SubkeyBinding, primaryPair.PrivateKey);
        var bindingPackets = new PgpSignatureSubpacketGenerator();
        bindingPackets.SetSignatureCreationTime(false, DateTime.UtcNow);
        bindingPackets.SetKeyFlags(true, PgpKeyFlags.CanSign);
        bindingPackets.AddEmbeddedSignature(false, backSignature);
        bindingGenerator.SetHashedSubpackets(bindingPackets.Generate());
        var binding = bindingGenerator.GenerateCertification(master, subkey);

        foreach (var previousBinding in subkey.GetKeySignatures()
                     .Where(signature => signature.SignatureType == PgpSignature.SubkeyBinding)
                     .ToArray())
        {
            subkey = PgpPublicKey.RemoveCertification(subkey, previousBinding);
        }
        subkey = PgpPublicKey.AddCertification(subkey, binding);
        publicRing = PgpPublicKeyRing.InsertPublicKey(publicRing, subkey);

        var embeddedSignatures = binding.GetHashedSubPackets()!.GetEmbeddedSignatures();
        Assert.Equal(1, embeddedSignatures.Count);
        var embedded = embeddedSignatures[0];
        Assert.Contains((SignatureSubpacketTag)100, embedded.GetHashedSubPackets()!.GetCriticalTags()!);
        using var keyBytes = new MemoryStream();
        publicRing.Encode(keyBytes);

        var exception = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(keyBytes.ToArray()));

        Assert.Contains("No usable", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FromKeyringData_SigningSubkeyWithoutValidBackSignatureIsRejected(bool forgeBackSignature)
    {
        var (secretRing, publicRing) = CreateRingWithSigningSubkey();
        var primarySecret = secretRing.GetSecretKeys().First();
        var master = publicRing.GetPublicKeys().Single(key => key.IsMasterKey);
        var subkey = publicRing.GetPublicKeys().Single(key => !key.IsMasterKey);
        foreach (var binding in subkey.GetKeySignatures()
                     .Where(signature => signature.SignatureType == PgpSignature.SubkeyBinding)
                     .ToArray())
        {
            subkey = PgpPublicKey.RemoveCertification(subkey, binding);
        }

        var bindingGenerator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        bindingGenerator.InitSign(PgpSignature.SubkeyBinding, primarySecret.ExtractPrivateKey(null)!);
        var bindingPackets = new PgpSignatureSubpacketGenerator();
        bindingPackets.SetSignatureCreationTime(false, DateTime.UtcNow.AddMinutes(1));
        bindingPackets.SetKeyFlags(true, PgpKeyFlags.CanSign);
        if (forgeBackSignature)
        {
            var attackerGenerator = new RsaKeyPairGenerator();
            attackerGenerator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
            var attackerPair = new PgpKeyPair(
                PublicKeyAlgorithmTag.RsaGeneral,
                attackerGenerator.GenerateKeyPair(),
                DateTime.UtcNow);
            var backSignatureGenerator = new PgpSignatureGenerator(
                PublicKeyAlgorithmTag.RsaGeneral,
                HashAlgorithmTag.Sha256);
            backSignatureGenerator.InitSign(PgpSignature.PrimaryKeyBinding, attackerPair.PrivateKey);
            backSignatureGenerator.SetHashedSubpackets(new PgpSignatureSubpacketGenerator().Generate());
            bindingPackets.SetEmbeddedSignature(
                false,
                backSignatureGenerator.GenerateCertification(master, subkey));
        }

        bindingGenerator.SetHashedSubpackets(bindingPackets.Generate());
        var forgedSubkey = PgpPublicKey.AddCertification(
            subkey,
            bindingGenerator.GenerateCertification(master, subkey));
        var forgedRing = PgpPublicKeyRing.InsertPublicKey(publicRing, forgedSubkey);

        using var ms = new MemoryStream();
        forgedRing.Encode(ms);
        var exception = Assert.Throws<ArgumentException>(
            () => HelmProvenanceTrustedKey.FromKeyringData(ms.ToArray()));

        Assert.Contains("signing key", exception.Message, StringComparison.OrdinalIgnoreCase);
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
    public void ExtractSha256_ReadsQuotedFilesKeyContainingColon()
    {
        const string archiveName = "chart: prod.tgz";
        var digest = new string('a', 64);
        var prov = $"""
            -----BEGIN PGP SIGNED MESSAGE-----
            Hash: SHA512

            name: demo

            ...
            files:
              {System.Text.Json.JsonSerializer.Serialize(archiveName)}: sha256:{digest}

            -----BEGIN PGP SIGNATURE-----

            AAAA
            -----END PGP SIGNATURE-----
            """;

        Assert.Equal(digest, HelmProvenance.ExtractSha256(prov, archiveName));
        Assert.Equal("sha256:" + digest, HelmProvenance.ExtractMetadata(prov)["files." + archiveName]);
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

    [Fact]
    public async Task SignAsync_UsesRootChartMetadataWhenSubchartAppearsFirst()
    {
        var work = TempDir();
        try
        {
            var archivePath = Path.Combine(work, "rootchart-0.1.0.tgz");
            await using (var file = File.Create(archivePath))
            await using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Optimal))
            using (var tar = new System.Formats.Tar.TarWriter(gzip))
            {
                var childYaml = Encoding.UTF8.GetBytes(DefaultChartYaml.Replace("provchart", "dependency", StringComparison.Ordinal));
                var rootYaml = Encoding.UTF8.GetBytes(DefaultChartYaml.Replace("provchart", "rootchart", StringComparison.Ordinal));
                await tar.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(
                    System.Formats.Tar.TarEntryType.RegularFile, "rootchart/charts/dependency/Chart.yaml")
                {
                    DataStream = new MemoryStream(childYaml)
                });
                await tar.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(
                    System.Formats.Tar.TarEntryType.RegularFile, "rootchart/Chart.yaml")
                {
                    DataStream = new MemoryStream(rootYaml)
                });
            }

            var (_, _, signingKey, _) = CreateTestKeys();
            var provPath = await HelmProvenance.SignAsync(archivePath, signingKey);
            var metadata = HelmProvenance.ExtractMetadata(await File.ReadAllTextAsync(provPath));

            Assert.Equal("rootchart", metadata["name"]);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task SignAsync_RejectsMultipleRootChartMetadataFiles()
    {
        var work = TempDir();
        try
        {
            var archivePath = Path.Combine(work, "rootchart-0.1.0.tgz");
            await using (var file = File.Create(archivePath))
            await using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Optimal))
            using (var tar = new System.Formats.Tar.TarWriter(gzip))
            {
                var decoyYaml = Encoding.UTF8.GetBytes(DefaultChartYaml.Replace("provchart", "decoy", StringComparison.Ordinal));
                var rootYaml = Encoding.UTF8.GetBytes(DefaultChartYaml.Replace("provchart", "rootchart", StringComparison.Ordinal));
                await tar.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(
                    System.Formats.Tar.TarEntryType.RegularFile, "decoy/Chart.yaml")
                {
                    DataStream = new MemoryStream(decoyYaml)
                });
                await tar.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(
                    System.Formats.Tar.TarEntryType.RegularFile, "rootchart/Chart.yaml")
                {
                    DataStream = new MemoryStream(rootYaml)
                });
            }

            var (_, _, signingKey, _) = CreateTestKeys();

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => HelmProvenance.SignAsync(archivePath, signingKey));

            Assert.Contains("multiple root Chart.yaml", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Theory]
    [InlineData("../Chart.yaml")]
    [InlineData("root/../payload.txt")]
    public async Task SignAsync_RejectsUnsafeArchiveEntryPaths(string unsafeEntryName)
    {
        var work = TempDir();
        try
        {
            var archivePath = Path.Combine(work, "rootchart-0.1.0.tgz");
            await using (var file = File.Create(archivePath))
            await using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Optimal))
            using (var tar = new System.Formats.Tar.TarWriter(gzip))
            {
                var unsafeContent = Encoding.UTF8.GetBytes("untrusted archive content");
                await tar.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(
                    System.Formats.Tar.TarEntryType.RegularFile, unsafeEntryName)
                {
                    DataStream = new MemoryStream(unsafeContent)
                });

                var rootYaml = Encoding.UTF8.GetBytes(DefaultChartYaml.Replace("provchart", "rootchart", StringComparison.Ordinal));
                await tar.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(
                    System.Formats.Tar.TarEntryType.RegularFile, "rootchart/Chart.yaml")
                {
                    DataStream = new MemoryStream(rootYaml)
                });
            }

            var (_, _, signingKey, _) = CreateTestKeys();

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => HelmProvenance.SignAsync(archivePath, signingKey));

            Assert.Contains("unsafe", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task SignAsync_AllowsExplicitDirectoryEntriesWithTrailingSlash()
    {
        var work = TempDir();
        try
        {
            var archivePath = Path.Combine(work, "rootchart-0.1.0.tgz");
            await using (var file = File.Create(archivePath))
            await using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Optimal))
            using (var tar = new System.Formats.Tar.TarWriter(gzip))
            {
                await tar.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(
                    System.Formats.Tar.TarEntryType.Directory, "rootchart/"));
                var rootYaml = Encoding.UTF8.GetBytes(DefaultChartYaml.Replace("provchart", "rootchart", StringComparison.Ordinal));
                await tar.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(
                    System.Formats.Tar.TarEntryType.RegularFile, "rootchart/Chart.yaml")
                {
                    DataStream = new MemoryStream(rootYaml)
                });
            }

            var (_, _, signingKey, trustedKey) = CreateTestKeys();

            var provenancePath = await HelmProvenance.SignAsync(archivePath, signingKey);
            var result = await HelmProvenance.VerifyAsync(archivePath, [trustedKey], provenancePath);

            Assert.True(result.IsValid, result.FailureReason);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
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
            var archiveName = Path.GetFileName(tgz);
            var caseVariantArchiveName = char.ToUpperInvariant(archiveName[0]) + archiveName[1..];

            // A case-only filename variant carries this archive's digest, but the exact
            // archive-name entry carries a different digest and must take precedence.
            var prov = $"""
                -----BEGIN PGP SIGNED MESSAGE-----
                Hash: SHA512

                name: provchart

                ...
                files:
                  {caseVariantArchiveName}: sha256:{actualHash}
                  {archiveName}: sha256:{siblingHash}

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
    public async Task Verify_ContinuesPastExpiredTrustedCopyWhenRenewedKeyMatches()
    {
        var work = TempDir();
        try
        {
            const string userId = "Rotated signer <rotate@example.com>";
            var (secretRing, publicRing, signingKey, staleTrustedKey) = CreateTestKeys(
                userId,
                validitySeconds: 5);
            var primarySecret = secretRing.GetSecretKeys().First();
            var primary = publicRing.GetPublicKeys().Single(key => key.IsMasterKey);
            var renewal = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
            renewal.InitSign(PgpSignature.PositiveCertification, primarySecret.ExtractPrivateKey(null)!);
            var renewalPackets = new PgpSignatureSubpacketGenerator();
            renewalPackets.SetSignatureCreationTime(false, DateTime.UtcNow.AddSeconds(2));
            renewalPackets.SetKeyFlags(true, PgpKeyFlags.CanSign | PgpKeyFlags.CanCertify);
            renewal.SetHashedSubpackets(renewalPackets.Generate());
            var renewedPrimary = PgpPublicKey.AddCertification(primary, userId,
                renewal.GenerateCertification(userId, primary));
            var renewedRing = PgpPublicKeyRing.InsertPublicKey(publicRing, renewedPrimary);
            using var keyBytes = new MemoryStream();
            renewedRing.Encode(keyBytes);
            var renewedTrustedKey = Assert.Single(HelmProvenanceTrustedKey.FromKeyringData(keyBytes.ToArray()));

            var tgz = await CreateTestChartArchiveAsync(work);
            await HelmProvenance.SignAsync(tgz, signingKey);
            await Task.Delay(TimeSpan.FromSeconds(6));

            var result = await HelmProvenance.VerifyAsync(tgz, [staleTrustedKey, renewedTrustedKey]);

            Assert.True(result.IsValid, result.FailureReason);
            Assert.Equal(renewedTrustedKey.Fingerprint, result.SignerFingerprint);
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
            var provenancePath = await HelmProvenance.SignAsync(tgz, signingKey);
            var provenance = await File.ReadAllTextAsync(provenancePath);
            var signatureStart = provenance.IndexOf("-----BEGIN PGP SIGNATURE-----", StringComparison.Ordinal);
            Assert.NotEqual(-1, signatureStart);
            using var decodedSignature = PgpUtilities.GetDecoderStream(
                new MemoryStream(Encoding.ASCII.GetBytes(provenance[signatureStart..])));
            var signatureList = Assert.IsType<PgpSignatureList>(new PgpObjectFactory(decodedSignature).NextPgpObject());
            Assert.Equal(PgpSignature.CanonicalTextDocument, signatureList[0].SignatureType);

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
