using HelmSharp.Repo;

namespace HelmSharp.Tests;

public sealed class HelmChartRepositoryConfigurationTests : IDisposable
{
    private readonly string _tempDir;

    public HelmChartRepositoryConfigurationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "helmsharp-repository-configuration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task AddRepositoryAsync_PersistsHelmCompatibleConfigurationAcrossInstances()
    {
        var options = CreateOptions();
        using (var repository = new HelmChartRepository(options))
        {
            await repository.AddRepositoryAsync("stable", "https://charts.example.test/");
            await repository.AddRepositoryAsync("private", "https://private.example.test", "alice", "secret");
        }

        using var reloadedRepository = new HelmChartRepository(options);
        var repositories = await reloadedRepository.ListRepositoriesAsync();

        Assert.Collection(repositories,
            repository =>
            {
                Assert.Equal("private", repository.Name);
                Assert.Equal("https://private.example.test", repository.Url);
                Assert.Equal("alice", repository.Username);
                Assert.Equal("secret", repository.Password);
            },
            repository =>
            {
                Assert.Equal("stable", repository.Name);
                Assert.Equal("https://charts.example.test", repository.Url);
            });
        var config = await File.ReadAllTextAsync(reloadedRepository.RepositoryConfigPath);
        Assert.Contains("apiVersion: v1", config, StringComparison.Ordinal);
        Assert.Contains("repositories:", config, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveRepositoryAsync_RemovesOnlyRequestedRepository()
    {
        using var repository = new HelmChartRepository(CreateOptions());
        await repository.AddRepositoryAsync("first", "https://first.example.test");
        await repository.AddRepositoryAsync("second", "https://second.example.test");

        await repository.RemoveRepositoryAsync("first");

        var remaining = Assert.Single(await repository.ListRepositoriesAsync());
        Assert.Equal("second", remaining.Name);
    }

    [Fact]
    public async Task AddRepositoryAsync_RejectsDuplicateAndInvalidNames()
    {
        using var repository = new HelmChartRepository(CreateOptions());
        await repository.AddRepositoryAsync("valid-name", "https://charts.example.test");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.AddRepositoryAsync("valid-name", "https://other.example.test"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.AddRepositoryAsync("not/a/repository", "https://charts.example.test"));
    }

    [Fact]
    public async Task AddRepositoryAsync_PreservesExistingNamesThatDifferOnlyByCase()
    {
        var options = CreateOptions();
        var configPath = Path.Combine(options.ConfigDirectory!, "repositories.yaml");
        Directory.CreateDirectory(options.ConfigDirectory!);
        await File.WriteAllTextAsync(configPath, """
            apiVersion: v1
            repositories:
              - name: Stable
                url: https://stable.example.test
            """);
        using var repository = new HelmChartRepository(options);

        await repository.AddRepositoryAsync("stable", "https://lowercase.example.test");

        Assert.Equal(["Stable", "stable"], (await repository.ListRepositoriesAsync()).Select(entry => entry.Name));
    }

    [Theory]
    [InlineData("entries: {}\n")]
    [InlineData("apiVersion: v1\nentries: []\n")]
    public void ParseRepoIndex_RejectsMissingOrInvalidRequiredFields(string yaml)
    {
        Assert.Throws<InvalidDataException>(() => HelmChartRepository.ParseRepoIndex(yaml));
    }

    [Fact]
    public async Task ListRepositoriesAsync_LoadsLegacyJsonWhenYamlConfigurationIsAbsent()
    {
        var options = CreateOptions();
        Directory.CreateDirectory(options.CacheDirectory!);
        var legacyRepositories = new Dictionary<string, HelmRepository>
        {
            ["legacy"] = new()
            {
                Name = "legacy",
                Url = "https://legacy.example.test",
                Username = "alice"
            }
        };
        await File.WriteAllTextAsync(
            Path.Combine(options.CacheDirectory!, "repositories.json"),
            System.Text.Json.JsonSerializer.Serialize(legacyRepositories));
        using var repository = new HelmChartRepository(options);

        var legacy = Assert.Single(await repository.ListRepositoriesAsync());

        Assert.Equal("legacy", legacy.Name);
        Assert.Equal("https://legacy.example.test", legacy.Url);
        Assert.Equal("alice", legacy.Username);
    }

    [Fact]
    public async Task RepositoryConfigPath_OverridesConfigDirectory()
    {
        var configuredPath = Path.Combine(_tempDir, "custom", "named-repositories.yaml");
        var options = new HelmRepositoryOptions
        {
            ConfigDirectory = Path.Combine(_tempDir, "unused-config"),
            CacheDirectory = Path.Combine(_tempDir, "cache"),
            RepositoryConfigPath = configuredPath
        };
        using var repository = new HelmChartRepository(options);

        await repository.AddRepositoryAsync("stable", "https://charts.example.test");

        Assert.Equal(configuredPath, repository.RepositoryConfigPath);
        Assert.True(File.Exists(configuredPath));
        Assert.False(File.Exists(Path.Combine(_tempDir, "unused-config", "repositories.yaml")));
    }

    [Fact]
    public void ResolveRepositoryConfigPath_ConfigDirectoryOverridesEnvironmentPath()
    {
        var configDirectory = Path.Combine(_tempDir, "isolated-config");
        var environmentPath = Path.Combine(_tempDir, "user-config", "repositories.yaml");

        var resolvedPath = HelmChartRepository.ResolveRepositoryConfigPath(
            new HelmRepositoryOptions { ConfigDirectory = configDirectory },
            environmentPath);

        Assert.Equal(Path.Combine(configDirectory, "repositories.yaml"), resolvedPath);
    }

    [Fact]
    public void GetRepositoryIndexCacheFileName_IsDeterministicAndSafe()
    {
        var cacheFileName = HelmChartRepository.GetRepositoryIndexCacheFileName("../../unsafe");

        Assert.Equal(cacheFileName, HelmChartRepository.GetRepositoryIndexCacheFileName("../../unsafe"));
        Assert.Equal("unsafe-index.yaml", cacheFileName);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, cacheFileName);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, cacheFileName);
    }

    [Fact]
    public void GetRepositoryIndexCacheFileName_UsesEachRepositoryName()
    {
        Assert.Equal("first-index.yaml", HelmChartRepository.GetRepositoryIndexCacheFileName("first"));
        Assert.Equal("second-index.yaml", HelmChartRepository.GetRepositoryIndexCacheFileName("second"));
    }

    [Fact]
    public void GetRepositoryCacheDirectory_AppendsRepositoryDirectoryToHelmCacheHome()
    {
        var cacheHome = Path.Combine(_tempDir, "helm-cache");

        Assert.Equal(Path.Combine(cacheHome, "repository"), HelmChartRepository.GetRepositoryCacheDirectory(cacheHome));
    }

    private HelmRepositoryOptions CreateOptions()
        => new()
        {
            ConfigDirectory = Path.Combine(_tempDir, "config"),
            CacheDirectory = Path.Combine(_tempDir, "cache")
        };

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }
}
