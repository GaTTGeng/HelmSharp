using System.Security.Cryptography;
using System.Text.Json;
using HelmSharp.Registry;

namespace HelmSharp.Tests;

/// <summary>Validates Helm OCI manifest and blob integrity before use.</summary>
public sealed class OciChartContentValidatorTests
{
    private static readonly byte[] Config = "{\"name\":\"builtins\",\"version\":\"1.2.3\"}"u8.ToArray();
    private static readonly byte[] Chart = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Charts", "builtins-1.2.3.tgz"));
    private static readonly byte[] Provenance = "signature"u8.ToArray();

    [Fact]
    public async Task ValidContent_ReturnsMetadataAndVerifiedBytes()
    {
        var manifestBytes = Manifest(Descriptor(OciChartContentValidator.ChartType, Chart), Descriptor(OciChartContentValidator.ProvenanceType, Provenance));
        var manifest = await OciChartContentValidator.ReadManifestAsync(Stream(manifestBytes), Digest(manifestBytes), CancellationToken.None);
        var content = await OciChartContentValidator.ReadContentAsync(manifest, Stream(Config), Stream(Chart), Stream(Provenance), CancellationToken.None);
        Assert.Equal("builtins", content.Name);
        Assert.Equal("1.2.3", content.Version);
        Assert.Equal(Chart, content.Chart);
        Assert.Equal(Provenance, content.Provenance);
    }

    [Fact]
    public async Task InvalidManifest_RejectsMissingDuplicateAndUnknownLayers()
    {
        var chart = Descriptor(OciChartContentValidator.ChartType, Chart);
        foreach (var bytes in new[] { Manifest(), Manifest(chart, chart), Manifest(chart, Descriptor("unknown", Chart)), Manifest(Descriptor("unknown", Chart)) })
            await Assert.ThrowsAsync<InvalidDataException>(() => OciChartContentValidator.ReadManifestAsync(Stream(bytes), null, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidDescriptorsAndBodies_AreRejected()
    {
        var valid = Descriptor(OciChartContentValidator.ChartType, Chart);
        foreach (var layer in new object[]
        {
            new { mediaType = OciChartContentValidator.ChartType, digest = Digest(Chart), size = 101L * 1024 * 1024 },
            new { mediaType = OciChartContentValidator.ChartType, digest = "sha256:bad", size = Chart.Length },
            new { mediaType = OciChartContentValidator.ChartType, digest = Digest(Chart), size = Chart.Length + 1 },
            new { mediaType = OciChartContentValidator.ChartType, digest = Digest(Config), size = Chart.Length }
        })
        {
            var bytes = Manifest(layer);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                var manifest = await OciChartContentValidator.ReadManifestAsync(Stream(bytes), null, CancellationToken.None);
                await OciChartContentValidator.ReadContentAsync(manifest, Stream(Config), Stream(Chart), null, CancellationToken.None);
            });
        }
        var validBytes = Manifest(valid);
        await Assert.ThrowsAsync<InvalidDataException>(() => OciChartContentValidator.ReadManifestAsync(Stream(validBytes), Digest(Chart), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => OciChartContentValidator.ReadManifestAsync(Stream(new byte[1024 * 1024 + 1]), null, CancellationToken.None));
        var parsed = await OciChartContentValidator.ReadManifestAsync(Stream(validBytes), null, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => OciChartContentValidator.ReadContentAsync(parsed, Stream(Chart), Stream(Chart), null, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidConfig_IsRejectedEvenWhenItsDigestMatches()
    {
        var invalidConfig = "{\"name\":\"builtins\"}"u8.ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 2,
            mediaType = OciChartContentValidator.ManifestType,
            config = Descriptor(OciChartContentValidator.ConfigType, invalidConfig),
            layers = new[] { Descriptor(OciChartContentValidator.ChartType, Chart) }
        });
        var manifest = await OciChartContentValidator.ReadManifestAsync(Stream(bytes), null, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => OciChartContentValidator.ReadContentAsync(manifest, Stream(invalidConfig), Stream(Chart), null, CancellationToken.None));
    }
    [Fact]
    public async Task Cancellation_StopsReadsWithoutReturningContent()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OciChartContentValidator.ReadManifestAsync(Stream(Manifest()), null, cancellation.Token));
        var manifest = await OciChartContentValidator.ReadManifestAsync(Stream(Manifest(Descriptor(OciChartContentValidator.ChartType, Chart))), null, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OciChartContentValidator.ReadContentAsync(manifest, Stream(Config), Stream(Chart), null, cancellation.Token));
    }

    private static object Descriptor(string type, byte[] bytes) => new { mediaType = type, digest = Digest(bytes), size = bytes.Length };
    private static byte[] Manifest(params object[] layers) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 2,
        mediaType = OciChartContentValidator.ManifestType,
        config = Descriptor(OciChartContentValidator.ConfigType, Config),
        layers
    });
    private static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static MemoryStream Stream(byte[] bytes) => new(bytes, writable: false);
}
