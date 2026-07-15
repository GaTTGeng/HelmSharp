using HelmSharp.Action;
using HelmSharp.Repo;

namespace HelmSharp.Tests;

public sealed class OperationRequestTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"helmsharp-operation-requests-{Guid.NewGuid():N}");

    public OperationRequestTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void Requests_HaveDocumentedDefaults()
    {
        var package = new HelmPackageRequest();
        Assert.Equal(string.Empty, package.ChartPath);
        Assert.Null(package.Destination);
        Assert.Null(package.Version);
        Assert.Null(package.AppVersion);
        Assert.False(package.DependencyUpdate);
        Assert.False(package.SkipSchemaValidation);

        var pull = new HelmPullRequest();
        Assert.Equal(string.Empty, pull.ChartReference);
        Assert.Null(pull.Version);
        Assert.Null(pull.Destination);
        Assert.False(pull.Untar);
        Assert.Null(pull.UntarDirectory);
        Assert.Null(pull.RepositoryUrl);
        Assert.Null(pull.Username);
        Assert.Null(pull.Password);
        Assert.True(pull.VerifyDigest);

        var repoIndex = new HelmRepoIndexRequest();
        Assert.Equal(string.Empty, repoIndex.DirectoryPath);
        Assert.Null(repoIndex.Url);
        Assert.Null(repoIndex.MergeIndexPath);
        Assert.Null(repoIndex.OutputPath);
        Assert.False(repoIndex.FailOnInvalidPackage);

        var update = new HelmDependencyUpdateRequest();
        Assert.Equal(string.Empty, update.ChartPath);
        Assert.Null(update.RepositoryConfigPath);
        Assert.Null(update.RepositoryCachePath);
        Assert.False(update.SkipRepositoryRefresh);

        var build = new HelmDependencyBuildRequest();
        Assert.Equal(string.Empty, build.ChartPath);
        Assert.Null(build.RepositoryConfigPath);
        Assert.Null(build.RepositoryCachePath);
        Assert.True(build.VerifyDigests);

        var list = new HelmDependencyListRequest();
        Assert.Equal(string.Empty, list.ChartPath);
        Assert.True(list.IncludeDiagnostics);
    }

    [Fact]
    public void Requests_PreserveNonDefaultValues()
    {
        var package = new HelmPackageRequest
        {
            ChartPath = "chart",
            Destination = "packages",
            Version = "2.0.0",
            AppVersion = "3.0",
            DependencyUpdate = true,
            SkipSchemaValidation = true
        };
        Assert.Equal("packages", package.Destination);
        Assert.True(package.DependencyUpdate);
        Assert.True(package.SkipSchemaValidation);

        var pull = new HelmPullRequest
        {
            ChartReference = "app",
            Version = "~1.2.0",
            Destination = "downloads",
            Untar = true,
            UntarDirectory = "charts",
            RepositoryUrl = "https://charts.example.test",
            Username = "user",
            Password = "secret",
            VerifyDigest = false
        };
        Assert.Equal("https://charts.example.test", pull.RepositoryUrl);
        Assert.True(pull.Untar);
        Assert.False(pull.VerifyDigest);

        var repoIndex = new HelmRepoIndexRequest
        {
            DirectoryPath = "packages",
            Url = "https://charts.example.test",
            MergeIndexPath = "old-index.yaml",
            OutputPath = "site/index.yaml",
            FailOnInvalidPackage = true
        };
        Assert.Equal("site/index.yaml", repoIndex.OutputPath);
        Assert.True(repoIndex.FailOnInvalidPackage);

        var update = new HelmDependencyUpdateRequest
        {
            ChartPath = "chart",
            RepositoryConfigPath = "repositories.yaml",
            RepositoryCachePath = "cache",
            SkipRepositoryRefresh = true
        };
        Assert.True(update.SkipRepositoryRefresh);

        var build = new HelmDependencyBuildRequest
        {
            ChartPath = "chart",
            RepositoryConfigPath = "repositories.yaml",
            RepositoryCachePath = "cache",
            VerifyDigests = false
        };
        Assert.False(build.VerifyDigests);

        var list = new HelmDependencyListRequest
        {
            ChartPath = "chart",
            IncludeDiagnostics = false
        };
        Assert.False(list.IncludeDiagnostics);
    }

    [Fact]
    public void IHelmClient_PreservesConvenienceOverloadsAlongsideRequests()
    {
        AssertOverloads(
            nameof(IHelmClient.PullAsync),
            [typeof(string), typeof(string), typeof(string), typeof(CancellationToken)],
            [typeof(HelmPullRequest), typeof(CancellationToken)]);
        AssertOverloads(
            nameof(IHelmClient.PackageAsync),
            [typeof(string), typeof(string), typeof(string), typeof(string), typeof(CancellationToken)],
            [typeof(HelmPackageRequest), typeof(CancellationToken)]);
        AssertOverloads(
            nameof(IHelmClient.RepoIndexAsync),
            [typeof(string), typeof(string), typeof(CancellationToken)],
            [typeof(HelmRepoIndexRequest), typeof(CancellationToken)]);
        Assert.NotNull(typeof(HelmClient).GetMethod(
            nameof(HelmClient.RepoIndexAsync),
            [typeof(string), typeof(string), typeof(CancellationToken), typeof(string)]));
        AssertOverloads(
            nameof(IHelmClient.DependencyUpdateAsync),
            [typeof(string), typeof(CancellationToken)],
            [typeof(HelmDependencyUpdateRequest), typeof(CancellationToken)]);
        AssertOverloads(
            nameof(IHelmClient.DependencyBuildAsync),
            [typeof(string), typeof(CancellationToken)],
            [typeof(HelmDependencyBuildRequest), typeof(CancellationToken)]);
        AssertOverloads(
            nameof(IHelmClient.DependencyListAsync),
            [typeof(string), typeof(CancellationToken)],
            [typeof(HelmDependencyListRequest), typeof(CancellationToken)]);
    }

    [Fact]
    public async Task PackageAsync_RequestRoutesNonDefaultMetadataAndDestination()
    {
        var destination = Path.Combine(_tempDirectory, "packages");
        var chartPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Charts", "minimal"));
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var result = await client.PackageAsync(new HelmPackageRequest
        {
            ChartPath = chartPath,
            Destination = destination,
            Version = "2.0.0",
            AppVersion = "3.0"
        });

        Assert.True(result.Succeeded, result.StandardError);
        Assert.True(File.Exists(Path.Combine(destination, "minimal-2.0.0.tgz")));
    }

    [Fact]
    public async Task PullAsync_RequestRoutesThroughLowerLevelApi()
    {
        var chartPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Charts", "minimal"));
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var result = await client.PullAsync(new HelmPullRequest
        {
            ChartReference = chartPath,
            Version = "1.2.3",
            VerifyDigest = false
        });

        Assert.True(result.Succeeded);
        Assert.Contains(chartPath, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepoIndexAsync_RequestHonorsCustomOutputPath()
    {
        var packages = Path.Combine(_tempDirectory, "repository");
        var output = Path.Combine(_tempDirectory, "published", "charts.yaml");
        Directory.CreateDirectory(packages);
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var result = await client.RepoIndexAsync(new HelmRepoIndexRequest
        {
            DirectoryPath = packages,
            Url = "https://charts.example.test",
            OutputPath = output
        });

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(Path.Combine(packages, "index.yaml")));
    }

    [Fact]
    public async Task DependencyRequests_RouteThroughConvenienceFacade()
    {
        var chartPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Charts", "minimal"));
        var client = new HelmClient(new StaticHelmOptionsProvider());

        var update = await client.DependencyUpdateAsync(new HelmDependencyUpdateRequest
        {
            ChartPath = chartPath,
            RepositoryCachePath = Path.Combine(_tempDirectory, "cache"),
            SkipRepositoryRefresh = true
        });
        var build = await client.DependencyBuildAsync(new HelmDependencyBuildRequest
        {
            ChartPath = chartPath,
            RepositoryCachePath = Path.Combine(_tempDirectory, "cache"),
            VerifyDigests = false
        });
        var list = await client.DependencyListAsync(new HelmDependencyListRequest
        {
            ChartPath = chartPath,
            IncludeDiagnostics = false
        });

        Assert.True(update.Succeeded);
        Assert.True(build.Succeeded);
        Assert.True(list.Succeeded);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temporary test output.
        }
    }

    private sealed class StaticHelmOptionsProvider : IHelmOptionsProvider
    {
        public ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new HelmExecutionOptions { DefaultNamespace = "default" });
    }

    private static void AssertOverloads(
        string methodName,
        Type[] convenienceParameters,
        Type[] requestParameters)
    {
        Assert.NotNull(typeof(IHelmClient).GetMethod(methodName, convenienceParameters));
        Assert.NotNull(typeof(IHelmClient).GetMethod(methodName, requestParameters));
        Assert.NotNull(typeof(HelmClient).GetMethod(methodName, convenienceParameters));
        Assert.NotNull(typeof(HelmClient).GetMethod(methodName, requestParameters));
    }
}
