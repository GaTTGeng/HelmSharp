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
        char[]? passphrase = null)
    {
        var kpg = new RsaKeyPairGenerator();
        kpg.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var kp = kpg.GenerateKeyPair();

        var hashed = new PgpSignatureSubpacketGenerator();
        hashed.SetKeyFlags(true, PgpKeyFlags.CanSign | PgpKeyFlags.CanCertify);
        hashed.SetPreferredSymmetricAlgorithms(true, [(int)SymmetricKeyAlgorithmTag.Aes256]);
        hashed.SetPreferredHashAlgorithms(true, [(int)HashAlgorithmTag.Sha512, (int)HashAlgorithmTag.Sha256]);

        var keyPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, kp, DateTime.UtcNow);
        var encAlgo = passphrase is null ? SymmetricKeyAlgorithmTag.Null : SymmetricKeyAlgorithmTag.Aes256;
        var krg = new PgpKeyRingGenerator(
            (int)PgpSignature.PositiveCertification,
            keyPair,
            userId,
            encAlgo,
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

    private static async Task<string> CreateTestChartArchiveAsync(string workDir)
    {
        var chartDir = Path.Combine(workDir, "provchart");
        Directory.CreateDirectory(Path.Combine(chartDir, "templates"));
        await File.WriteAllTextAsync(Path.Combine(chartDir, "Chart.yaml"), """
            apiVersion: v2
            name: provchart
            description: provenance test chart
            type: application
            version: 0.1.0
            appVersion: "1.0"
            """);
        await File.WriteAllTextAsync(Path.Combine(chartDir, "values.yaml"), "replicaCount: 1\n");
        await File.WriteAllTextAsync(Path.Combine(chartDir, "templates", "cm.yaml"), """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: provchart-cm
            data:
              hello: world
            """);

        var tgzPath = Path.Combine(workDir, "provchart-0.1.0.tgz");
        await PackChartAsync(chartDir, tgzPath);
        return tgzPath;
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
            var passphrase = "s3cret-pass".ToCharArray();
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
