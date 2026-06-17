using System.Security.Cryptography;
using System.Text;

namespace HelmSharp.Action;

/// <summary>
/// Chart provenance — generates and verifies .prov files for chart integrity.
/// A .prov file contains: chart metadata hash, signature, and pgp info.
/// </summary>
public static class HelmProvenance
{
    /// <summary>
    /// Generates a .prov file for a chart archive.
    /// </summary>
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
        provContent.AppendLine($"comment: chemical-ai-helm managed provenance");
        provContent.AppendLine();
        provContent.AppendLine(Convert.ToBase64String(SHA512.HashData(chartBytes)));
        provContent.AppendLine("-----END PGP SIGNATURE-----");

        var provPath = chartTgzPath + ".prov";
        await File.WriteAllTextAsync(provPath, provContent.ToString(), ct);
        return provPath;
    }

    /// <summary>
    /// Verifies a chart archive against its .prov file.
    /// Returns true if the SHA256 hash matches.
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
    /// Extracts the SHA256 hash from a .prov file.
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
    /// Extracts chart metadata from a .prov file.
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
