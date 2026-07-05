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
    [InlineData("invalid-prerelease", "apiVersion: v2\nname: invalid-prerelease\nversion: 1.2.3-01\n")]
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
    public async Task PackageAsync_MissingApiVersionMatchesHelmPackage()
    {
        var sourceChartYaml = """
            name: missing-api
            version: 1.2.3
            """;
        var sharpChartDir = await CreateChartAsync("sharp-missing-api", sourceChartYaml);
        var helmChartDir = await CreateChartAsync("helm-missing-api", sourceChartYaml);
        var sharpDestination = Path.Combine(_tempDir, "sharp-missing-api-output");
        var helmDestination = Path.Combine(_tempDir, "helm-missing-api-output");
        Directory.CreateDirectory(helmDestination);
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var sharpResult = await client.PackageAsync(sharpChartDir, sharpDestination);
        var helmResult = await HelmCliRunner.PackageAsync(
            helmChartDir,
            helmDestination,
            version: null,
            appVersion: null,
            CancellationToken.None);

        Assert.Equal(0, helmResult.ExitCode);
        Assert.Equal(helmResult.ExitCode, sharpResult.ExitCode);
        Assert.True(File.Exists(Path.Combine(sharpDestination, "missing-api-1.2.3.tgz")));
        Assert.True(File.Exists(Path.Combine(helmDestination, "missing-api-1.2.3.tgz")));
    }

    [HelmCliTheory]
    [InlineData("1")]
    [InlineData("1.2")]
    public async Task PackageAsync_ShortVersionFormsMatchHelmPackage(string chartVersion)
    {
        var sourceChartYaml = $"""
            apiVersion: v2
            name: short-version
            version: {chartVersion}
            """;
        var sharpChartDir = await CreateChartAsync($"sharp-short-version-{chartVersion}", sourceChartYaml);
        var helmChartDir = await CreateChartAsync($"helm-short-version-{chartVersion}", sourceChartYaml);
        var sharpDestination = Path.Combine(_tempDir, $"sharp-short-version-{chartVersion}-output");
        var helmDestination = Path.Combine(_tempDir, $"helm-short-version-{chartVersion}-output");
        Directory.CreateDirectory(helmDestination);
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var sharpResult = await client.PackageAsync(sharpChartDir, sharpDestination);
        var helmResult = await HelmCliRunner.PackageAsync(
            helmChartDir,
            helmDestination,
            version: null,
            appVersion: null,
            CancellationToken.None);

        Assert.Equal(0, helmResult.ExitCode);
        Assert.Equal(helmResult.ExitCode, sharpResult.ExitCode);
        Assert.True(File.Exists(Path.Combine(sharpDestination, $"short-version-{chartVersion}.tgz")));
        Assert.True(File.Exists(Path.Combine(helmDestination, $"short-version-{chartVersion}.tgz")));
    }

    [Fact]
    public async Task PackageAsync_DestinationInsideChartDoesNotIncludePackageItself()
    {
        var chartDir = await CreateChartAsync("self-package", """
            apiVersion: v2
            name: self-package
            version: 1.2.3
            """);
        await File.WriteAllTextAsync(Path.Combine(chartDir, "values.yaml"), "replicaCount: 1\n");
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var result = await client.PackageAsync(chartDir, chartDir);

        Assert.Equal(0, result.ExitCode);
        var packagePath = Path.Combine(chartDir, "self-package-1.2.3.tgz");
        Assert.True(File.Exists(packagePath));

        var entryNames = await ReadPackageEntryNamesAsync(packagePath);
        Assert.Contains("self-package/Chart.yaml", entryNames);
        Assert.Contains("self-package/values.yaml", entryNames);
        Assert.DoesNotContain("self-package/self-package-1.2.3.tgz", entryNames);
    }

    [Fact]
    public async Task PackageAsync_HelmIgnoreFiltersIgnoredFilesAndPreservesBinaryPayload()
    {
        var chartDir = await CreateChartAsync("helmignore-filter", """
            apiVersion: v2
            name: helmignore-filter
            version: 1.2.3
            """);
        await WriteTextAsync(Path.Combine(chartDir, ".helmignore"), """
            # comments and blank lines are ignored

            .helmignore
            .git/
            .vscode/
            generated/
            *.tmp
            *.bak
            /root-only.txt
            config/*.draft
            """);
        await WriteTextAsync(Path.Combine(chartDir, "values.yaml"), "replicaCount: 1\n");
        await WriteTextAsync(Path.Combine(chartDir, "templates", "deployment.yaml"), "kind: Deployment\n");
        await WriteTextAsync(Path.Combine(chartDir, ".git", "config"), "ignored\n");
        await WriteTextAsync(Path.Combine(chartDir, ".vscode", "settings.json"), "{}\n");
        await WriteTextAsync(Path.Combine(chartDir, "generated", "nested", "manifest.yaml"), "ignored\n");
        await WriteTextAsync(Path.Combine(chartDir, "notes.tmp"), "ignored\n");
        await WriteTextAsync(Path.Combine(chartDir, "backup.bak"), "ignored\n");
        await WriteTextAsync(Path.Combine(chartDir, "root-only.txt"), "ignored\n");
        await WriteTextAsync(Path.Combine(chartDir, "nested", "root-only.txt"), "kept\n");
        await WriteTextAsync(Path.Combine(chartDir, "config", "local.draft"), "ignored\n");
        var payload = new byte[] { 0, 1, 2, 127, 128, 255 };
        await WriteBytesAsync(Path.Combine(chartDir, "files", "payload.bin"), payload);
        var destination = Path.Combine(_tempDir, "helmignore-filter-output");
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var result = await client.PackageAsync(chartDir.Replace('\\', '/'), destination.Replace('\\', '/'));

        Assert.Equal(0, result.ExitCode);
        var packagePath = Path.Combine(destination, "helmignore-filter-1.2.3.tgz");
        var entryNames = await ReadPackageEntryNamesAsync(packagePath);
        Assert.Contains("helmignore-filter/Chart.yaml", entryNames);
        Assert.Contains("helmignore-filter/values.yaml", entryNames);
        Assert.Contains("helmignore-filter/templates/deployment.yaml", entryNames);
        Assert.Contains("helmignore-filter/nested/root-only.txt", entryNames);
        Assert.Contains("helmignore-filter/files/payload.bin", entryNames);
        Assert.DoesNotContain("helmignore-filter/.helmignore", entryNames);
        Assert.DoesNotContain("helmignore-filter/.git/config", entryNames);
        Assert.DoesNotContain("helmignore-filter/.vscode/settings.json", entryNames);
        Assert.DoesNotContain("helmignore-filter/generated/nested/manifest.yaml", entryNames);
        Assert.DoesNotContain("helmignore-filter/notes.tmp", entryNames);
        Assert.DoesNotContain("helmignore-filter/backup.bak", entryNames);
        Assert.DoesNotContain("helmignore-filter/root-only.txt", entryNames);
        Assert.DoesNotContain("helmignore-filter/config/local.draft", entryNames);
        Assert.Equal(payload, await ReadPackageEntryBytesAsync(packagePath, "helmignore-filter/files/payload.bin"));
    }

    [Fact]
    public async Task PackageAsync_HelmIgnoreNegationRestoresLaterMatches()
    {
        var chartDir = await CreateChartAsync("helmignore-negation", """
            apiVersion: v2
            name: helmignore-negation
            version: 1.2.3
            """);
        await WriteTextAsync(Path.Combine(chartDir, ".helmignore"), """
            *.txt
            !important.txt
            nested/*.txt
            !nested/keep.txt
            generated/
            !generated/keep.yaml
            """);
        await WriteTextAsync(Path.Combine(chartDir, "values.yaml"), "replicaCount: 1\n");
        await WriteTextAsync(Path.Combine(chartDir, "ordinary.txt"), "ignored\n");
        await WriteTextAsync(Path.Combine(chartDir, "important.txt"), "kept\n");
        await WriteTextAsync(Path.Combine(chartDir, "nested", "drop.txt"), "ignored\n");
        await WriteTextAsync(Path.Combine(chartDir, "nested", "keep.txt"), "kept\n");
        await WriteTextAsync(Path.Combine(chartDir, "generated", "drop.yaml"), "ignored\n");
        await WriteTextAsync(Path.Combine(chartDir, "generated", "keep.yaml"), "kept\n");
        var destination = Path.Combine(_tempDir, "helmignore-negation-output");
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var result = await client.PackageAsync(chartDir, destination);

        Assert.Equal(0, result.ExitCode);
        var packagePath = Path.Combine(destination, "helmignore-negation-1.2.3.tgz");
        var entryNames = await ReadPackageEntryNamesAsync(packagePath);
        Assert.Contains("helmignore-negation/important.txt", entryNames);
        Assert.Contains("helmignore-negation/nested/keep.txt", entryNames);
        Assert.Contains("helmignore-negation/generated/keep.yaml", entryNames);
        Assert.DoesNotContain("helmignore-negation/ordinary.txt", entryNames);
        Assert.DoesNotContain("helmignore-negation/nested/drop.txt", entryNames);
        Assert.DoesNotContain("helmignore-negation/generated/drop.yaml", entryNames);
    }

    [HelmCliFact]
    public async Task PackageAsync_HelmIgnoreEntriesMatchHelmPackage()
    {
        var sharpChartDir = await CreateHelmIgnoreParityChartAsync("sharp-helmignore-parity");
        var helmChartDir = await CreateHelmIgnoreParityChartAsync("helm-helmignore-parity");
        var sharpDestination = Path.Combine(_tempDir, "sharp-helmignore-parity-output");
        var helmDestination = Path.Combine(_tempDir, "helm-helmignore-parity-output");
        Directory.CreateDirectory(helmDestination);
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var sharpResult = await client.PackageAsync(sharpChartDir, sharpDestination);
        var helmResult = await HelmCliRunner.PackageAsync(
            helmChartDir,
            helmDestination,
            version: null,
            appVersion: null,
            CancellationToken.None);

        Assert.Equal(0, sharpResult.ExitCode);
        Assert.Equal(0, helmResult.ExitCode);
        var sharpEntries = (await ReadPackageEntryNamesAsync(GetSinglePackagePath(sharpDestination)))
            .Order(StringComparer.Ordinal)
            .ToList();
        var helmEntries = (await ReadPackageEntryNamesAsync(GetSinglePackagePath(helmDestination)))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(helmEntries, sharpEntries);
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

    [Fact]
    public async Task PackageAsync_UsesChartNameRootAndPreservesRegularFiles()
    {
        var chartDir = await CreateArchiveLayoutChartAsync("source-folder");
        var destination = Path.Combine(_tempDir, "archive-layout-output");
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var result = await client.PackageAsync(chartDir, destination);

        Assert.Equal(0, result.ExitCode);
        var packagePath = Path.Combine(destination, "archive-layout-1.2.3.tgz");
        Assert.True(File.Exists(packagePath));
        var entryNames = await ReadPackageEntryNamesAsync(packagePath);
        Assert.All(entryNames, entryName => Assert.StartsWith("archive-layout/", entryName));
        Assert.DoesNotContain(entryNames, entryName => entryName.StartsWith("source-folder/", StringComparison.Ordinal));
        Assert.DoesNotContain("archive-layout/empty-dir/", entryNames);
        Assert.Contains("archive-layout/crds/widgets.yaml", entryNames);
        Assert.Contains("archive-layout/charts/child/Chart.yaml", entryNames);
        Assert.Equal([0, 1, 2, 127, 128, 255], await ReadPackageEntryBytesAsync(packagePath, "archive-layout/files/payload.bin"));
    }

    [HelmCliFact]
    public async Task PackageAsync_ArchiveEntriesMatchHelmPackageForLayoutAndMetadata()
    {
        var sharpChartDir = await CreateArchiveLayoutChartAsync("sharp-source-folder");
        var helmChartDir = await CreateArchiveLayoutChartAsync("helm-source-folder");
        var sharpDestination = Path.Combine(_tempDir, "sharp-archive-layout-output");
        var helmDestination = Path.Combine(_tempDir, "helm-archive-layout-output");
        Directory.CreateDirectory(helmDestination);
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var sharpResult = await client.PackageAsync(sharpChartDir, sharpDestination);
        var helmResult = await HelmCliRunner.PackageAsync(
            helmChartDir,
            helmDestination,
            version: null,
            appVersion: null,
            CancellationToken.None);

        Assert.Equal(0, sharpResult.ExitCode);
        Assert.Equal(0, helmResult.ExitCode);
        var sharpEntries = await ReadPackageEntriesAsync(GetSinglePackagePath(sharpDestination));
        var helmEntries = await ReadPackageEntriesAsync(GetSinglePackagePath(helmDestination));
        Assert.Equal(helmEntries, sharpEntries);
    }

    private async Task<string> CreateChartAsync(string directoryName, string? chartYaml)
    {
        var chartDir = Path.Combine(_tempDir, directoryName);
        Directory.CreateDirectory(chartDir);

        if (chartYaml is not null)
            await File.WriteAllTextAsync(Path.Combine(chartDir, "Chart.yaml"), chartYaml, Encoding.UTF8);

        return chartDir;
    }

    private async Task<string> CreateArchiveLayoutChartAsync(string directoryName)
    {
        var chartDir = await CreateChartAsync(directoryName, """
            apiVersion: v2
            name: archive-layout
            version: 1.2.3
            """);
        await WriteTextAsync(Path.Combine(chartDir, "values.yaml"), "replicaCount: 1\n");
        await WriteTextAsync(Path.Combine(chartDir, "templates", "deployment.yaml"), "kind: Deployment\n");
        await WriteTextAsync(Path.Combine(chartDir, "crds", "widgets.yaml"), """
            apiVersion: apiextensions.k8s.io/v1
            kind: CustomResourceDefinition
            metadata:
              name: widgets.example.com
            """);
        await WriteTextAsync(Path.Combine(chartDir, "charts", "child", "Chart.yaml"), """
            apiVersion: v2
            name: child
            version: 0.1.0
            """);
        await WriteTextAsync(Path.Combine(chartDir, "charts", "child", "values.yaml"), "enabled: true\n");
        await WriteTextAsync(Path.Combine(chartDir, "README.md"), "# Archive layout\n");
        await WriteBytesAsync(Path.Combine(chartDir, "files", "payload.bin"), [0, 1, 2, 127, 128, 255]);
        Directory.CreateDirectory(Path.Combine(chartDir, "empty-dir"));
        return chartDir;
    }

    private async Task<string> CreateHelmIgnoreParityChartAsync(string directoryName)
    {
        var chartDir = await CreateChartAsync(directoryName, """
            apiVersion: v2
            name: helmignore-parity
            version: 1.2.3
            """);
        await WriteTextAsync(Path.Combine(chartDir, ".helmignore"), """
            # Keep all otherwise unmatched files, then apply package exclusions.
            !*

            .git/
            .vscode/
            generated/
            *.tmp
            *.bak
            /root-only.txt
            config/*.draft
            [ab].txt
            """);
        await WriteTextAsync(Path.Combine(chartDir, "values.yaml"), "replicaCount: 1\n");
        await WriteTextAsync(Path.Combine(chartDir, "templates", "deployment.yaml"), "kind: Deployment\n");
        await WriteTextAsync(Path.Combine(chartDir, ".git", "config"), "ignored\n");
        await WriteTextAsync(Path.Combine(chartDir, ".vscode", "settings.json"), "{}\n");
        await WriteTextAsync(Path.Combine(chartDir, "generated", "nested", "manifest.yaml"), "ignored\n");
        await WriteTextAsync(Path.Combine(chartDir, "notes.tmp"), "ignored\n");
        await WriteTextAsync(Path.Combine(chartDir, "backup.bak"), "ignored\n");
        await WriteTextAsync(Path.Combine(chartDir, "root-only.txt"), "ignored\n");
        await WriteTextAsync(Path.Combine(chartDir, "nested", "root-only.txt"), "kept\n");
        await WriteTextAsync(Path.Combine(chartDir, "config", "local.draft"), "ignored\n");
        await WriteTextAsync(Path.Combine(chartDir, "a.txt"), "ignored\n");
        await WriteTextAsync(Path.Combine(chartDir, "c.txt"), "kept\n");
        await WriteBytesAsync(Path.Combine(chartDir, "files", "payload.bin"), [0, 1, 2, 127, 128, 255]);
        return chartDir;
    }

    private static async Task WriteTextAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static async Task WriteBytesAsync(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content);
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

    private static async Task<List<string>> ReadPackageEntryNamesAsync(string packagePath)
    {
        var entries = new List<string>();
        await using var file = File.OpenRead(packagePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
            entries.Add(entry.Name);

        return entries;
    }

    private static async Task<List<PackageEntrySnapshot>> ReadPackageEntriesAsync(string packagePath)
    {
        var entries = new List<PackageEntrySnapshot>();
        await using var file = File.OpenRead(packagePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            var size = 0L;
            if (entry.DataStream is not null)
            {
                using var memory = new MemoryStream();
                await entry.DataStream.CopyToAsync(memory);
                size = memory.Length;
            }

            entries.Add(new PackageEntrySnapshot(entry.Name, entry.EntryType, entry.Mode, size));
        }

        return entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList();
    }

    private static async Task<byte[]> ReadPackageEntryBytesAsync(string packagePath, string entryName)
    {
        await using var file = File.OpenRead(packagePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.DataStream is null || !string.Equals(entry.Name, entryName, StringComparison.Ordinal))
                continue;

            using var memory = new MemoryStream();
            await entry.DataStream.CopyToAsync(memory);
            return memory.ToArray();
        }

        throw new InvalidOperationException($"Entry {entryName} was not found in package {packagePath}.");
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

    private sealed record PackageEntrySnapshot(
        string Name,
        TarEntryType EntryType,
        UnixFileMode Mode,
        long Size);
}
