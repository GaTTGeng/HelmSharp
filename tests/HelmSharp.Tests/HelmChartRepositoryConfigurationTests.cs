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
    public void GetRepositoryIndexCacheFileName_IsDeterministicAndSafe()
    {
        var cacheFileName = HelmChartRepository.GetRepositoryIndexCacheFileName("../../unsafe", "https://charts.example.test/path");

        Assert.Equal(cacheFileName, HelmChartRepository.GetRepositoryIndexCacheFileName("../../unsafe", "https://charts.example.test/path"));
        Assert.Matches("^unsafe-[a-f0-9]{12}-index\\.yaml$", cacheFileName);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, cacheFileName);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, cacheFileName);
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
