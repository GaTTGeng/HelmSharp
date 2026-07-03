using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using HelmSharp.Action;

namespace HelmSharp.Tests;

public sealed class PackageMetadataValidationTests : IDisposable
{
    private readonly string _tempDir;

    public PackageMetadataValidationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "helmsharp-package-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task PackageAsync_InvalidMetadataReturnsFailureWithoutArchive()
    {
        var chartDir = await CreateChartAsync("invalid-version", """
            apiVersion: v2
            name: invalid-version
            version: nope
            """);
        var destination = Path.Combine(_tempDir, "packages");
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var result = await client.PackageAsync(chartDir, destination);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("validation: chart.metadata.version \"nope\" is invalid", result.StandardError);
        Assert.False(ContainsPackage(destination));
    }

    [Fact]
    public async Task PackageAsync_InvalidMetadataThrowsFromLowerLevelApi()
    {
        var chartDir = await CreateChartAsync("missing-name", """
            apiVersion: v2
            version: 1.2.3
            """);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => HelmChartPackager.PackageAsync(chartDir, _tempDir));

        Assert.Contains("validation: chart.metadata.name is required", ex.Message);
    }

    [Fact]
    public async Task PackageAsync_MissingChartYamlReturnsFailureWithoutArchive()
    {
        var chartDir = Path.Combine(_tempDir, "missing-chart-yaml");
        Directory.CreateDirectory(chartDir);
        var destination = Path.Combine(_tempDir, "missing-output");
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var result = await client.PackageAsync(chartDir, destination);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Error: Chart.yaml file is missing", result.StandardError);
        Assert.False(ContainsPackage(destination));
    }

    [Fact]
    public async Task PackageAsync_MalformedChartYamlReturnsActionableFailure()
    {
        var chartDir = await CreateChartAsync("malformed", """
            apiVersion: v2
            name: [oops
            version: 1.2.3
            """);
        var destination = Path.Combine(_tempDir, "malformed-output");
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var result = await client.PackageAsync(chartDir, destination);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("cannot load Chart.yaml", result.StandardError);
        Assert.False(ContainsPackage(destination));
    }

    [Fact]
    public async Task PackageAsync_InvalidChartNameReturnsFailureWithoutArchive()
    {
        var chartDir = await CreateChartAsync("invalid-name", """
            apiVersion: v2
            name: bad/name
            version: 1.2.3
            """);
        var destination = Path.Combine(_tempDir, "invalid-name-output");
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var result = await client.PackageAsync(chartDir, destination);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("validation: chart.metadata.name \"bad/name\" is invalid", result.StandardError);
        Assert.False(ContainsPackage(destination));
    }

    [Fact]
    public async Task PackageAsync_UnsupportedApiVersionReturnsFailureWithoutArchive()
    {
        var chartDir = await CreateChartAsync("unsupported-api", """
            apiVersion: v3
            name: unsupported-api
            version: 1.2.3
            """);
        var destination = Path.Combine(_tempDir, "unsupported-api-output");
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var result = await client.PackageAsync(chartDir, destination);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("validation: chart.metadata.apiVersion \"v3\" is unsupported", result.StandardError);
        Assert.False(ContainsPackage(destination));
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    public async Task PackageAsync_ApiVersionV1AndV2PackageSuccessfully(string apiVersion)
    {
        var chartDir = await CreateChartAsync($"valid-{apiVersion}", $"""
            apiVersion: {apiVersion}
            name: valid-{apiVersion}
            version: 1.2.3
            """);
        var destination = Path.Combine(_tempDir, $"{apiVersion}-output");
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var result = await client.PackageAsync(chartDir, destination);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(destination, $"valid-{apiVersion}-1.2.3.tgz")));
    }

    [Fact]
    public async Task PackageAsync_VersionOverrideRewritesArchiveOnly()
    {
        var sourceChartYaml = """
            apiVersion: v2
            name: override-version
            version: 1.2.3
            appVersion: "1.0"
            """;
        var chartDir = await CreateChartAsync("override-version", sourceChartYaml);
        var sourceChartPath = Path.Combine(chartDir, "Chart.yaml");
        var destination = Path.Combine(_tempDir, "override-version-output");
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var result = await client.PackageAsync(chartDir, destination, version: "2.0.0");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(sourceChartYaml, await File.ReadAllTextAsync(sourceChartPath));

        var packagePath = Path.Combine(destination, "override-version-2.0.0.tgz");
        Assert.True(File.Exists(packagePath));
        var packagedChartYaml = await ReadChartYamlFromPackageAsync(packagePath);
        Assert.Contains("version: 2.0.0", packagedChartYaml);
        Assert.DoesNotContain("version: 1.2.3", packagedChartYaml);
        Assert.Contains("appVersion: \"1.0\"", packagedChartYaml);
    }

    [Fact]
    public async Task PackageAsync_AppVersionOverrideRewritesPackagedMetadataOnly()
    {
        var sourceChartYaml = """
            apiVersion: v2
            name: override-app-version
            version: 1.2.3
            appVersion: "1.0"
            """;
        var chartDir = await CreateChartAsync("override-app-version", sourceChartYaml);
        var sourceChartPath = Path.Combine(chartDir, "Chart.yaml");
        var destination = Path.Combine(_tempDir, "override-app-version-output");
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var result = await client.PackageAsync(chartDir, destination, appVersion: "3.4");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(sourceChartYaml, await File.ReadAllTextAsync(sourceChartPath));

        var packagePath = Path.Combine(destination, "override-app-version-1.2.3.tgz");
        Assert.True(File.Exists(packagePath));
        var packagedChartYaml = await ReadChartYamlFromPackageAsync(packagePath);
        Assert.Contains("version: 1.2.3", packagedChartYaml);
        Assert.Contains("appVersion: \"3.4\"", packagedChartYaml);
        Assert.DoesNotContain("appVersion: \"1.0\"", packagedChartYaml);
    }

    [Fact]
    public async Task PackageAsync_InvalidVersionOverrideReturnsFailureWithoutArchive()
    {
        var sourceChartYaml = """
            apiVersion: v2
            name: invalid-override
            version: 1.2.3
            """;
        var chartDir = await CreateChartAsync("invalid-override", sourceChartYaml);
        var sourceChartPath = Path.Combine(chartDir, "Chart.yaml");
        var destination = Path.Combine(_tempDir, "invalid-override-output");
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var result = await client.PackageAsync(chartDir, destination, version: "not-semver");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Error: Invalid Semantic Version", result.StandardError);
        Assert.Equal(sourceChartYaml, await File.ReadAllTextAsync(sourceChartPath));
        Assert.False(ContainsPackage(destination));
    }

    [HelmCliTheory]
    [InlineData("missing-chart-yaml", null)]
    [InlineData("empty-chart-yaml", "")]
    [InlineData("missing-name", "apiVersion: v2\nversion: 1.2.3\n")]
    [InlineData("missing-version", "apiVersion: v2\nname: missing-version\n")]
    [InlineData("invalid-semver", "apiVersion: v2\nname: invalid-semver\nversion: nope\n")]
    [InlineData("invalid-type", "apiVersion: v2\nname: invalid-type\nversion: 1.2.3\ntype: wrong\n")]
    public async Task PackageAsync_ValidationFailuresMatchHelmPackage(
        string caseName,
        string? chartYaml)
    {
        var chartDir = await CreateChartAsync(caseName, chartYaml);
        var sharpDestination = Path.Combine(_tempDir, $"sharp-{caseName}");
        var helmDestination = Path.Combine(_tempDir, $"helm-{caseName}");
        Directory.CreateDirectory(helmDestination);
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var sharpResult = await client.PackageAsync(chartDir, sharpDestination);
        var helmResult = await HelmCliRunner.PackageAsync(
            chartDir,
            helmDestination,
            version: null,
            appVersion: null,
            CancellationToken.None);

        Assert.NotEqual(0, helmResult.ExitCode);
        Assert.Equal(helmResult.ExitCode, sharpResult.ExitCode);
        Assert.Equal(helmResult.Stderr.Trim(), sharpResult.StandardError.Trim());
        Assert.False(ContainsPackage(sharpDestination));
        Assert.False(ContainsPackage(helmDestination));
    }

    [HelmCliFact]
    public async Task PackageAsync_VersionAndAppVersionOverrideMatchesHelmPackagedChartYaml()
    {
        var sourceChartYaml = """
            apiVersion: v2
            name: appver-test
            version: 1.2.3
            appVersion: 1.16
            """;
        var sharpChartDir = await CreateChartAsync("sharp-appver-test", sourceChartYaml);
        var helmChartDir = await CreateChartAsync("helm-appver-test", sourceChartYaml);
        var sharpDestination = Path.Combine(_tempDir, "sharp-appver-output");
        var helmDestination = Path.Combine(_tempDir, "helm-appver-output");
        Directory.CreateDirectory(helmDestination);
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var sharpResult = await client.PackageAsync(
            sharpChartDir,
            sharpDestination,
            version: "2.0.0",
            appVersion: "3.4");
        var helmResult = await HelmCliRunner.PackageAsync(
            helmChartDir,
            helmDestination,
            version: "2.0.0",
            appVersion: "3.4",
            CancellationToken.None);

        Assert.Equal(0, sharpResult.ExitCode);
        Assert.Equal(0, helmResult.ExitCode);

        var sharpChartYaml = await ReadChartYamlFromPackageAsync(GetSinglePackagePath(sharpDestination));
        var helmChartYaml = await ReadChartYamlFromPackageAsync(GetSinglePackagePath(helmDestination));
        Assert.Equal(helmChartYaml, sharpChartYaml);
        Assert.Equal(sourceChartYaml, await File.ReadAllTextAsync(Path.Combine(sharpChartDir, "Chart.yaml")));
    }

    private async Task<string> CreateChartAsync(string directoryName, string? chartYaml)
    {
        var chartDir = Path.Combine(_tempDir, directoryName);
        Directory.CreateDirectory(chartDir);

        if (chartYaml is not null)
            await File.WriteAllTextAsync(Path.Combine(chartDir, "Chart.yaml"), chartYaml, Encoding.UTF8);

        return chartDir;
    }

    private static bool ContainsPackage(string destination)
        => Directory.Exists(destination) &&
           Directory.EnumerateFiles(destination, "*.tgz", SearchOption.AllDirectories).Any();

    private static string GetSinglePackagePath(string destination)
        => Directory.EnumerateFiles(destination, "*.tgz", SearchOption.AllDirectories).Single();

    private static async Task<string> ReadChartYamlFromPackageAsync(string packagePath)
    {
        await using var file = File.OpenRead(packagePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.DataStream is null ||
                !entry.Name.EndsWith("/Chart.yaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var memory = new MemoryStream();
            await entry.DataStream.CopyToAsync(memory);
            return Encoding.UTF8.GetString(memory.ToArray());
        }

        throw new InvalidOperationException($"Chart.yaml was not found in package {packagePath}.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private sealed class StaticHelmOptionsProvider : IHelmOptionsProvider
    {
        public ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new HelmExecutionOptions { DefaultNamespace = "default" });
    }
}
