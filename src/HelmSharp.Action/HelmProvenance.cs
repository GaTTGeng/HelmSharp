using System.Security.Cryptography;
using System.Text;

namespace HelmSharp.Action;

/// <summary>
/// Chart provenance — generates and verifies .prov files for chart integrity.
/// A .prov file contains: chart metadata hash, signature, and pgp info.
/// </summary>
/// <remarks>
/// The .prov file is written in Helm's clearsigned armor layout for format compatibility, but
/// the signature block currently carries a base64 SHA-512 digest of the archive rather than a
/// real PGP signature. Integrity checking is therefore hash-based only; do not treat these
/// files as cryptographically signed provenance.
/// </remarks>
public static class HelmProvenance
{
    /// <summary>
    /// Generates a .prov file for a chart archive, written next to it as
    /// <c>{archive}.prov</c>. Records the archive name, SHA-256 digest, and UTC timestamp.
    /// </summary>
    /// <param name="chartTgzPath">Chart archive to describe.</param>
    /// <param name="keyId">Optional PGP key identifier recorded in the file (not used for signing).</param>
    /// <returns>Path of the generated .prov file.</returns>
    public static async Task<string> GenerateProvFileAsync(
        string chartTgzPath,
        string? keyId = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(chartTgzPath))
            throw new FileNotFoundException($"Chart archive not found: {chartTgzPath}");

        var chartBytes = await File.ReadAllBytesAsync(chartTgzPath, ct);
        var sha256 = Convert.ToHexString(SHA256.HashData(chartBytes)).ToLowerInvariant();
        var chartName = Path.GetFileNameWithoutExtension(chartTgzPath);

        var provContent = new StringBuilder();
        provContent.AppendLine("-----BEGIN PGP SIGNED MESSAGE-----");
        provContent.AppendLine("Hash: SHA256");
        provContent.AppendLine();
        provContent.AppendLine($"name: {chartName}");
        provContent.AppendLine($"sha256: {sha256}");
        provContent.AppendLine($"generated: {DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.ffffffZ}");

        if (keyId is not null)
            provContent.AppendLine($"pgpKeyID: {keyId}");

        provContent.AppendLine("-----BEGIN PGP SIGNATURE-----");
        provContent.AppendLine($"comment: HelmSharp managed provenance");
        provContent.AppendLine();
        provContent.AppendLine(Convert.ToBase64String(SHA512.HashData(chartBytes)));
        provContent.AppendLine("-----END PGP SIGNATURE-----");

        var provPath = chartTgzPath + ".prov";
        await File.WriteAllTextAsync(provPath, provContent.ToString(), ct);
        return provPath;
    }

    /// <summary>
    /// Verifies a chart archive against its .prov file.
    /// Returns true if the SHA256 hash matches; a missing .prov file yields false.
    /// Only hash integrity is checked — the signature block is not verified.
    /// </summary>
    public static async Task<bool> VerifyAsync(
        string chartTgzPath,
        string? provPath = null,
        CancellationToken ct = default)
    {
        provPath ??= chartTgzPath + ".prov";
        if (!File.Exists(provPath))
            return false;

        var chartBytes = await File.ReadAllBytesAsync(chartTgzPath, ct);
        var actualHash = Convert.ToHexString(SHA256.HashData(chartBytes)).ToLowerInvariant();

        var provContent = await File.ReadAllTextAsync(provPath, ct);
        var expectedHash = ExtractSha256(provContent);

        return expectedHash is not null &&
               string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the SHA256 hash from a .prov file (the hex value following the
    /// <c>sha256:</c> key), or null when the key is absent.
    /// </summary>
    public static string? ExtractSha256(string provContent)
    {
        foreach (var line in provContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                return trimmed["sha256:".Length..].Trim();
        }
        return null;
    }

    /// <summary>
    /// Extracts chart metadata key/value pairs from the clearsigned message body of a .prov
    /// file (name, sha256, generated, pgpKeyID). The armor headers and signature block are
    /// excluded. Keys are compared case-insensitively.
    /// </summary>
    public static Dictionary<string, string> ExtractMetadata(string provContent)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inMessage = false;

        foreach (var line in provContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed == "-----BEGIN PGP SIGNED MESSAGE-----")
            {
                inMessage = true;
                continue;
            }
            if (trimmed == "-----BEGIN PGP SIGNATURE-----")
                break;

            if (inMessage && trimmed.StartsWith("Hash:"))
                continue;

            if (inMessage && trimmed.Contains(':'))
            {
                var colonIndex = trimmed.IndexOf(':');
                var key = trimmed[..colonIndex].Trim();
                var value = trimmed[(colonIndex + 1)..].Trim();
                result[key] = value;
            }
        }

        return result;
    }
}
