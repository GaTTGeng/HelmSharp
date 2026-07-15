using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using HelmSharp.Action;
using HelmSharp.Chart;
using HelmSharp.Repo;

namespace HelmSharp.Tests;

public sealed class PackagingRepositoryGoldenTests : IDisposable
{
    private readonly string _tempDir;

    public PackagingRepositoryGoldenTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "helmsharp-packaging-repository-golden-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [HelmCliFact]
    public async Task PackageAsync_GoldenChartArchiveMatchesHelmPackage()
    {
        var sharpChartDir = await CreatePackageGoldenChartAsync("sharp-package-source", "1.2.3");
        var helmChartDir = await CreatePackageGoldenChartAsync("helm-package-source", "1.2.3");
        var sharpDestination = Path.Combine(_tempDir, "sharp-package-output");
        var helmDestination = Path.Combine(_tempDir, "helm-package-output");
        Directory.CreateDirectory(helmDestination);
        var client = CreateClient();

        var sharpResult = await client.PackageAsync(sharpChartDir, sharpDestination);
        var helmResult = await HelmCliRunner.PackageAsync(
            helmChartDir,
            helmDestination,
            version: null,
            appVersion: null,
            CancellationToken.None);

        AssertOperationSucceeded("helm package", helmResult);
        AssertOperationSucceeded("HelmSharp PackageAsync", sharpResult);
        Assert.Equal(
            await ReadArchiveSnapshotAsync(GetSinglePackagePath(helmDestination)),
            await ReadArchiveSnapshotAsync(GetSinglePackagePath(sharpDestination)));
    }

    [HelmCliFact]
    public async Task RepoIndexAsync_LocalPackageIndexMatchesHelmRepoIndexFields()
    {
        var sharpRepoDir = Path.Combine(_tempDir, "sharp-repo-index");
        var helmRepoDir = Path.Combine(_tempDir, "helm-repo-index");
        Directory.CreateDirectory(sharpRepoDir);
        Directory.CreateDirectory(helmRepoDir);
        await PackageRepoChartVersionAsync("1.0.0", sharpRepoDir);
        await PackageRepoChartVersionAsync("1.1.0", sharpRepoDir);
        CopyPackages(sharpRepoDir, helmRepoDir);

        var sharpIndexPath = await HelmRepoIndexer.GenerateIndexAsync(
            sharpRepoDir,
            "http://127.0.0.1:19080/charts",
            CancellationToken.None);
        var helmResult = await HelmCliRunner.RepoIndexAsync(
            helmRepoDir,
            "http://127.0.0.1:19080/charts",
            CancellationToken.None);

        AssertOperationSucceeded("helm repo index", helmResult);
        var helmIndexPath = Path.Combine(helmRepoDir, "index.yaml");
        var sharpIndexYaml = File.ReadAllText(sharpIndexPath, Encoding.UTF8);
        var sharpIndex = ReadIndexSnapshot(sharpIndexPath, "repo-golden");
        var helmIndex = ReadIndexSnapshot(helmIndexPath, "repo-golden");

        Assert.Equal(helmIndex.ApiVersion, sharpIndex.ApiVersion);
        Assert.DoesNotContain("home: null", sharpIndexYaml);
        Assert.DoesNotContain("sources: null", sharpIndexYaml);
        Assert.DoesNotContain("keywords: null", sharpIndexYaml);
        Assert.DoesNotContain("maintainers: null", sharpIndexYaml);
        Assert.DoesNotContain("deprecated: null", sharpIndexYaml);
        Assert.Equal(["1.1.0", "1.0.0"], sharpIndex.Versions.Select(version => version.Version));
        Assert.Equal(helmIndex.Versions.Select(version => version.Version), sharpIndex.Versions.Select(version => version.Version));
        Assert.Equal(helmIndex.Versions.Select(version => version.Url), sharpIndex.Versions.Select(version => version.Url));
        Assert.Equal(helmIndex.Versions.Select(version => version.Digest), sharpIndex.Versions.Select(version => version.Digest));
        Assert.All(sharpIndex.Versions, version =>
        {
            Assert.StartsWith("http://127.0.0.1:19080/charts/repo-golden-", version.Url, StringComparison.Ordinal);
            Assert.True(DateTimeOffset.TryParse(version.Created, out _), $"created was not a timestamp: {version.Created}");
        });
        Assert.True(DateTimeOffset.TryParse(sharpIndex.Generated, out _), $"generated was not a timestamp: {sharpIndex.Generated}");
    }

    [HelmCliFact]
    public async Task RepoIndexAsync_FullMetadataEntryStructurallyMatchesHelmRepoIndex()
    {
        var sharpRepoDir = Path.Combine(_tempDir, "sharp-full-metadata-repo-index");
        var helmRepoDir = Path.Combine(_tempDir, "helm-full-metadata-repo-index");
        Directory.CreateDirectory(sharpRepoDir);
        Directory.CreateDirectory(helmRepoDir);
        await PackageFullMetadataRepoChartAsync(sharpRepoDir);
        CopyPackages(sharpRepoDir, helmRepoDir);

        var sharpIndexPath = await HelmRepoIndexer.GenerateIndexAsync(
            sharpRepoDir,
            "https://repo.example.test/base",
            CancellationToken.None);
        var helmResult = await HelmCliRunner.RepoIndexAsync(
            helmRepoDir,
            "https://repo.example.test/base",
            CancellationToken.None);

        AssertOperationSucceeded("helm repo index", helmResult);
        var helmIndexPath = Path.Combine(helmRepoDir, "index.yaml");
        var sharpIndex = ReadNormalizedIndexEntry(sharpIndexPath, "repo-full-metadata");
        var helmIndex = ReadNormalizedIndexEntry(helmIndexPath, "repo-full-metadata");

        Assert.Equal(HelmYaml.Serialize(helmIndex), HelmYaml.Serialize(sharpIndex));
        Assert.Equal(
            await ComputeSha256Async(GetSinglePackagePath(sharpRepoDir)),
            HelmYaml.GetString(sharpIndex, "digest"));
    }

    [HelmCliFact]
    public async Task RepoIndexAsync_MergePreservesAbsentVersionsAndUrls()
    {
        var sharpRepoDir = Path.Combine(_tempDir, "sharp-merge-repo-index");
        var helmRepoDir = Path.Combine(_tempDir, "helm-merge-repo-index");
        Directory.CreateDirectory(sharpRepoDir);
        Directory.CreateDirectory(helmRepoDir);
        await PackageRepoChartVersionAsync("1.0.0", sharpRepoDir);
        CopyPackages(sharpRepoDir, helmRepoDir);

        var sharpExistingIndex = Path.Combine(_tempDir, "sharp-existing-index.yaml");
        var helmExistingIndex = Path.Combine(_tempDir, "helm-existing-index.yaml");
        File.Copy(
            await HelmRepoIndexer.GenerateIndexAsync(sharpRepoDir, "https://old.example.test/charts", CancellationToken.None),
            sharpExistingIndex);
        var helmInitial = await HelmCliRunner.RepoIndexAsync(
            helmRepoDir,
            "https://old.example.test/charts",
            CancellationToken.None);
        AssertOperationSucceeded("helm repo index initial merge fixture", helmInitial);
        File.Copy(Path.Combine(helmRepoDir, "index.yaml"), helmExistingIndex);

        File.Delete(Path.Combine(sharpRepoDir, "repo-golden-1.0.0.tgz"));
        File.Delete(Path.Combine(helmRepoDir, "repo-golden-1.0.0.tgz"));
        await PackageRepoChartVersionAsync("1.1.0", sharpRepoDir);
        CopyPackages(sharpRepoDir, helmRepoDir);

        var sharpIndexPath = await HelmRepoIndexer.GenerateIndexAsync(
            sharpRepoDir,
            "https://new.example.test/charts/",
            CancellationToken.None,
            mergeIndexPath: sharpExistingIndex);
        var helmMerged = await HelmCliRunner.RepoIndexAsync(
            helmRepoDir,
            "https://new.example.test/charts/",
            CancellationToken.None,
            mergeIndexPath: helmExistingIndex);
        AssertOperationSucceeded("helm repo index merge", helmMerged);

        var sharpIndex = ReadIndexSnapshot(sharpIndexPath, "repo-golden");
        var helmIndex = ReadIndexSnapshot(Path.Combine(helmRepoDir, "index.yaml"), "repo-golden");
        Assert.Equal(helmIndex.Versions.Select(version => version.Version), sharpIndex.Versions.Select(version => version.Version));
        Assert.Equal(helmIndex.Versions.Select(version => version.Url), sharpIndex.Versions.Select(version => version.Url));
        Assert.Equal(helmIndex.Versions.Select(version => version.Digest), sharpIndex.Versions.Select(version => version.Digest));
        Assert.Equal(
            ["https://new.example.test/charts/repo-golden-1.1.0.tgz", "https://old.example.test/charts/repo-golden-1.0.0.tgz"],
            sharpIndex.Versions.Select(version => version.Url));
    }

    [HelmCliFact]
    public async Task RepoIndexAsync_MergeReplacesMatchingLocalVersion()
    {
        var sharpRepoDir = Path.Combine(_tempDir, "sharp-merge-replacement-repo-index");
        var helmRepoDir = Path.Combine(_tempDir, "helm-merge-replacement-repo-index");
        Directory.CreateDirectory(sharpRepoDir);
        Directory.CreateDirectory(helmRepoDir);
        await PackageRepoChartVersionAsync("1.0.0", sharpRepoDir, "Original chart");
        CopyPackages(sharpRepoDir, helmRepoDir);

        var sharpExistingIndex = Path.Combine(_tempDir, "sharp-replacement-existing-index.yaml");
        var helmExistingIndex = Path.Combine(_tempDir, "helm-replacement-existing-index.yaml");
        var sharpInitialPath = await HelmRepoIndexer.GenerateIndexAsync(
            sharpRepoDir,
            "https://old.example.test/charts",
            CancellationToken.None);
        File.Copy(sharpInitialPath, sharpExistingIndex);
        var originalDigest = ReadIndexSnapshot(sharpInitialPath, "repo-golden").Versions.Single().Digest;
        var helmInitial = await HelmCliRunner.RepoIndexAsync(
            helmRepoDir,
            "https://old.example.test/charts",
            CancellationToken.None);
        AssertOperationSucceeded("helm repo index initial replacement fixture", helmInitial);
        File.Copy(Path.Combine(helmRepoDir, "index.yaml"), helmExistingIndex);

        File.Delete(Path.Combine(sharpRepoDir, "repo-golden-1.0.0.tgz"));
        File.Delete(Path.Combine(helmRepoDir, "repo-golden-1.0.0.tgz"));
        await PackageRepoChartVersionAsync("1.0.0", sharpRepoDir, "Replacement chart");
        CopyPackages(sharpRepoDir, helmRepoDir);

        var sharpIndexPath = await HelmRepoIndexer.GenerateIndexAsync(
            sharpRepoDir,
            "https://new.example.test/charts/",
            CancellationToken.None,
            mergeIndexPath: sharpExistingIndex);
        var helmMerged = await HelmCliRunner.RepoIndexAsync(
            helmRepoDir,
            "https://new.example.test/charts/",
            CancellationToken.None,
            mergeIndexPath: helmExistingIndex);
        AssertOperationSucceeded("helm repo index replacement merge", helmMerged);

        var sharpIndex = ReadIndexSnapshot(sharpIndexPath, "repo-golden");
        var helmIndex = ReadIndexSnapshot(Path.Combine(helmRepoDir, "index.yaml"), "repo-golden");
        Assert.Single(sharpIndex.Versions);
        Assert.Equal(helmIndex.Versions.Select(version => version.Version), sharpIndex.Versions.Select(version => version.Version));
        Assert.Equal(helmIndex.Versions.Select(version => version.Url), sharpIndex.Versions.Select(version => version.Url));
        Assert.Equal(helmIndex.Versions.Select(version => version.Digest), sharpIndex.Versions.Select(version => version.Digest));
        Assert.NotEqual(originalDigest, sharpIndex.Versions.Single().Digest);
        Assert.Equal("https://new.example.test/charts/repo-golden-1.0.0.tgz", sharpIndex.Versions.Single().Url);
    }

    [HelmCliFact]
    public async Task RepoIndexAsync_MergeReplacesSemanticallyEquivalentLocalVersions()
    {
        var versionPairs = new[]
        {
            (Existing: "1.2.0", Local: "1.2"),
            (Existing: "1.2.0+existing", Local: "1.2.0+local")
        };

        for (var index = 0; index < versionPairs.Length; index++)
        {
            var (existingVersion, localVersion) = versionPairs[index];
            var sharpRepoDir = Path.Combine(_tempDir, $"sharp-semver-merge-repo-index-{index}");
            var helmRepoDir = Path.Combine(_tempDir, $"helm-semver-merge-repo-index-{index}");
            Directory.CreateDirectory(sharpRepoDir);
            Directory.CreateDirectory(helmRepoDir);
            await PackageRepoChartVersionAsync(existingVersion, sharpRepoDir);
            CopyPackages(sharpRepoDir, helmRepoDir);

            var sharpExistingIndex = Path.Combine(_tempDir, $"sharp-semver-existing-index-{index}.yaml");
            var helmExistingIndex = Path.Combine(_tempDir, $"helm-semver-existing-index-{index}.yaml");
            File.Copy(
                await HelmRepoIndexer.GenerateIndexAsync(sharpRepoDir, ct: CancellationToken.None),
                sharpExistingIndex);
            var helmInitial = await HelmCliRunner.RepoIndexAsync(helmRepoDir, url: null, CancellationToken.None);
            AssertOperationSucceeded("helm repo index initial semantic merge fixture", helmInitial);
            File.Copy(Path.Combine(helmRepoDir, "index.yaml"), helmExistingIndex);

            File.Delete(Path.Combine(sharpRepoDir, $"repo-golden-{existingVersion}.tgz"));
            File.Delete(Path.Combine(helmRepoDir, $"repo-golden-{existingVersion}.tgz"));
            await PackageRepoChartVersionAsync(localVersion, sharpRepoDir);
            CopyPackages(sharpRepoDir, helmRepoDir);

            var sharpIndexPath = await HelmRepoIndexer.GenerateIndexAsync(
                sharpRepoDir,
                url: null,
                ct: CancellationToken.None,
                mergeIndexPath: sharpExistingIndex);
            var helmMerged = await HelmCliRunner.RepoIndexAsync(
                helmRepoDir,
                url: null,
                cancellationToken: CancellationToken.None,
                mergeIndexPath: helmExistingIndex);
            AssertOperationSucceeded("helm repo index semantic merge", helmMerged);

            var sharpIndex = ReadIndexSnapshot(sharpIndexPath, "repo-golden");
            var helmIndex = ReadIndexSnapshot(Path.Combine(helmRepoDir, "index.yaml"), "repo-golden");
            Assert.Single(sharpIndex.Versions);
            Assert.Equal(localVersion, sharpIndex.Versions.Single().Version);
            Assert.Equal(helmIndex.Versions.Select(version => version.Version), sharpIndex.Versions.Select(version => version.Version));
            Assert.Equal(helmIndex.Versions.Select(version => version.Digest), sharpIndex.Versions.Select(version => version.Digest));
        }
    }

    [Fact]
    public async Task RepoIndexAsync_MergePreservesVersionsThatOnlyMatchAsConstraints()
    {
        var sharpRepoDir = Path.Combine(_tempDir, "sharp-constraint-version-merge-repo-index");
        Directory.CreateDirectory(sharpRepoDir);
        await PackageRepoChartVersionAsync("1.2", sharpRepoDir);

        var sharpExistingIndex = Path.Combine(_tempDir, "sharp-constraint-version-existing-index.yaml");
        File.Copy(await HelmRepoIndexer.GenerateIndexAsync(sharpRepoDir, ct: CancellationToken.None), sharpExistingIndex);

        File.Delete(Path.Combine(sharpRepoDir, "repo-golden-1.2.tgz"));
        await PackageRepoChartVersionAsync("1.2.5", sharpRepoDir);

        var sharpIndexPath = await HelmRepoIndexer.GenerateIndexAsync(
            sharpRepoDir,
            url: null,
            ct: CancellationToken.None,
            mergeIndexPath: sharpExistingIndex);

        var sharpIndex = ReadIndexSnapshot(sharpIndexPath, "repo-golden");
        Assert.Equal(["1.2.5", "1.2"], sharpIndex.Versions.Select(version => version.Version));
    }

    [HelmCliFact]
    public async Task RepoIndexAsync_MergeUsesChartMetadataNameInsteadOfEntryKey()
    {
        var sharpRepoDir = Path.Combine(_tempDir, "sharp-metadata-name-merge-repo-index");
        var helmRepoDir = Path.Combine(_tempDir, "helm-metadata-name-merge-repo-index");
        Directory.CreateDirectory(sharpRepoDir);
        Directory.CreateDirectory(helmRepoDir);
        await PackageRepoChartVersionAsync("1.0.0", sharpRepoDir);
        CopyPackages(sharpRepoDir, helmRepoDir);

        const string staleEntryKeyIndex = """
            apiVersion: v1
            entries:
              stale-entry-key:
                - apiVersion: v2
                  name: repo-golden
                  version: 0.1.0
                  created: 2024-01-01T00:00:00Z
                  urls:
                    - https://old.example.test/charts/repo-golden-0.1.0.tgz
            """;
        var sharpExistingIndex = Path.Combine(_tempDir, "sharp-metadata-name-existing-index.yaml");
        var helmExistingIndex = Path.Combine(_tempDir, "helm-metadata-name-existing-index.yaml");
        await WriteTextAsync(sharpExistingIndex, staleEntryKeyIndex);
        await WriteTextAsync(helmExistingIndex, staleEntryKeyIndex);

        var sharpIndexPath = await HelmRepoIndexer.GenerateIndexAsync(
            sharpRepoDir,
            "https://new.example.test/charts",
            CancellationToken.None,
            mergeIndexPath: sharpExistingIndex);
        var helmMerged = await HelmCliRunner.RepoIndexAsync(
            helmRepoDir,
            "https://new.example.test/charts",
            CancellationToken.None,
            mergeIndexPath: helmExistingIndex);
        AssertOperationSucceeded("helm repo index metadata-name merge", helmMerged);

        var sharpIndex = HelmYaml.DeserializeDictionary(File.ReadAllText(sharpIndexPath, Encoding.UTF8));
        var helmIndex = HelmYaml.DeserializeDictionary(File.ReadAllText(Path.Combine(helmRepoDir, "index.yaml"), Encoding.UTF8));
        var sharpEntries = Assert.IsAssignableFrom<IDictionary<string, object?>>(sharpIndex["entries"]);
        var helmEntries = Assert.IsAssignableFrom<IDictionary<string, object?>>(helmIndex["entries"]);
        Assert.DoesNotContain("stale-entry-key", sharpEntries.Keys);
        Assert.DoesNotContain("stale-entry-key", helmEntries.Keys);
        Assert.Equal(helmEntries.Keys.Order(), sharpEntries.Keys.Order());

        var sharpSnapshot = ReadIndexSnapshot(sharpIndexPath, "repo-golden");
        var helmSnapshot = ReadIndexSnapshot(Path.Combine(helmRepoDir, "index.yaml"), "repo-golden");
        Assert.Equal(helmSnapshot.Versions.Select(version => version.Version), sharpSnapshot.Versions.Select(version => version.Version));
        Assert.Equal(helmSnapshot.Versions.Select(version => version.Url), sharpSnapshot.Versions.Select(version => version.Url));
    }

    [HelmCliFact]
    public async Task RepoIndexAsync_MergeDeduplicatesExistingVersions()
    {
        var sharpRepoDir = Path.Combine(_tempDir, "sharp-duplicate-merge-repo-index");
        var helmRepoDir = Path.Combine(_tempDir, "helm-duplicate-merge-repo-index");
        Directory.CreateDirectory(sharpRepoDir);
        Directory.CreateDirectory(helmRepoDir);

        const string duplicateVersionIndex = """
            apiVersion: v1
            entries:
              repo-golden:
                - apiVersion: v2
                  name: repo-golden
                  version: 1.0.0
                  created: 2024-01-01T00:00:00Z
                  urls:
                    - https://old.example.test/charts/first-repo-golden-1.0.0.tgz
                - apiVersion: v2
                  name: repo-golden
                  version: 1.0.0
                  created: 2024-01-02T00:00:00Z
                  urls:
                    - https://old.example.test/charts/duplicate-repo-golden-1.0.0.tgz
            """;
        var sharpExistingIndex = Path.Combine(_tempDir, "sharp-duplicate-existing-index.yaml");
        var helmExistingIndex = Path.Combine(_tempDir, "helm-duplicate-existing-index.yaml");
        await WriteTextAsync(sharpExistingIndex, duplicateVersionIndex);
        await WriteTextAsync(helmExistingIndex, duplicateVersionIndex);

        var sharpIndexPath = await HelmRepoIndexer.GenerateIndexAsync(
            sharpRepoDir,
            url: null,
            ct: CancellationToken.None,
            mergeIndexPath: sharpExistingIndex);
        var helmMerged = await HelmCliRunner.RepoIndexAsync(
            helmRepoDir,
            url: null,
            cancellationToken: CancellationToken.None,
            mergeIndexPath: helmExistingIndex);
        AssertOperationSucceeded("helm repo index duplicate merge", helmMerged);

        var sharpSnapshot = ReadIndexSnapshot(sharpIndexPath, "repo-golden");
        var helmSnapshot = ReadIndexSnapshot(Path.Combine(helmRepoDir, "index.yaml"), "repo-golden");
        Assert.Single(sharpSnapshot.Versions);
        Assert.Equal(helmSnapshot.Versions.Select(version => version.Version), sharpSnapshot.Versions.Select(version => version.Version));
        Assert.Equal(helmSnapshot.Versions.Select(version => version.Url), sharpSnapshot.Versions.Select(version => version.Url));
    }

    [HelmCliFact]
    public async Task RepoIndexAsync_MergeWithMissingIndexGeneratesNewIndex()
    {
        var sharpRepoDir = Path.Combine(_tempDir, "sharp-missing-merge-repo-index");
        var helmRepoDir = Path.Combine(_tempDir, "helm-missing-merge-repo-index");
        Directory.CreateDirectory(sharpRepoDir);
        Directory.CreateDirectory(helmRepoDir);
        await PackageRepoChartVersionAsync("1.0.0", sharpRepoDir);
        CopyPackages(sharpRepoDir, helmRepoDir);

        var sharpIndexPath = await HelmRepoIndexer.GenerateIndexAsync(
            sharpRepoDir,
            "https://repo.example.test/charts",
            CancellationToken.None,
            mergeIndexPath: Path.Combine(_tempDir, "missing-sharp-index.yaml"));
        var helmResult = await HelmCliRunner.RepoIndexAsync(
            helmRepoDir,
            "https://repo.example.test/charts",
            CancellationToken.None,
            mergeIndexPath: Path.Combine(_tempDir, "missing-helm-index.yaml"));
        AssertOperationSucceeded("helm repo index with missing merge file", helmResult);

        var sharpIndex = ReadIndexSnapshot(sharpIndexPath, "repo-golden");
        var helmIndex = ReadIndexSnapshot(Path.Combine(helmRepoDir, "index.yaml"), "repo-golden");
        Assert.Equal(helmIndex.Versions.Select(version => version.Version), sharpIndex.Versions.Select(version => version.Version));
        Assert.Equal(helmIndex.Versions.Select(version => version.Url), sharpIndex.Versions.Select(version => version.Url));
        Assert.Equal(helmIndex.Versions.Select(version => version.Digest), sharpIndex.Versions.Select(version => version.Digest));
    }

    [HelmCliFact]
    public async Task RepoIndexAsync_MergeWithInvalidExistingIndexFailsWithoutWritingIndex()
    {
        var sharpRepoDir = Path.Combine(_tempDir, "sharp-invalid-merge-repo-index");
        var helmRepoDir = Path.Combine(_tempDir, "helm-invalid-merge-repo-index");
        Directory.CreateDirectory(sharpRepoDir);
        Directory.CreateDirectory(helmRepoDir);
        await PackageRepoChartVersionAsync("1.0.0", sharpRepoDir);
        CopyPackages(sharpRepoDir, helmRepoDir);

        var sharpInvalidIndex = Path.Combine(_tempDir, "invalid-sharp-index.yaml");
        var helmInvalidIndex = Path.Combine(_tempDir, "invalid-helm-index.yaml");
        await WriteTextAsync(sharpInvalidIndex, "not: a Helm repository index\n");
        await WriteTextAsync(helmInvalidIndex, "not: a Helm repository index\n");

        await Assert.ThrowsAsync<InvalidDataException>(() => HelmRepoIndexer.GenerateIndexAsync(
            sharpRepoDir,
            "https://repo.example.test/charts",
            CancellationToken.None,
            mergeIndexPath: sharpInvalidIndex));
        var helmResult = await HelmCliRunner.RepoIndexAsync(
            helmRepoDir,
            "https://repo.example.test/charts",
            CancellationToken.None,
            mergeIndexPath: helmInvalidIndex);

        Assert.NotEqual(0, helmResult.ExitCode);
        Assert.False(File.Exists(Path.Combine(sharpRepoDir, "index.yaml")));
        Assert.False(File.Exists(Path.Combine(helmRepoDir, "index.yaml")));
    }

    [HelmCliFact]
    public async Task RepoIndexAsync_MergeWithDirectoryPathFailsWithoutWritingIndex()
    {
        var sharpRepoDir = Path.Combine(_tempDir, "sharp-directory-merge-repo-index");
        var helmRepoDir = Path.Combine(_tempDir, "helm-directory-merge-repo-index");
        var sharpMergeDirectory = Path.Combine(_tempDir, "sharp-merge-directory");
        var helmMergeDirectory = Path.Combine(_tempDir, "helm-merge-directory");
        Directory.CreateDirectory(sharpRepoDir);
        Directory.CreateDirectory(helmRepoDir);
        Directory.CreateDirectory(sharpMergeDirectory);
        Directory.CreateDirectory(helmMergeDirectory);
        await PackageRepoChartVersionAsync("1.0.0", sharpRepoDir);
        CopyPackages(sharpRepoDir, helmRepoDir);

        await Assert.ThrowsAsync<InvalidDataException>(() => HelmRepoIndexer.GenerateIndexAsync(
            sharpRepoDir,
            "https://repo.example.test/charts",
            CancellationToken.None,
            mergeIndexPath: sharpMergeDirectory));
        var helmResult = await HelmCliRunner.RepoIndexAsync(
            helmRepoDir,
            "https://repo.example.test/charts",
            CancellationToken.None,
            mergeIndexPath: helmMergeDirectory);

        Assert.NotEqual(0, helmResult.ExitCode);
        Assert.False(File.Exists(Path.Combine(sharpRepoDir, "index.yaml")));
        Assert.False(File.Exists(Path.Combine(helmRepoDir, "index.yaml")));
    }

    [Fact]
    public async Task RepoIndexAsync_InvalidArchiveReportsDiagnosticAndIndexesValidPackages()
    {
        var repoDir = Path.Combine(_tempDir, "repo-index-invalid-archive");
        Directory.CreateDirectory(repoDir);
        await PackageMinimalRepoChartAsync(repoDir);
        var invalidPackagePath = Path.Combine(repoDir, "broken-0.1.0.tgz");
        await File.WriteAllTextAsync(invalidPackagePath, "not a gzipped chart archive", Encoding.UTF8);
        var malformedChartYamlPath = await PackageMalformedChartYamlAsync(repoDir);

        var result = await HelmRepoIndexer.GenerateIndexWithDiagnosticsAsync(
            repoDir,
            "https://repo.example.test/base",
            CancellationToken.None);

        var index = HelmYaml.DeserializeDictionary(File.ReadAllText(result.IndexPath, Encoding.UTF8));
        var entries = Assert.IsAssignableFrom<IDictionary<string, object?>>(index["entries"]);
        Assert.True(entries.ContainsKey("repo-minimal"));
        Assert.Equal(
            [invalidPackagePath, malformedChartYamlPath],
            result.Diagnostics.Select(diagnostic => diagnostic.PackagePath).Order(StringComparer.Ordinal));
        Assert.All(result.Diagnostics, diagnostic =>
            Assert.False(string.IsNullOrWhiteSpace(diagnostic.Message)));
    }

    [Fact]
    public void RepoIndexAsync_PublicApiPreservesExistingOverloads()
    {
        var parameters = new[] { typeof(string), typeof(string), typeof(CancellationToken) };

        Assert.NotNull(typeof(HelmRepoIndexer).GetMethod(nameof(HelmRepoIndexer.GenerateIndexAsync), parameters));
        Assert.NotNull(typeof(HelmRepoIndexer).GetMethod(nameof(HelmRepoIndexer.GenerateIndexWithDiagnosticsAsync), parameters));
        Assert.NotNull(typeof(HelmClient).GetMethod(nameof(HelmClient.RepoIndexAsync), parameters));
        Assert.NotNull(typeof(IHelmClient).GetMethod(nameof(IHelmClient.RepoIndexAsync), parameters));
    }

    [HelmCliFact]
    public async Task PullAsync_DirectArchiveUrlAndRepositoryReferenceMatchHelmPull()
    {
        var repoDir = Path.Combine(_tempDir, "pull-repo");
        Directory.CreateDirectory(repoDir);
        await PackageRepoChartVersionAsync("1.0.0", repoDir);
        await PackageRepoChartVersionAsync("1.1.0", repoDir);
        await using var server = await LocalFileServer.StartAsync(repoDir);
        await HelmRepoIndexer.GenerateIndexAsync(repoDir, server.BaseUrl, CancellationToken.None);

        await AssertPullMatchesHelmAsync(
            $"{server.BaseUrl}/repo-golden-1.1.0.tgz",
            helmRepoUrl: null,
            version: null,
            expectedPackageName: "repo-golden-1.1.0.tgz");
        await AssertPullMatchesHelmAsync(
            "repo-golden",
            helmRepoUrl: server.BaseUrl,
            version: "1.0.0",
            expectedPackageName: "repo-golden-1.0.0.tgz");

        async Task AssertPullMatchesHelmAsync(
            string helmChartRef,
            string? helmRepoUrl,
            string? version,
            string expectedPackageName)
        {
            var helmDestination = Path.Combine(_tempDir, $"helm-pull-{Guid.NewGuid():N}");
            Directory.CreateDirectory(helmDestination);
            var helmResult = await HelmCliRunner.PullAsync(
                helmChartRef,
                helmDestination,
                version,
                helmRepoUrl,
                CancellationToken.None);

            var sharpRef = helmRepoUrl is null
                ? helmChartRef
                : $"{helmRepoUrl.TrimEnd('/')}/{helmChartRef}";
            using var repository = new HelmChartRepository(Path.Combine(_tempDir, $"sharp-cache-{Guid.NewGuid():N}"));
            var sharpPath = await repository.PullChartAsync(sharpRef, version, CancellationToken.None);

            AssertOperationSucceeded("helm pull", helmResult);
            var helmChart = await HelmChartLoader.LoadAsync(Path.Combine(helmDestination, expectedPackageName), CancellationToken.None);
            var sharpChart = await HelmChartLoader.LoadAsync(sharpPath, CancellationToken.None);
            AssertChartSnapshotsEqual(CreateChartSnapshot(helmChart), CreateChartSnapshot(sharpChart));
        }
    }

    [Fact]
    public async Task PullAsync_RepositoryReferenceResolvesVersionsBySemverConstraint()
    {
        var repoDir = Path.Combine(_tempDir, "semver-pull-repo");
        Directory.CreateDirectory(repoDir);
        await PackageRepoChartVersionAsync("1.2.0", repoDir);
        await PackageRepoChartVersionAsync("1.2.5+build.7", repoDir);
        await PackageRepoChartVersionAsync("1.10.0", repoDir);
        await PackageRepoChartVersionAsync("2.0.0-beta.1", repoDir);
        await using var server = await LocalFileServer.StartAsync(repoDir);
        await HelmRepoIndexer.GenerateIndexAsync(repoDir, server.BaseUrl, CancellationToken.None);

        await AssertPulledVersionAsync(version: null, expectedVersion: "1.10.0");
        await AssertPulledVersionAsync(version: "~1.2.0", expectedVersion: "1.2.5+build.7");
        await AssertPulledVersionAsync(version: ">=2.0.0-beta.1 <2.0.0", expectedVersion: "2.0.0-beta.1");

        async Task AssertPulledVersionAsync(string? version, string expectedVersion)
        {
            using var repository = new HelmChartRepository(Path.Combine(_tempDir, $"sharp-cache-{Guid.NewGuid():N}"));
            var sharpPath = await repository.PullChartAsync(
                new HelmPullRequest
                {
                    ChartReference = "repo-golden",
                    RepositoryUrl = server.BaseUrl,
                    Version = version
                },
                CancellationToken.None);
            var chart = await HelmChartLoader.LoadAsync(sharpPath, CancellationToken.None);

            Assert.Equal(expectedVersion, chart.Version);
        }
    }

    [Fact]
    public async Task PullAsync_RepositoryReferenceResolvesShortChartVersions()
    {
        var repoDir = Path.Combine(_tempDir, "short-version-pull-repo");
        Directory.CreateDirectory(repoDir);
        await PackageRepoChartVersionAsync("1", repoDir);
        await PackageRepoChartVersionAsync("1.2", repoDir);
        await using var server = await LocalFileServer.StartAsync(repoDir);
        await HelmRepoIndexer.GenerateIndexAsync(repoDir, server.BaseUrl, CancellationToken.None);

        await AssertPulledVersionAsync(version: null, expectedVersion: "1.2");
        await AssertPulledVersionAsync(version: "1.2", expectedVersion: "1.2");

        async Task AssertPulledVersionAsync(string? version, string expectedVersion)
        {
            using var repository = new HelmChartRepository(Path.Combine(_tempDir, $"sharp-cache-{Guid.NewGuid():N}"));
            var sharpPath = await repository.PullChartAsync(
                $"{server.BaseUrl}/repo-golden",
                version,
                CancellationToken.None);
            var chart = await HelmChartLoader.LoadAsync(sharpPath, CancellationToken.None);

            Assert.Equal(expectedVersion, chart.Version);
        }
    }

    [Fact]
    public async Task PullAsync_RequestAppliesCredentialsToIndexAndArchiveDownloads()
    {
        const string username = "chart-user";
        const string password = "chart-password";
        var repoDir = Path.Combine(_tempDir, "authenticated-pull-repo");
        Directory.CreateDirectory(repoDir);
        await PackageRepoChartVersionAsync("1.2.3", repoDir);
        await using var server = await LocalFileServer.StartAsync(repoDir, username, password);
        await HelmRepoIndexer.GenerateIndexAsync(repoDir, server.BaseUrl, CancellationToken.None);

        using var repository = new HelmChartRepository(Path.Combine(_tempDir, "authenticated-pull-cache"));
        var chartPath = await repository.PullChartAsync(
            new HelmPullRequest
            {
                ChartReference = "repo-golden",
                RepositoryUrl = server.BaseUrl,
                Version = "1.2.3",
                Username = username,
                Password = password
            },
            CancellationToken.None);

        var chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);
        Assert.Equal("1.2.3", chart.Version);
    }

    [Fact]
    public async Task PullAsync_RequestDoesNotForwardCredentialsToCrossOriginArchiveByDefault()
    {
        const string username = "chart-user";
        const string password = "chart-password";
        var archiveDir = Path.Combine(_tempDir, "cross-origin-archive");
        var indexDir = Path.Combine(_tempDir, "cross-origin-index");
        Directory.CreateDirectory(archiveDir);
        Directory.CreateDirectory(indexDir);
        await PackageRepoChartVersionAsync("1.2.3", archiveDir);
        File.Copy(
            Path.Combine(archiveDir, "repo-golden-1.2.3.tgz"),
            Path.Combine(indexDir, "repo-golden-1.2.3.tgz"));
        await using var archiveServer = await LocalFileServer.StartAsync(archiveDir);
        await HelmRepoIndexer.GenerateIndexAsync(indexDir, archiveServer.BaseUrl, CancellationToken.None);
        await using var indexServer = await LocalFileServer.StartAsync(indexDir, username, password);

        using var repository = new HelmChartRepository(Path.Combine(_tempDir, "cross-origin-cache"));
        var chartPath = await repository.PullChartAsync(
            new HelmPullRequest
            {
                ChartReference = "repo-golden",
                RepositoryUrl = indexServer.BaseUrl,
                Username = username,
                Password = password
            },
            CancellationToken.None);

        var chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);
        Assert.Equal("1.2.3", chart.Version);
        Assert.Null(archiveServer.LastAuthorization);
    }

    [Fact]
    public async Task PullAsync_RequestCanExplicitlyForwardCredentialsToCrossOriginArchive()
    {
        const string username = "chart-user";
        const string password = "chart-password";
        var archiveDir = Path.Combine(_tempDir, "pass-credentials-archive");
        var indexDir = Path.Combine(_tempDir, "pass-credentials-index");
        Directory.CreateDirectory(archiveDir);
        Directory.CreateDirectory(indexDir);
        await PackageRepoChartVersionAsync("1.2.3", archiveDir);
        File.Copy(
            Path.Combine(archiveDir, "repo-golden-1.2.3.tgz"),
            Path.Combine(indexDir, "repo-golden-1.2.3.tgz"));
        await using var archiveServer = await LocalFileServer.StartAsync(archiveDir, username, password);
        await HelmRepoIndexer.GenerateIndexAsync(indexDir, archiveServer.BaseUrl, CancellationToken.None);
        await using var indexServer = await LocalFileServer.StartAsync(indexDir, username, password);

        using var repository = new HelmChartRepository(Path.Combine(_tempDir, "pass-credentials-cache"));
        var chartPath = await repository.PullChartAsync(
            new HelmPullRequest
            {
                ChartReference = "repo-golden",
                RepositoryUrl = indexServer.BaseUrl,
                Username = username,
                Password = password,
                PassCredentialsAll = true
            },
            CancellationToken.None);

        var chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);
        Assert.Equal("1.2.3", chart.Version);
        Assert.NotNull(archiveServer.LastAuthorization);
    }

    [HelmCliFact]
    public async Task DependencyWorkflows_LocalRepositoryMatchHelmOutputsAndPackages()
    {
        var repoDir = Path.Combine(_tempDir, "dependency-repo");
        Directory.CreateDirectory(repoDir);
        await PackageDependencyChartAsync(repoDir);
        await using var server = await LocalFileServer.StartAsync(repoDir);
        await HelmRepoIndexer.GenerateIndexAsync(repoDir, server.BaseUrl, CancellationToken.None);

        var sharpUpdateChart = await CreateDependencyParentChartAsync("sharp-update-parent", server.BaseUrl);
        var helmUpdateChart = await CreateDependencyParentChartAsync("helm-update-parent", server.BaseUrl);
        var client = CreateClient();

        var sharpMissing = await client.DependencyListAsync(sharpUpdateChart);
        var helmMissing = await HelmCliRunner.DependencyListAsync(helmUpdateChart, CancellationToken.None);
        AssertOperationSucceeded("helm dependency list before update", helmMissing);
        Assert.Equal("missing", ParseDependencyStatus(sharpMissing.StandardOutput, "child-dep"));
        Assert.Equal("missing", ParseDependencyStatus(helmMissing.Stdout, "child-dep"));

        var sharpUpdate = await client.DependencyUpdateAsync(sharpUpdateChart);
        var helmUpdate = await HelmCliRunner.DependencyUpdateAsync(helmUpdateChart, CancellationToken.None);
        AssertOperationSucceeded("helm dependency update", helmUpdate);
        AssertOperationSucceeded("HelmSharp DependencyUpdateAsync", sharpUpdate);
        await AssertDependencyPackageMatchesAsync(helmUpdateChart, sharpUpdateChart, "0.2.5");

        var sharpOk = await client.DependencyListAsync(sharpUpdateChart);
        var helmOk = await HelmCliRunner.DependencyListAsync(helmUpdateChart, CancellationToken.None);
        AssertOperationSucceeded("helm dependency list after update", helmOk);
        Assert.Equal("ok", ParseDependencyStatus(sharpOk.StandardOutput, "child-dep"));
        Assert.Equal("ok", ParseDependencyStatus(helmOk.Stdout, "child-dep"));

        var sharpBuildChart = Path.Combine(_tempDir, "sharp-build-parent");
        var helmBuildChart = Path.Combine(_tempDir, "helm-build-parent");
        CopyDirectory(sharpUpdateChart, sharpBuildChart);
        CopyDirectory(helmUpdateChart, helmBuildChart);

        var sharpBuild = await client.DependencyBuildAsync(sharpBuildChart);
        using var helmBuildHome = HelmCliRunner.CreateHome();
        var helmRepoAdd = await HelmCliRunner.RepoAddAsync("local", server.BaseUrl, helmBuildHome, CancellationToken.None);
        AssertOperationSucceeded("helm repo add for dependency build", helmRepoAdd);
        var helmBuild = await HelmCliRunner.DependencyBuildAsync(helmBuildChart, helmBuildHome, CancellationToken.None);
        AssertOperationSucceeded("helm dependency build", helmBuild);
        AssertOperationSucceeded("HelmSharp DependencyBuildAsync", sharpBuild);
        await AssertDependencyPackageMatchesAsync(helmBuildChart, sharpBuildChart, "0.2.5");
    }

    private async Task<string> CreatePackageGoldenChartAsync(string directoryName, string version)
    {
        var chartDir = await CreateChartAsync(directoryName, $"""
            apiVersion: v2
            name: package-golden
            version: {version}
            appVersion: "2.4"
            description: Package golden chart
            type: application
            keywords:
              - golden
              - package
            maintainers:
              - name: HelmSharp
                email: maintainers@example.test
            """);
        await WriteTextAsync(Path.Combine(chartDir, ".helmignore"), """
            ignored/
            *.tmp
            """);
        await WriteTextAsync(Path.Combine(chartDir, "values.yaml"), "replicaCount: 2\n");
        await WriteTextAsync(Path.Combine(chartDir, "templates", "deployment.yaml"), "kind: Deployment\n");
        await WriteTextAsync(Path.Combine(chartDir, "crds", "widgets.yaml"), """
            apiVersion: apiextensions.k8s.io/v1
            kind: CustomResourceDefinition
            metadata:
              name: widgets.example.com
            """);
        await WriteTextAsync(Path.Combine(chartDir, "README.md"), "# Package golden\n");
        await WriteTextAsync(Path.Combine(chartDir, "ignored", "drop.yaml"), "ignored\n");
        await WriteTextAsync(Path.Combine(chartDir, "notes.tmp"), "ignored\n");
        await WriteBytesAsync(Path.Combine(chartDir, "files", "payload.bin"), [0, 1, 2, 127, 128, 255]);
        return chartDir;
    }

    private async Task PackageRepoChartVersionAsync(
        string version,
        string destination,
        string description = "Repository golden chart")
    {
        var chartDir = await CreateChartAsync($"repo-golden-{version}", $"""
            apiVersion: v2
            name: repo-golden
            version: {version}
            appVersion: v1.0
            description: {description}
            type: application
            """);
        await WriteTextAsync(Path.Combine(chartDir, "values.yaml"), $"version: {version}\n");
        await WriteTextAsync(Path.Combine(chartDir, "templates", "configmap.yaml"), $"version: {version}\n");
        await HelmChartPackager.PackageAsync(chartDir, destination, cancellationToken: CancellationToken.None);
    }

    private async Task PackageFullMetadataRepoChartAsync(string destination)
    {
        var chartDir = await CreateChartAsync("repo-full-metadata-source", """
            apiVersion: v2
            name: repo-full-metadata
            version: 2.0.0-beta.1
            appVersion: "3.4"
            description: Repository full metadata chart
            type: library
            home: https://example.test/repo-full-metadata
            icon: https://example.test/repo-full-metadata/icon.svg
            kubeVersion: ">=1.25.0"
            deprecated: true
            keywords:
              - repository
              - golden
            sources:
              - https://github.com/example/repo-full-metadata
            maintainers:
              - name: HelmSharp Maintainer
                email: maintainer@example.test
                url: https://example.test/maintainer
            annotations:
              category: tests
              empty: ""
            dependencies:
              - name: repo-child
                version: 0.1.0
                repository: file://charts/repo-child
                condition: repoChild.enabled
                tags:
                  - optional
                import-values:
                  - child
                  - child: parent
                alias: child-alias
            """);
        var childDir = Path.Combine(chartDir, "charts", "repo-child");
        Directory.CreateDirectory(childDir);
        await WriteTextAsync(Path.Combine(childDir, "Chart.yaml"), """
            apiVersion: v2
            name: repo-child
            version: 0.1.0
            """);
        await WriteTextAsync(Path.Combine(chartDir, "values.yaml"), "repoChild:\n  enabled: true\n");
        await WriteTextAsync(Path.Combine(chartDir, "templates", "configmap.yaml"), "kind: ConfigMap\n");
        await HelmChartPackager.PackageAsync(chartDir, destination, cancellationToken: CancellationToken.None);
    }

    private static async Task<string> PackageMalformedChartYamlAsync(string destination)
    {
        var packagePath = Path.Combine(destination, "broken-yaml-0.1.0.tgz");
        await using var file = File.Create(packagePath);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        await using var tar = new TarWriter(gzip);

        await WriteTarEntryAsync(tar, "broken-yaml/Chart.yaml", """
            apiVersion: v2
            name: [broken
            version: 0.1.0
            """);
        await WriteTarEntryAsync(tar, "broken-yaml/values.yaml", "valid: true\n");
        return packagePath;
    }

    private async Task PackageMinimalRepoChartAsync(string destination)
    {
        var chartDir = await CreateChartAsync("repo-minimal-source", """
            apiVersion: v2
            name: repo-minimal
            version: 1.0.0
            """);
        await HelmChartPackager.PackageAsync(chartDir, destination, cancellationToken: CancellationToken.None);
    }

    private async Task PackageDependencyChartAsync(string destination)
    {
        await PackageDependencyChartVersionAsync("0.2.0", destination);
        await PackageDependencyChartVersionAsync("0.2.5", destination);
        await PackageDependencyChartVersionAsync("0.3.0-beta.1", destination);
    }

    private async Task PackageDependencyChartVersionAsync(string version, string destination)
    {
        var chartDir = await CreateChartAsync($"child-dep-source-{version}", $"""
            apiVersion: v2
            name: child-dep
            version: {version}
            appVersion: v2
            description: Local dependency chart
            type: application
            """);
        await WriteTextAsync(Path.Combine(chartDir, "values.yaml"), $"childVersion: {version}\n");
        await WriteTextAsync(Path.Combine(chartDir, "templates", "configmap.yaml"), $"version: {version}\n");
        await HelmChartPackager.PackageAsync(chartDir, destination, cancellationToken: CancellationToken.None);
    }

    private async Task<string> CreateDependencyParentChartAsync(string directoryName, string repositoryUrl)
    {
        var chartDir = await CreateChartAsync(directoryName, $"""
            apiVersion: v2
            name: dependency-parent
            version: 0.1.0
            dependencies:
              - name: child-dep
                version: ~0.2.0
                repository: {repositoryUrl}
            """);
        await WriteTextAsync(Path.Combine(chartDir, "values.yaml"), "parent: true\n");
        await WriteTextAsync(Path.Combine(chartDir, "templates", "configmap.yaml"), "kind: ConfigMap\n");
        return chartDir;
    }

    private async Task<string> CreateChartAsync(string directoryName, string chartYaml)
    {
        var chartDir = Path.Combine(_tempDir, directoryName);
        Directory.CreateDirectory(chartDir);
        await WriteTextAsync(Path.Combine(chartDir, "Chart.yaml"), chartYaml);
        return chartDir;
    }

    private static async Task AssertDependencyPackageMatchesAsync(
        string helmChartDir,
        string sharpChartDir,
        string expectedVersion)
    {
        var helmPackage = Path.Combine(helmChartDir, "charts", $"child-dep-{expectedVersion}.tgz");
        var sharpPackage = Path.Combine(sharpChartDir, "charts", $"child-dep-{expectedVersion}.tgz");
        Assert.True(File.Exists(helmPackage), $"Helm dependency package was not written: {helmPackage}");
        Assert.True(File.Exists(sharpPackage), $"HelmSharp dependency package was not written: {sharpPackage}");
        Assert.Equal(
            await ReadArchiveSnapshotAsync(helmPackage),
            await ReadArchiveSnapshotAsync(sharpPackage));
    }

    private static IndexSnapshot ReadIndexSnapshot(string indexPath, string chartName)
    {
        var index = HelmYaml.DeserializeDictionary(File.ReadAllText(indexPath, Encoding.UTF8));
        Assert.True(index.TryGetValue("entries", out var entriesObject), $"entries missing from {indexPath}");
        var entries = Assert.IsAssignableFrom<IDictionary<string, object?>>(entriesObject);
        Assert.True(entries.TryGetValue(chartName, out var versionsObject), $"{chartName} missing from {indexPath}");
        var versions = Assert.IsAssignableFrom<IList<object?>>(versionsObject);

        return new IndexSnapshot(
            HelmYaml.GetString(index, "apiVersion") ?? string.Empty,
            HelmYaml.GetString(index, "generated") ?? string.Empty,
            versions
                .Cast<IDictionary<string, object?>>()
                .Select(version =>
                {
                    var urls = Assert.IsAssignableFrom<IList<object?>>(version["urls"]);
                    return new IndexVersionSnapshot(
                        HelmYaml.GetString(version, "version") ?? string.Empty,
                        Convert.ToString(urls.Single()) ?? string.Empty,
                        HelmYaml.GetString(version, "digest") ?? string.Empty,
                        HelmYaml.GetString(version, "created") ?? string.Empty);
                })
                .ToList());
    }

    private static Dictionary<string, object?> ReadNormalizedIndexEntry(string indexPath, string chartName)
    {
        var index = HelmYaml.DeserializeDictionary(File.ReadAllText(indexPath, Encoding.UTF8));
        Assert.True(index.TryGetValue("entries", out var entriesObject), $"entries missing from {indexPath}");
        var entries = Assert.IsAssignableFrom<IDictionary<string, object?>>(entriesObject);
        Assert.True(entries.TryGetValue(chartName, out var versionsObject), $"{chartName} missing from {indexPath}");
        var versions = Assert.IsAssignableFrom<IList<object?>>(versionsObject);
        var entry = new Dictionary<string, object?>(
            Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(versions)),
            StringComparer.OrdinalIgnoreCase);
        entry.Remove("created");
        return entry;
    }

    private static ChartSnapshot CreateChartSnapshot(HelmChart chart)
        => new(
            chart.Name,
            chart.Version,
            HelmCliRunner.NormalizeLineEndings(chart.ValuesYaml),
            chart.Templates.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList(),
            chart.Files
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new FileSnapshot(kv.Key, Convert.ToHexString(SHA256.HashData(kv.Value))))
                .ToList());

    private static void AssertChartSnapshotsEqual(ChartSnapshot expected, ChartSnapshot actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.ValuesYaml, actual.ValuesYaml);
        Assert.Equal(expected.Templates, actual.Templates);
        Assert.Equal(expected.Files, actual.Files);
    }

    private static string ParseDependencyStatus(string output, string dependencyName)
    {
        foreach (var line in HelmCliRunner.NormalizeLineEndings(output).Split('\n'))
        {
            if (!line.StartsWith(dependencyName, StringComparison.Ordinal))
                continue;

            var columns = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (columns.Length >= 4)
                return columns[^1].Trim().ToLowerInvariant();

            var whitespaceColumns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (whitespaceColumns.Length >= 4)
                return whitespaceColumns[^1].Trim().ToLowerInvariant();
        }

        throw new InvalidOperationException($"Dependency {dependencyName} was not found in output:{Environment.NewLine}{output}");
    }

    private static async Task<List<ArchiveEntrySnapshot>> ReadArchiveSnapshotAsync(string packagePath)
    {
        var entries = new List<ArchiveEntrySnapshot>();
        await using var file = File.OpenRead(packagePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            var hash = string.Empty;
            var size = 0L;
            if (entry.DataStream is not null)
            {
                using var memory = new MemoryStream();
                await entry.DataStream.CopyToAsync(memory);
                var content = NormalizeArchiveEntryContent(entry.Name, memory.ToArray());
                size = content.Length;
                hash = Convert.ToHexString(SHA256.HashData(content));
            }

            entries.Add(new ArchiveEntrySnapshot(entry.Name, entry.EntryType, entry.Mode, size, hash));
        }

        return entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList();
    }

    private static async Task<string> ComputeSha256Async(string path)
        => Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();

    private static byte[] NormalizeArchiveEntryContent(string entryName, byte[] content)
    {
        if (!entryName.EndsWith("/Chart.yaml", StringComparison.OrdinalIgnoreCase))
            return content;

        var chartYaml = Encoding.UTF8.GetString(content);
        return Encoding.UTF8.GetBytes(HelmYaml.Serialize(HelmYaml.DeserializeDictionary(chartYaml)));
    }

    private static void CopyPackages(string sourceDir, string destinationDir)
    {
        foreach (var packagePath in Directory.EnumerateFiles(sourceDir, "*.tgz"))
        {
            var destinationPath = Path.Combine(destinationDir, Path.GetFileName(packagePath));
            File.Copy(packagePath, destinationPath);
            File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(packagePath));
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, sourcePath);
            var destinationPath = Path.Combine(destinationDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
            File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
        }
    }

    private static string GetSinglePackagePath(string destination)
        => Directory.EnumerateFiles(destination, "*.tgz", SearchOption.AllDirectories).Single();

    private static HelmClient CreateClient()
        => new(new StaticHelmOptionsProvider());

    private static void AssertOperationSucceeded(string operation, HelmCliResult result)
        => Assert.True(
            result.ExitCode == 0,
            $"{operation} failed with exit code {result.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{result.Stderr}");

    private static void AssertOperationSucceeded(string operation, CommandResult result)
        => Assert.True(
            result.ExitCode == 0,
            $"{operation} failed with exit code {result.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}stderr:{Environment.NewLine}{result.StandardError}");

    private static async Task WriteTextAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static async Task WriteTarEntryAsync(TarWriter tar, string entryName, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var entry = new GnuTarEntry(TarEntryType.RegularFile, entryName)
        {
            DataStream = new MemoryStream(bytes)
        };
        await using (entry.DataStream)
        {
            await tar.WriteEntryAsync(entry);
        }
    }

    private static async Task WriteBytesAsync(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content);
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

    private sealed class LocalFileServer : IAsyncDisposable
    {
        private readonly string _rootDirectory;
        private readonly TcpListener _listener;
        private readonly string? _expectedAuthorization;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serverTask;

        private LocalFileServer(
            string rootDirectory,
            TcpListener listener,
            string? expectedAuthorization)
        {
            _rootDirectory = rootDirectory;
            _listener = listener;
            _expectedAuthorization = expectedAuthorization;
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            BaseUrl = $"http://127.0.0.1:{endpoint.Port}";
            _serverTask = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl { get; }

        public string? LastAuthorization { get; private set; }

        public static Task<LocalFileServer> StartAsync(
            string rootDirectory,
            string? username = null,
            string? password = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var expectedAuthorization = username is null
                ? null
                : $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"))}";
            return Task.FromResult(new LocalFileServer(rootDirectory, listener, expectedAuthorization));
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client), _cancellation.Token);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync();
                if (requestLine is null)
                    return;

                string? authorization = null;
                string? headerLine;
                while (!string.IsNullOrEmpty(headerLine = await reader.ReadLineAsync()))
                {
                    const string authorizationPrefix = "Authorization:";
                    if (headerLine.StartsWith(authorizationPrefix, StringComparison.OrdinalIgnoreCase))
                        authorization = headerLine[authorizationPrefix.Length..].Trim();
                }
                LastAuthorization = authorization;

                var parts = requestLine.Split(' ');
                if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, HttpStatusCode.MethodNotAllowed, []);
                    return;
                }

                if (_expectedAuthorization is not null &&
                    !string.Equals(authorization, _expectedAuthorization, StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, HttpStatusCode.Unauthorized, []);
                    return;
                }

                var requestPath = Uri.UnescapeDataString(parts[1].Split('?', 2)[0]).TrimStart('/');
                var localPath = Path.GetFullPath(Path.Combine(_rootDirectory, requestPath.Replace('/', Path.DirectorySeparatorChar)));
                var rootPath = Path.GetFullPath(_rootDirectory);
                if (!IsInsideRoot(localPath, rootPath) || !File.Exists(localPath))
                {
                    await WriteResponseAsync(stream, HttpStatusCode.NotFound, []);
                    return;
                }

                await WriteResponseAsync(stream, HttpStatusCode.OK, await File.ReadAllBytesAsync(localPath));
            }
        }

        private static bool IsInsideRoot(string localPath, string rootPath)
        {
            var rootWithSeparator = rootPath.EndsWith(Path.DirectorySeparatorChar)
                ? rootPath
                : rootPath + Path.DirectorySeparatorChar;
            return localPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task WriteResponseAsync(Stream stream, HttpStatusCode statusCode, byte[] body)
        {
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {(int)statusCode} {statusCode}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            if (body.Length > 0)
                await stream.WriteAsync(body);
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try { await _serverTask; }
            catch { }
            _cancellation.Dispose();
        }
    }

    private sealed record ArchiveEntrySnapshot(
        string Name,
        TarEntryType EntryType,
        UnixFileMode Mode,
        long Size,
        string Sha256);

    private sealed record IndexSnapshot(
        string ApiVersion,
        string Generated,
        List<IndexVersionSnapshot> Versions);

    private sealed record IndexVersionSnapshot(
        string Version,
        string Url,
        string Digest,
        string Created);

    private sealed record ChartSnapshot(
        string Name,
        string Version,
        string ValuesYaml,
        List<KeyValuePair<string, string>> Templates,
        List<FileSnapshot> Files);

    private sealed record FileSnapshot(string Path, string Sha256);
}
