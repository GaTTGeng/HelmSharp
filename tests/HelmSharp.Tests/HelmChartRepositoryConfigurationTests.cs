using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    public async Task RemoveRepositoryAsync_RemovesCachedIndexForRepositoryName()
    {
        using var repository = new HelmChartRepository(CreateOptions());
        await repository.AddRepositoryAsync("stable", "https://stable.example.test");
        Directory.CreateDirectory(repository.CacheDirectory);
        var cachePath = Path.Combine(
            repository.CacheDirectory,
            HelmChartRepository.GetRepositoryIndexCacheFileName("stable"));
        await File.WriteAllTextAsync(cachePath, "apiVersion: v1\nentries: {}\n");

        await repository.RemoveRepositoryAsync("stable");

        Assert.False(File.Exists(cachePath));
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

    [Theory]
    [InlineData("repo with spaces")]
    [InlineData("@scope")]
    [InlineData(".private")]
    public async Task AddRepositoryAsync_AllowsHelmValidRepositoryNames(string name)
    {
        using var repository = new HelmChartRepository(CreateOptions());

        await repository.AddRepositoryAsync(name, "https://charts.example.test");

        var added = Assert.Single(await repository.ListRepositoriesAsync());
        Assert.Equal(name, added.Name);
    }

    [Fact]
    public async Task AddRepositoryAsync_IsIdempotentForMatchingConfiguration()
    {
        using var repository = new HelmChartRepository(CreateOptions());
        await repository.AddRepositoryAsync("stable", "https://charts.example.test", "alice", "secret");

        await repository.AddRepositoryAsync("stable", "https://charts.example.test/", "alice", "secret");

        Assert.Single(await repository.ListRepositoriesAsync());
    }

    [Fact]
    public async Task AddRepositoryAsync_NormalizesLoadedUrlBeforeIdempotentComparison()
    {
        var options = CreateOptions();
        Directory.CreateDirectory(options.ConfigDirectory!);
        await File.WriteAllTextAsync(Path.Combine(options.ConfigDirectory!, "repositories.yaml"), """
            apiVersion: v1
            repositories:
              - name: stable
                url: https://charts.example.test/
            """);
        using var repository = new HelmChartRepository(options);

        await repository.AddRepositoryAsync("stable", "https://charts.example.test");

        Assert.Single(await repository.ListRepositoriesAsync());
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

    [Fact]
    public async Task AddRepositoryAsync_WaitsForRepositoryConfigurationLock()
    {
        var options = CreateOptions();
        Directory.CreateDirectory(options.ConfigDirectory!);
        var lockPath = HelmChartRepository.GetRepositoryConfigLockPath(
            Path.Combine(options.ConfigDirectory!, "repositories.yaml"));
        await using var heldLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        using var repository = new HelmChartRepository(options);

        var addTask = repository.AddRepositoryAsync("stable", "https://charts.example.test");
        var completed = await Task.WhenAny(addTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(addTask, completed);

        await heldLock.DisposeAsync();
        await addTask.WaitAsync(TimeSpan.FromSeconds(5));

        var added = Assert.Single(await repository.ListRepositoriesAsync());
        Assert.Equal("stable", added.Name);
    }

    [Fact]
    public async Task AddRepositoryAsync_SerializesConcurrentConfigurationUpdates()
    {
        var options = CreateOptions();
        using var repository = new HelmChartRepository(options);
        var additions = Enumerable.Range(0, 12)
            .Select(index => repository.AddRepositoryAsync(
                $"repository-{index}",
                $"https://repository-{index}.example.test"));

        await Task.WhenAll(additions);

        Assert.Equal(12, (await repository.ListRepositoriesAsync()).Count);
    }

    [Fact]
    public async Task AddRepositoryAsync_RethrowsRepositoryLockPermissionFailure()
    {
        var options = CreateOptions();
        Directory.CreateDirectory(options.ConfigDirectory!);
        var lockPath = HelmChartRepository.GetRepositoryConfigLockPath(
            Path.Combine(options.ConfigDirectory!, "repositories.yaml"));
        Directory.CreateDirectory(lockPath);
        using var repository = new HelmChartRepository(options);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => repository.AddRepositoryAsync("stable", "https://charts.example.test"));
    }

    [Fact]
    public void GetRepositoryConfigLockPath_UsesHelmCompatibleSiblingLockFile()
    {
        var configPath = Path.Combine(_tempDir, "config", "custom-repositories.yaml");

        var lockPath = HelmChartRepository.GetRepositoryConfigLockPath(configPath);

        Assert.Equal(Path.Combine(_tempDir, "config", "custom-repositories.lock"), lockPath);
    }

    [Fact]
    public void CreateRepositoryIndexRequest_ScopesCredentialsToOriginalHostByDefault()
    {
        var repository = new HelmRepository
        {
            Name = "private",
            Url = "https://repo.example.test/charts",
            Username = "alice",
            Password = "secret"
        };
        using var sameHostRequest = HelmChartRepository.CreateRepositoryIndexRequest(
            new Uri("https://repo.example.test/charts/index.yaml"),
            repository,
            new Uri(repository.Url));
        using var otherHostRequest = HelmChartRepository.CreateRepositoryIndexRequest(
            new Uri("https://cdn.example.test/charts/index.yaml"),
            repository,
            new Uri(repository.Url));

        Assert.NotNull(sameHostRequest.Headers.Authorization);
        Assert.Null(otherHostRequest.Headers.Authorization);
    }

    [Fact]
    public void CreateRepositoryIndexRequest_PassesCredentialsToAllHostsWhenConfigured()
    {
        var repository = new HelmRepository
        {
            Name = "private",
            Url = "https://repo.example.test/charts",
            Username = "alice",
            Password = "secret",
            PassCredentialsAll = true
        };
        using var request = HelmChartRepository.CreateRepositoryIndexRequest(
            new Uri("https://cdn.example.test/charts/index.yaml"),
            repository,
            new Uri(repository.Url));

        Assert.NotNull(request.Headers.Authorization);
    }

    [Fact]
    public async Task SendRepositoryIndexRequestAsync_PreservesCredentialsAcrossSameHostRedirect()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("https://repo.example.test/redirected/index.yaml") }
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("apiVersion: v1\nentries: {}\n")
            });
        using var client = new HttpClient(handler);
        var repository = new HelmRepository
        {
            Name = "private",
            Url = "https://repo.example.test/charts",
            Username = "alice",
            Password = "secret"
        };

        using var response = await HelmChartRepository.SendRepositoryIndexRequestAsync(
            client,
            repository,
            CancellationToken.None);

        response.EnsureSuccessStatusCode();
        Assert.Equal(2, handler.Requests.Count);
        Assert.NotNull(handler.Requests[0].Headers.Authorization);
        Assert.NotNull(handler.Requests[1].Headers.Authorization);
    }

    [Fact]
    public async Task SendRepositoryIndexRequestAsync_DoesNotPassCredentialsAcrossCrossHostRedirectByDefault()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("https://cdn.example.test/redirected/index.yaml") }
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("apiVersion: v1\nentries: {}\n")
            });
        using var client = new HttpClient(handler);
        var repository = new HelmRepository
        {
            Name = "private",
            Url = "https://repo.example.test/charts",
            Username = "alice",
            Password = "secret"
        };

        using var response = await HelmChartRepository.SendRepositoryIndexRequestAsync(
            client,
            repository,
            CancellationToken.None);

        response.EnsureSuccessStatusCode();
        Assert.Equal(2, handler.Requests.Count);
        Assert.NotNull(handler.Requests[0].Headers.Authorization);
        Assert.Null(handler.Requests[1].Headers.Authorization);
    }

    [Fact]
    public void ValidateCustomCertificate_PreservesServerProvidedIntermediate()
    {
        var certificates = CreateCertificateChain();
        using var root = certificates.Root;
        using var intermediate = certificates.Intermediate;
        using var leaf = certificates.Leaf;
        using var serverChain = new X509Chain();
        serverChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        serverChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        serverChain.ChainPolicy.CustomTrustStore.Add(root);
        serverChain.ChainPolicy.ExtraStore.Add(intermediate);
        Assert.True(serverChain.Build(leaf));

        using var validationChain = new X509Chain();
        HelmChartRepository.AddServerChainCertificates(validationChain, serverChain);

        Assert.Contains(
            validationChain.ChainPolicy.ExtraStore.Cast<X509Certificate2>(),
            certificate => string.Equals(
                certificate.Thumbprint,
                intermediate.Thumbprint,
                StringComparison.OrdinalIgnoreCase));
        Assert.True(HelmChartRepository.ValidateCustomCertificate(
            leaf,
            serverChain,
            SslPolicyErrors.RemoteCertificateChainErrors,
            new X509Certificate2Collection(root)));
    }

    [Fact]
    public async Task UpdateConfiguredRepositoriesAsync_UsesPersistedTlsAndAuthenticationSettings()
    {
        var options = CreateOptions();
        Directory.CreateDirectory(options.ConfigDirectory!);
        await File.WriteAllTextAsync(Path.Combine(options.ConfigDirectory!, "repositories.yaml"), """
            apiVersion: v1
            repositories:
              - name: private
                url: https://repo.example.test/charts
                username: alice
                password: secret
                certFile: client.pem
                keyFile: client-key.pem
                caFile: root-ca.pem
                insecure_skip_tls_verify: false
                pass_credentials_all: true
            """);
        HelmRepository? configuredRepository = null;
        var configuredHandler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("apiVersion: v1\nentries: {}\n")
        });
        using var repository = new HelmChartRepository(
            options,
            new RecordingHandler(),
            configured =>
            {
                configuredRepository = configured;
                return configuredHandler;
            });

        var update = Assert.Single(await repository.UpdateConfiguredRepositoriesAsync());

        Assert.True(update.Succeeded, update.Error);
        Assert.NotNull(configuredRepository);
        Assert.Equal("alice", configuredRepository.Username);
        Assert.Equal("secret", configuredRepository.Password);
        Assert.Equal("client.pem", configuredRepository.CertFile);
        Assert.Equal("client-key.pem", configuredRepository.KeyFile);
        Assert.Equal("root-ca.pem", configuredRepository.CaFile);
        Assert.False(configuredRepository.InsecureSkipTlsVerify);
        Assert.True(configuredRepository.PassCredentialsAll);
        Assert.NotNull(Assert.Single(configuredHandler.Requests).Headers.Authorization);
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
    public void ResolveHelmConfigDirectory_UsesMacOSPreferencesFallback()
    {
        var home = Path.Combine(_tempDir, "home");
        var applicationData = Path.Combine(_tempDir, "application-data");

        var configDirectory = HelmChartRepository.ResolveHelmConfigDirectory(
            helmConfigHome: null,
            xdgConfigHome: null,
            isMacOS: true,
            userProfile: home,
            applicationData: applicationData);

        Assert.Equal(Path.Combine(home, "Library", "Preferences", "helm"), configDirectory);
    }

    [Fact]
    public void ResolveHelmConfigDirectory_HelmConfigHomeOverridesMacOSFallback()
    {
        var helmConfigHome = Path.Combine(_tempDir, "helm-config");

        var configDirectory = HelmChartRepository.ResolveHelmConfigDirectory(
            helmConfigHome,
            xdgConfigHome: Path.Combine(_tempDir, "xdg-config"),
            isMacOS: true,
            userProfile: Path.Combine(_tempDir, "home"),
            applicationData: Path.Combine(_tempDir, "application-data"));

        Assert.Equal(helmConfigHome, configDirectory);
    }

    [Fact]
    public void ResolveHelmConfigDirectory_AppendsHelmToXdgConfigHome()
    {
        var xdgConfigHome = Path.Combine(_tempDir, "xdg-config");

        var configDirectory = HelmChartRepository.ResolveHelmConfigDirectory(
            helmConfigHome: null,
            xdgConfigHome: xdgConfigHome,
            isMacOS: true,
            userProfile: Path.Combine(_tempDir, "home"),
            applicationData: Path.Combine(_tempDir, "application-data"));

        Assert.Equal(Path.Combine(xdgConfigHome, "helm"), configDirectory);
    }

    [Fact]
    public void GetRepositoryIndexCacheFileName_IsDeterministicAndSafe()
    {
        var cacheFileName = HelmChartRepository.GetRepositoryIndexCacheFileName("../../unsafe");

        Assert.Equal(cacheFileName, HelmChartRepository.GetRepositoryIndexCacheFileName("../../unsafe"));
        Assert.Matches("^\\.\\.-\\.\\.-unsafe-[a-f0-9]{12}-index\\.yaml$", cacheFileName);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, cacheFileName);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, cacheFileName);
    }

    [Fact]
    public void GetRepositoryIndexCacheFileName_DisambiguatesSanitizedPortableNames()
    {
        var colon = HelmChartRepository.GetRepositoryIndexCacheFileName(
            "private:charts",
            caseInsensitiveFileSystem: false);
        var questionMark = HelmChartRepository.GetRepositoryIndexCacheFileName(
            "private?charts",
            caseInsensitiveFileSystem: false);

        Assert.DoesNotContain(':', colon);
        Assert.DoesNotContain('?', questionMark);
        Assert.NotEqual(colon, questionMark);
    }

    [Theory]
    [InlineData("repo with spaces", "repo with spaces-index.yaml")]
    [InlineData("@scope", "@scope-index.yaml")]
    [InlineData(".private", ".private-index.yaml")]
    public void GetRepositoryIndexCacheFileName_PreservesHelmValidNames(string repositoryName, string expected)
    {
        Assert.Equal(expected, HelmChartRepository.GetRepositoryIndexCacheFileName(repositoryName));
    }

    [Fact]
    public async Task WriteAllTextAtomicallyAsync_PreservesExistingFileWhenCanceled()
    {
        var path = Path.Combine(_tempDir, "cache", "stable-index.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "old-index");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HelmChartRepository.WriteAllTextAtomicallyAsync(path, "new-index", cts.Token));

        Assert.Equal("old-index", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteAllTextAtomicallyAsync_ReplacesExistingFile()
    {
        var path = Path.Combine(_tempDir, "cache", "stable-index.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "old-index");

        await HelmChartRepository.WriteAllTextAtomicallyAsync(path, "new-index", CancellationToken.None);

        Assert.Equal("new-index", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task FetchRepoIndexAsync_InvalidUpdatePreservesLastKnownGoodCache()
    {
        var options = CreateOptions();
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("entries: {}\n")
        });
        using var repository = new HelmChartRepository(options, handler);
        var cachePath = Path.Combine(
            options.CacheDirectory!,
            HelmChartRepository.GetRepositoryIndexCacheFileName("stable"));
        Directory.CreateDirectory(options.CacheDirectory!);
        const string lastKnownGood = "apiVersion: v1\nentries: {}\n";
        await File.WriteAllTextAsync(cachePath, lastKnownGood);

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.FetchRepoIndexAsync(
            "https://repo.example.test",
            username: null,
            password: null,
            repositoryName: "stable"));

        Assert.Equal(lastKnownGood, await File.ReadAllTextAsync(cachePath));
    }

    [Fact]
    public async Task FetchRepoIndexAsync_CachesEveryConfiguredIdentityForDuplicateUrls()
    {
        var options = CreateOptions();
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("apiVersion: v1\nentries: {}\n")
        });
        using var repository = new HelmChartRepository(options, handler);
        await repository.AddRepositoryAsync("first", "https://repo.example.test");
        await repository.AddRepositoryAsync("second", "https://repo.example.test");

        await repository.FetchRepoIndexAsync("https://repo.example.test");

        Assert.True(File.Exists(Path.Combine(
            options.CacheDirectory!,
            HelmChartRepository.GetRepositoryIndexCacheFileName("first"))));
        Assert.True(File.Exists(Path.Combine(
            options.CacheDirectory!,
            HelmChartRepository.GetRepositoryIndexCacheFileName("second"))));
    }

    [Fact]
    public async Task FetchRepoIndexAsync_NormalizesConfiguredUrlBeforeCacheIdentityLookup()
    {
        var options = CreateOptions();
        Directory.CreateDirectory(options.ConfigDirectory!);
        await File.WriteAllTextAsync(Path.Combine(options.ConfigDirectory!, "repositories.yaml"), """
            apiVersion: v1
            repositories:
              - name: stable
                url: https://repo.example.test/charts/
            """);
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("apiVersion: v1\nentries: {}\n")
        });
        using var repository = new HelmChartRepository(options, handler);

        await repository.FetchRepoIndexAsync("https://repo.example.test/charts");

        Assert.True(File.Exists(Path.Combine(
            options.CacheDirectory!,
            HelmChartRepository.GetRepositoryIndexCacheFileName("stable"))));
        Assert.False(File.Exists(Path.Combine(
            options.CacheDirectory!,
            HelmChartRepository.GetRepositoryIndexCacheFileName("repository"))));
    }

    [Fact]
    public async Task FetchRepoIndexAsync_PreservesCaseDistinctCacheIdentities()
    {
        var options = CreateOptions();
        const string uppercaseIndex = "apiVersion: v1\nentries:\n  uppercase: []\n";
        const string lowercaseIndex = "apiVersion: v1\nentries:\n  lowercase: []\n";
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(uppercaseIndex) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(lowercaseIndex) });
        using var repository = new HelmChartRepository(options, handler);
        await repository.AddRepositoryAsync("Stable", "https://uppercase.example.test");
        await repository.AddRepositoryAsync("stable", "https://lowercase.example.test");

        await repository.FetchRepoIndexAsync(
            "https://uppercase.example.test",
            username: null,
            password: null,
            repositoryName: "Stable");
        await repository.FetchRepoIndexAsync(
            "https://lowercase.example.test",
            username: null,
            password: null,
            repositoryName: "stable");

        var uppercasePath = Path.Combine(
            options.CacheDirectory!,
            HelmChartRepository.GetRepositoryIndexCacheFileName("Stable"));
        var lowercasePath = Path.Combine(
            options.CacheDirectory!,
            HelmChartRepository.GetRepositoryIndexCacheFileName("stable"));
        Assert.NotEqual(uppercasePath, lowercasePath);
        Assert.Equal(uppercaseIndex, await File.ReadAllTextAsync(uppercasePath));
        Assert.Equal(lowercaseIndex, await File.ReadAllTextAsync(lowercasePath));
    }

    [Fact]
    public async Task AddRepositoryAsync_PreservesOwnerOnlyUnixConfigurationPermissions()
    {
        if (OperatingSystem.IsWindows())
            return;

        var options = CreateOptions();
        Directory.CreateDirectory(options.ConfigDirectory!);
        var configPath = Path.Combine(options.ConfigDirectory!, "repositories.yaml");
        await File.WriteAllTextAsync(configPath, "apiVersion: v1\nrepositories: []\n");
        var ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(configPath, ownerOnly);
        using var repository = new HelmChartRepository(options);

        await repository.AddRepositoryAsync("stable", "https://charts.example.test");

        Assert.Equal(ownerOnly, File.GetUnixFileMode(configPath));
    }

    [Fact]
    public async Task AddRepositoryAsync_CreatesOwnerOnlyUnixConfiguration()
    {
        if (OperatingSystem.IsWindows())
            return;

        var options = CreateOptions();
        using var repository = new HelmChartRepository(options);

        await repository.AddRepositoryAsync("stable", "https://charts.example.test");

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(repository.RepositoryConfigPath));
    }

    [Fact]
    public void GetRepositoryIndexCacheFileName_UsesEachRepositoryName()
    {
        Assert.Equal("first-index.yaml", HelmChartRepository.GetRepositoryIndexCacheFileName("first"));
        Assert.Equal("second-index.yaml", HelmChartRepository.GetRepositoryIndexCacheFileName("second"));
    }

    [Fact]
    public void GetRepositoryIndexCacheFileName_DisambiguatesCaseSensitiveIdentitiesOnInsensitiveFileSystems()
    {
        var uppercase = HelmChartRepository.GetRepositoryIndexCacheFileName(
            "Stable",
            caseInsensitiveFileSystem: true);
        var lowercase = HelmChartRepository.GetRepositoryIndexCacheFileName(
            "stable",
            caseInsensitiveFileSystem: true);

        Assert.False(string.Equals(uppercase, lowercase, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            uppercase,
            HelmChartRepository.GetRepositoryIndexCacheFileName(
                "Stable",
                caseInsensitiveFileSystem: true));
        Assert.Equal(
            "Stable-index.yaml",
            HelmChartRepository.GetRepositoryIndexCacheFileName(
                "Stable",
                caseInsensitiveFileSystem: false));
    }

    [Fact]
    public void GetRepositoryCacheDirectory_AppendsRepositoryDirectoryToHelmCacheHome()
    {
        var cacheHome = Path.Combine(_tempDir, "helm-cache");

        Assert.Equal(Path.Combine(cacheHome, "repository"), HelmChartRepository.GetRepositoryCacheDirectory(cacheHome));
    }

    [Fact]
    public void ResolveCacheDirectory_OptionsOverrideRepositoryAndHomeEnvironmentPaths()
    {
        var optionsCache = Path.Combine(_tempDir, "options-cache");

        var cacheDirectory = HelmChartRepository.ResolveCacheDirectory(
            new HelmRepositoryOptions { CacheDirectory = optionsCache },
            environmentRepositoryCache: Path.Combine(_tempDir, "repository-cache"),
            helmCacheHome: Path.Combine(_tempDir, "helm-cache"),
            xdgCacheHome: Path.Combine(_tempDir, "xdg-cache"),
            isMacOS: false,
            isWindows: true,
            userProfile: Path.Combine(_tempDir, "home"),
            temporaryDirectory: Path.Combine(_tempDir, "temp"));

        Assert.Equal(optionsCache, cacheDirectory);
    }

    [Fact]
    public void ResolveCacheDirectory_RepositoryCacheEnvironmentOverridesCacheHome()
    {
        var repositoryCache = Path.Combine(_tempDir, "repository-cache");

        var cacheDirectory = HelmChartRepository.ResolveCacheDirectory(
            new HelmRepositoryOptions(),
            environmentRepositoryCache: repositoryCache,
            helmCacheHome: Path.Combine(_tempDir, "helm-cache"),
            xdgCacheHome: Path.Combine(_tempDir, "xdg-cache"),
            isMacOS: false,
            isWindows: true,
            userProfile: Path.Combine(_tempDir, "home"),
            temporaryDirectory: Path.Combine(_tempDir, "temp"));

        Assert.Equal(repositoryCache, cacheDirectory);
    }

    [Theory]
    [InlineData(true, false, "home/Library/Caches/helm/repository")]
    [InlineData(false, true, "temp/helm/repository")]
    [InlineData(false, false, "home/.cache/helm/repository")]
    public void ResolveHelmCacheDirectory_UsesPlatformFallbacks(
        bool isMacOS,
        bool isWindows,
        string expectedRelativePath)
    {
        var userProfile = Path.Combine(_tempDir, "home");
        var temporaryDirectory = Path.Combine(_tempDir, "temp");

        var cacheDirectory = HelmChartRepository.ResolveHelmCacheDirectory(
            helmCacheHome: null,
            xdgCacheHome: null,
            isMacOS,
            isWindows,
            userProfile,
            temporaryDirectory);

        Assert.Equal(
            Path.Combine(_tempDir, expectedRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            cacheDirectory);
    }

    [Fact]
    public void ResolveHelmCacheDirectory_HelmCacheHomeOverridesXdgAndPlatformFallbacks()
    {
        var helmCacheHome = Path.Combine(_tempDir, "helm-cache");

        var cacheDirectory = HelmChartRepository.ResolveHelmCacheDirectory(
            helmCacheHome,
            xdgCacheHome: Path.Combine(_tempDir, "xdg-cache"),
            isMacOS: true,
            isWindows: false,
            userProfile: Path.Combine(_tempDir, "home"),
            temporaryDirectory: Path.Combine(_tempDir, "temp"));

        Assert.Equal(Path.Combine(helmCacheHome, "repository"), cacheDirectory);
    }

    [Fact]
    public void ResolveHelmCacheDirectory_AppendsHelmToXdgCacheHome()
    {
        var xdgCacheHome = Path.Combine(_tempDir, "xdg-cache");

        var cacheDirectory = HelmChartRepository.ResolveHelmCacheDirectory(
            helmCacheHome: null,
            xdgCacheHome,
            isMacOS: false,
            isWindows: false,
            userProfile: Path.Combine(_tempDir, "home"),
            temporaryDirectory: Path.Combine(_tempDir, "temp"));

        Assert.Equal(Path.Combine(xdgCacheHome, "helm", "repository"), cacheDirectory);
    }

    private HelmRepositoryOptions CreateOptions()
        => new()
        {
            ConfigDirectory = Path.Combine(_tempDir, "config"),
            CacheDirectory = Path.Combine(_tempDir, "cache")
        };

    private static (X509Certificate2 Root, X509Certificate2 Intermediate, X509Certificate2 Leaf)
        CreateCertificateChain()
    {
        var now = DateTimeOffset.UtcNow;
        var identity = Guid.NewGuid().ToString("N");
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest(
            $"CN=HelmSharp Review Root {identity}",
            rootKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 1, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        var root = rootRequest.CreateSelfSigned(now.AddDays(-7), now.AddDays(7));

        using var intermediateKey = RSA.Create(2048);
        var intermediateRequest = new CertificateRequest(
            $"CN=HelmSharp Review Intermediate {identity}",
            intermediateKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        using var intermediatePublic = intermediateRequest.Create(
            root,
            now.AddDays(-5),
            now.AddDays(3),
            RandomNumberGenerator.GetBytes(16));
        var intermediate = intermediatePublic.CopyWithPrivateKey(intermediateKey);

        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest(
            $"CN=repo-{identity}.example.test",
            leafKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature,
            true));
        using var leafPublic = leafRequest.Create(
            intermediate,
            now.AddDays(-3),
            now.AddDays(1),
            RandomNumberGenerator.GetBytes(16));
        var leaf = leafPublic.CopyWithPrivateKey(leafKey);
        return (root, intermediate, leaf);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            Requests.Add(clone);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
