using System.Net;
using System.Net.Sockets;
using System.Text;
using HelmSharp.Registry;
using HelmSharp.Repo;

namespace HelmSharp.Tests;

/// <summary>OCI chart reference parsing and pre-request validation.</summary>
public sealed class OciChartReferenceTests
{
    [Theory]
    [InlineData("oci://REGISTRY.example:5000/team/nested/chart:1.2.3_build", "registry.example:5000", "team/nested/chart", "chart", "1.2.3_build", null)]
    [InlineData("oci://localhost/chart", "localhost", "chart", "chart", null, null)]
    [InlineData("oci://[::1]:5000/team/chart:latest", "[::1]:5000", "team/chart", "chart", "latest", null)]
    [InlineData("oci://example.com/chart@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "example.com", "chart", "chart", null, "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void Parse_ValidReferencesRoundTrip(string input, string registry, string repository, string name, string? tag, string? digest)
    {
        var parsed = OciChartReference.Parse(input);
        Assert.Equal(registry, parsed.Registry);
        Assert.Equal(repository, parsed.Repository);
        Assert.Equal(name, parsed.Name);
        Assert.Equal(tag, parsed.Tag);
        Assert.Equal(digest, parsed.Digest);
        Assert.Equal(parsed.ToString(), OciChartReference.Parse(parsed.ToString()).ToString());
    }

    [Theory]
    [InlineData("https://example.com/chart")]
    [InlineData("oci://example.com")]
    [InlineData("oci://example.com//chart")]
    [InlineData("oci://example.com/team/Chart")]
    [InlineData("oci://example.com/chart:bad!")]
    [InlineData("oci://example.com/chart:1.2.3+build")]
    [InlineData("oci://example.com/chart:")]
    [InlineData("oci://example.com/chart@sha256:abc")]
    [InlineData("oci://example.com/chart:tag@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("oci://user:password@example.com/chart")]
    [InlineData("oci://example.com:99999/chart")]
    [InlineData("oci://example.com/chart?secret=value")]
    [InlineData("oci://example.com/chart#fragment")]
    [InlineData("oci://example.com/%63hart")]
    public void Parse_InvalidReferencesAreRejectedWithoutEchoingInput(string input)
    {
        var error = Assert.Throws<ArgumentException>(() => OciChartReference.Parse(input));
        Assert.DoesNotContain(input, error.Message);
    }

    [Fact]
    public void NormalizeVersion_TranslatesBuildMetadata()
        => Assert.Equal("1.2.3_build.7", OciChartReference.NormalizeVersion("1.2.3+build.7"));

    [Theory]
    [InlineData("oci://example.com/Chart", null)]
    [InlineData("oci://example.com/chart", "bad!")]
    [InlineData("oci://example.com/chart:1.0.0", "1.0.1")]
    [InlineData("oci://example.com/chart:1.0.0_build", "1.0.0+build")]
    public async Task PullChartAsync_RejectsInvalidOrConflictingSelectorsBeforeRequest(string reference, string? version)
    {
        using var handler = new CountingHandler();
        using var repository = new HelmChartRepository(new HelmRepositoryOptions(), handler);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.PullChartAsync(reference, version));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task PullChartAsync_DigestIsRejectedBeforeRequest()
    {
        using var handler = new CountingHandler();
        using var repository = new HelmChartRepository(new HelmRepositoryOptions(), handler);
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.PullChartAsync(
            "oci://example.com/chart@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task PullChartAsync_MatchingTagAndVersionReachRegistry()
    {
        using var handler = new CountingHandler();
        using var repository = new HelmChartRepository(new HelmRepositoryOptions(), handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => repository.PullChartAsync("oci://example.com/chart:1.0.0", "1.0.0"));
        Assert.Equal(1, handler.RequestCount);
    }

    [HelmCliFact]
    public async Task Parse_RejectsTheSameMalformedNamesAndTagsAsHelm()
    {
        // Helm v4.2.2 validates these before attempting a local registry connection.
        var references = new[]
        {
            "oci://127.0.0.1:1/team/Chart:1.0.0",
            "oci://127.0.0.1:1/team/chart:bad!",
            "oci://127.0.0.1:1/team/chart:1.2.3+build"
        };
        foreach (var reference in references)
        {
            var helm = await HelmCliRunner.PullAsync(reference, Path.GetTempPath(), null, null, CancellationToken.None);
            Assert.NotEqual(0, helm.ExitCode);
            Assert.Contains("invalid reference", helm.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Throws<ArgumentException>(() => OciChartReference.Parse(reference));
        }
    }

    [HelmCliFact]
    public async Task Parse_ValidNameAndVersionBuildMetadataMatchHelmRequest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var reference = $"oci://127.0.0.1:{port}/team/chart";
            var parsed = OciChartReference.Parse(reference);
            var helmTask = HelmCliRunner.PullOciPlainHttpAsync(reference, "1.2.3+build.7", CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(timeout.Token);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
            var response = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
            await stream.WriteAsync(response, timeout.Token);
            var helm = await helmTask;
            Assert.NotEqual(0, helm.ExitCode);
            Assert.Contains($"/v2/{parsed.Repository}/manifests/{OciChartReference.NormalizeVersion("1.2.3+build.7")}", requestLine);
        }
        finally
        {
            listener.Stop();
        }
    }

    [HelmCliFact]
    public async Task Pull_TagAndVersionMustMatchLiterallyAsInHelm()
    {
        var matching = await HelmCliRunner.PullOciPlainHttpAsync("oci://127.0.0.1:1/team/chart:1.2.3", "1.2.3", CancellationToken.None);
        var different = await HelmCliRunner.PullOciPlainHttpAsync("oci://127.0.0.1:1/team/chart:1.2.3", "1.2.4", CancellationToken.None);
        var buildMetadata = await HelmCliRunner.PullOciPlainHttpAsync("oci://127.0.0.1:1/team/chart:1.2.3_build", "1.2.3+build", CancellationToken.None);
        Assert.DoesNotContain("mismatch", matching.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manifests/1.2.3", matching.Stderr, StringComparison.Ordinal);
        Assert.Contains("mismatch", different.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mismatch", buildMetadata.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
