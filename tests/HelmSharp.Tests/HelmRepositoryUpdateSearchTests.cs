using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HelmSharp.Action;
using HelmSharp.Repo;

namespace HelmSharp.Tests;

public sealed class HelmRepositoryUpdateSearchTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "helmsharp-repository-update-search-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RepoUpdateAndSearchRepo_UsesNamedCachesOfflineAcrossConfiguredRepositories()
    {
        await using var server = LocalIndexServer.Start(new Dictionary<string, string>
        {
            ["/first/index.yaml"] = FirstIndex,
            ["/second/index.yaml"] = SecondIndex
        });
        var options = CreateOptions();
        using (var repository = CreateNoProxyRepository(options))
        {
            await repository.AddRepositoryAsync("first", server.BaseUrl + "first");
            await repository.AddRepositoryAsync("second", server.BaseUrl + "second");
        }

        var client = CreateClient(options);
        var update = await client.RepoUpdateAsync();

        Assert.True(update.Succeeded);
        Assert.Contains("Update complete. 2 updated, 0 failed.", update.StandardOutput, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(options.CacheDirectory!, "first-index.yaml")));
        Assert.True(File.Exists(Path.Combine(options.CacheDirectory!, "second-index.yaml")));

        await server.DisposeAsync();

        var search = await client.SearchRepoAsync("shared");
        var results = JsonSerializer.Deserialize<List<HelmChartSearchResult>>(
            search.StandardOutput,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(search.Succeeded);
        Assert.NotNull(results);
        Assert.Collection(results,
            result =>
            {
                Assert.Equal("first/shared", result.Name);
                Assert.Equal("1.10.0", result.Version);
            },
            result =>
            {
                Assert.Equal("second/shared", result.Name);
                Assert.Equal("2.0.0", result.Version);
            });

        var descriptionSearch = await client.SearchRepoAsync("database");
        var descriptionResults = JsonSerializer.Deserialize<List<HelmChartSearchResult>>(
            descriptionSearch.StandardOutput,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("first/shared", Assert.Single(descriptionResults!).Name);
    }

    [Fact]
    public async Task RepoUpdate_PreservesUsableCachesWhenAnotherRepositoryFails()
    {
        await using var server = LocalIndexServer.Start(new Dictionary<string, string>
        {
            ["/good/index.yaml"] = FirstIndex
        });
        var options = CreateOptions();
        using (var repository = CreateNoProxyRepository(options))
        {
            await repository.AddRepositoryAsync("bad", server.BaseUrl + "missing");
            await repository.AddRepositoryAsync("good", server.BaseUrl + "good");
        }
        await File.WriteAllTextAsync(Path.Combine(options.CacheDirectory!, "bad-index.yaml"), StaleIndex);
        var client = CreateClient(options);

        var update = await client.RepoUpdateAsync();
        var search = await client.SearchRepoAsync(string.Empty);
        var results = JsonSerializer.Deserialize<List<HelmChartSearchResult>>(
            search.StandardOutput,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("Failed to update bad:", update.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Successfully updated: good", update.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Update complete. 1 updated, 1 failed.", update.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(results!, result => result.Name == "bad/stale" && result.Version == "0.5.0");
        Assert.Contains(results!, result => result.Name == "good/shared" && result.Version == "1.10.0");
    }

    [Fact]
    public async Task SearchRepoAsync_WithDirectUrl_PreservesNetworkSearchAndMatchesDescription()
    {
        await using var server = LocalIndexServer.Start(new Dictionary<string, string>
        {
            ["/direct/index.yaml"] = FirstIndex
        });
        using var repository = CreateNoProxyRepository(CreateOptions());

        var results = await repository.SearchRepoAsync(server.BaseUrl + "direct", "database");

        var result = Assert.Single(results);
        Assert.Equal("shared", result.Name);
        Assert.Equal("1.10.0", result.Version);
    }

    private HelmClient CreateClient(HelmRepositoryOptions options)
        => new(
            new StaticHelmOptionsProvider(),
            (_, _, _, _) => throw new InvalidOperationException("Kubernetes is not used by repository commands."),
            () => CreateNoProxyRepository(options));

    private HelmRepositoryOptions CreateOptions()
    {
        var root = Path.Combine(_tempDir, Guid.NewGuid().ToString("N"));
        return new HelmRepositoryOptions
        {
            ConfigDirectory = Path.Combine(root, "config"),
            CacheDirectory = Path.Combine(root, "cache")
        };
    }

    private static HelmChartRepository CreateNoProxyRepository(HelmRepositoryOptions options)
        => new(options, CreateNoProxyHandler(), _ => CreateNoProxyHandler());

    private static HttpClientHandler CreateNoProxyHandler()
        => new()
        {
            AllowAutoRedirect = false,
            UseProxy = false
        };

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private const string FirstIndex = """
        apiVersion: v1
        entries:
          shared:
            - name: shared
              version: 1.9.0
              description: Database chart (old)
            - name: shared
              version: 1.10.0-beta.1
              description: Database chart (preview)
            - name: shared
              version: 1.10.0
              description: Database chart
        """;

    private const string SecondIndex = """
        apiVersion: v1
        entries:
          shared:
            - name: shared
              version: 2.0.0
              description: Shared queue chart
        """;

    private const string StaleIndex = """
        apiVersion: v1
        entries:
          stale:
            - name: stale
              version: 0.5.0
              description: Cached fallback
        """;

    private sealed class StaticHelmOptionsProvider : IHelmOptionsProvider
    {
        public ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new HelmExecutionOptions { DefaultNamespace = "default" });
    }

    private sealed class LocalIndexServer : IAsyncDisposable
    {
        private readonly IReadOnlyDictionary<string, string> _responses;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serveTask;
        private bool _disposed;

        private LocalIndexServer(IReadOnlyDictionary<string, string> responses, TcpListener listener)
        {
            _responses = responses;
            _listener = listener;
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            BaseUrl = $"http://127.0.0.1:{endpoint.Port}/";
            _serveTask = ServeAsync();
        }

        public string BaseUrl { get; }

        public static LocalIndexServer Start(IReadOnlyDictionary<string, string> responses)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new LocalIndexServer(responses, listener);
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                    await HandleAsync(client, _cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch (SocketException) when (_cancellation.IsCancellationRequested)
            {
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            await using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true))
            {
                var requestLine = await reader.ReadLineAsync(cancellationToken);
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellationToken)))
                {
                }

                var path = requestLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
                string? body = null;
                var found = path is not null && _responses.TryGetValue(path, out body);
                body ??= "not found";
                var payload = Encoding.UTF8.GetBytes(body);
                var headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\n" +
                    "Content-Type: application/x-yaml\r\n" +
                    $"Content-Length: {payload.Length}\r\n" +
                    "Connection: close\r\n\r\n");
                await stream.WriteAsync(headers, cancellationToken);
                await stream.WriteAsync(payload, cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            _cancellation.Cancel();
            _listener.Stop();
            await _serveTask;
            _cancellation.Dispose();
        }
    }
}
