using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HelmSharp.Registry;

/// <summary>Validates bounded Helm OCI content before it is exposed to a caller.</summary>
internal static class OciChartContentValidator
{
    internal const string ManifestType = "application/vnd.oci.image.manifest.v1+json";
    internal const string ConfigType = "application/vnd.cncf.helm.config.v1+json";
    internal const string ChartType = "application/vnd.cncf.helm.chart.content.v1.tar+gzip";
    internal const string ProvenanceType = "application/vnd.cncf.helm.chart.provenance.v1.prov";
    private const int MetadataLimit = 1024 * 1024;
    private const int ChartLimit = 100 * 1024 * 1024;
    private static readonly Regex DigestPattern = new(@"\A(?:sha256:[a-f0-9]{64}|sha512:[a-f0-9]{128})\z", RegexOptions.CultureInvariant);

    internal sealed record Descriptor(string Digest, long Size);
    internal sealed record Manifest(Descriptor Config, Descriptor Chart, Descriptor? Provenance);
    internal sealed record Content(string Name, string Version, byte[] Chart, byte[]? Provenance);

    internal static async Task<Manifest> ReadManifestAsync(Stream stream, string? expectedDigest, CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(stream, MetadataLimit, cancellationToken);
        if (expectedDigest is not null)
            VerifyDigest(bytes, expectedDigest);
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 2 || root.GetProperty("mediaType").GetString() != ManifestType)
                throw Invalid();
            var config = ParseDescriptor(root.GetProperty("config"), ConfigType, MetadataLimit);
            Descriptor? chart = null, provenance = null;
            foreach (var layer in root.GetProperty("layers").EnumerateArray())
            {
                var type = layer.GetProperty("mediaType").GetString();
                if (type == ChartType && chart is null)
                    chart = ParseDescriptor(layer, ChartType, ChartLimit);
                else if (type == ProvenanceType && provenance is null)
                    provenance = ParseDescriptor(layer, ProvenanceType, MetadataLimit);
                else
                    throw Invalid();
            }
            return new Manifest(config, chart ?? throw Invalid(), provenance);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Invalid Helm OCI manifest.", ex);
        }
    }

    internal static async Task<Content> ReadContentAsync(Manifest manifest, Stream config, Stream chart, Stream? provenance, CancellationToken cancellationToken)
    {
        var configBytes = await ReadDescriptorAsync(config, manifest.Config, MetadataLimit, cancellationToken);
        var chartBytes = await ReadDescriptorAsync(chart, manifest.Chart, ChartLimit, cancellationToken);
        if ((manifest.Provenance is null) != (provenance is null))
            throw Invalid();
        var provenanceBytes = provenance is null ? null : await ReadDescriptorAsync(provenance, manifest.Provenance!, MetadataLimit, cancellationToken);
        try
        {
            using var json = JsonDocument.Parse(configBytes);
            var name = json.RootElement.GetProperty("name").GetString();
            var version = json.RootElement.GetProperty("version").GetString();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
                throw Invalid();
            return new Content(name, version, chartBytes, provenanceBytes);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("Invalid Helm OCI config.", ex);
        }
    }

    private static Descriptor ParseDescriptor(JsonElement value, string type, int limit)
    {
        var mediaType = value.GetProperty("mediaType").GetString();
        var digest = value.GetProperty("digest").GetString();
        var size = value.GetProperty("size").GetInt64();
        if (mediaType != type || digest is null || !DigestPattern.IsMatch(digest) || size < 0 || size > limit)
            throw Invalid();
        return new Descriptor(digest, size);
    }

    private static async Task<byte[]> ReadDescriptorAsync(Stream stream, Descriptor descriptor, int limit, CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(stream, (int)Math.Min(descriptor.Size, limit), cancellationToken);
        if (bytes.LongLength != descriptor.Size)
            throw Invalid();
        VerifyDigest(bytes, descriptor.Digest);
        return bytes;
    }

    private static void VerifyDigest(byte[] bytes, string digest)
    {
        if (!DigestPattern.IsMatch(digest))
            throw Invalid();
        var hash = digest.StartsWith("sha256:", StringComparison.Ordinal) ? SHA256.HashData(bytes) : SHA512.HashData(bytes);
        if (!string.Equals(Convert.ToHexString(hash), digest[(digest.IndexOf(':') + 1)..], StringComparison.OrdinalIgnoreCase))
            throw Invalid();
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            if (output.Length + read > limit)
                throw Invalid();
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private static InvalidDataException Invalid() => new("Invalid or oversized Helm OCI content.");
}
