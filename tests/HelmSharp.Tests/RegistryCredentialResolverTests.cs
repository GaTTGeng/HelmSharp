using System.Text;
using System.Text.Json;
using HelmSharp.Registry;

namespace HelmSharp.Tests;

/// <summary>Exercises source precedence and registry isolation without real credentials.</summary>
public sealed class RegistryCredentialResolverTests : IDisposable
{
    private readonly string _configPath = Path.GetTempFileName();

    [Fact]
    public async Task ResolveAsync_SelectsSourcesInOrderAndKeepsRegistryScopesSeparate()
    {
        WriteConfig(new
        {
            auths = new Dictionary<string, object>
            {
                ["https://REGISTRY.example:443/"] = new { auth = Auth("config:first:secret") },
                ["other.example"] = new { auth = Auth("second:secret") }
            }
        });
        var environment = new Dictionary<string, string>
        {
            ["HELMSHARP_REGISTRY_HOST"] = "REGISTRY.EXAMPLE:443",
            ["HELMSHARP_REGISTRY_USERNAME"] = "environment",
            ["HELMSHARP_REGISTRY_PASSWORD"] = "environment-secret",
            ["HELM_REGISTRY_CONFIG"] = _configPath
        };
        var provider = new LocalProvider("registry.example", "registry.example:443");
        var resolver = new RegistryCredentialResolver(provider, environment.GetValueOrDefault);
        var explicitCredential = new RegistryCredential("explicit", "explicit-secret");

        Assert.Same(explicitCredential, await resolver.ResolveAsync("registry.example:443", explicitCredential));
        Assert.Equal("environment", (await resolver.ResolveAsync("registry.example:443"))?.Username);

        var otherCredential = await resolver.ResolveAsync("OTHER.example");
        Assert.Equal("second", otherCredential?.Username);
        Assert.Equal("secret", otherCredential?.Password);

        environment.Remove("HELMSHARP_REGISTRY_HOST");
        var configCredential = await resolver.ResolveAsync("registry.example:443");
        Assert.Equal("config", configCredential?.Username);
        Assert.Equal("first:secret", configCredential?.Password);

        Assert.Equal("provider", (await resolver.ResolveAsync("REGISTRY.example"))?.Username);
        Assert.Null(await resolver.ResolveAsync("registry.example:5000"));
    }

    [Fact]
    public async Task ResolveAsync_ExplicitConfigPathOverridesEnvironmentPath()
    {
        WriteConfig(new { auths = new Dictionary<string, object> { ["registry"] = new { auth = Auth("config:secret") } } });
        var resolver = new RegistryCredentialResolver(null, name => name == "HELM_REGISTRY_CONFIG" ? _configPath : null);

        Assert.Null(await resolver.ResolveAsync("registry", helmConfigPath: _configPath + ".missing"));
        Assert.NotNull(await resolver.ResolveAsync("registry"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveAsync_RejectsApplicableHelpersWithoutExposingConfigValues(bool globalHelper)
    {
        var helpers = new Dictionary<string, string>();
        if (!globalHelper)
            helpers["REGISTRY.example"] = "sentinel-secret";
        WriteConfig(new
        {
            credsStore = globalHelper ? "sentinel-secret" : "",
            credHelpers = helpers,
            auths = new Dictionary<string, object> { ["registry.example"] = new { auth = Auth("config:secret") } }
        });
        var resolver = new RegistryCredentialResolver(new LocalProvider("other.example"), _ => null);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => resolver.ResolveAsync("registry.example", helmConfigPath: _configPath));
        Assert.Equal("External registry credential helpers are unsupported.", error.Message);
        Assert.DoesNotContain("sentinel-secret", error.ToString());

        if (!globalHelper)
            Assert.Equal("provider", (await resolver.ResolveAsync("other.example", helmConfigPath: _configPath))?.Username);
    }

    [Fact]
    public async Task ResolveAsync_RedactsInvalidAuthAndDoesNotFallBack()
    {
        WriteConfig(new { auths = new Dictionary<string, object> { ["registry"] = new { auth = "sentinel-secret" } } });
        var resolver = new RegistryCredentialResolver(new LocalProvider("registry"), _ => null);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => resolver.ResolveAsync("registry", helmConfigPath: _configPath));
        Assert.DoesNotContain("sentinel-secret", error.ToString());
        Assert.DoesNotContain(_configPath, error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task ResolveAsync_RejectsDuplicateNormalizedConfigEntries()
    {
        WriteConfig(new
        {
            auths = new Dictionary<string, object>
            {
                ["registry.example"] = new { auth = Auth("first:secret") },
                ["https://REGISTRY.example/"] = new { auth = Auth("second:secret") }
            }
        });
        var resolver = new RegistryCredentialResolver(null, _ => null);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => resolver.ResolveAsync("registry.example", helmConfigPath: _configPath));
    }

    [Theory]
    [InlineData("user:sentinel-secret@registry.example")]
    [InlineData("registry.example/repository")]
    [InlineData("registry.example:")]
    [InlineData("https://registry.example")]
    [InlineData("[::1]suffix:5000")]
    public async Task ResolveAsync_RejectsNonAuthorityInputWithoutEchoingIt(string registry)
    {
        var resolver = new RegistryCredentialResolver(null, _ => null);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => resolver.ResolveAsync(registry));
        Assert.DoesNotContain(registry, error.ToString());
    }

    [Fact]
    public async Task ResolveAsync_NormalizesIpv6AndKeepsThePort()
    {
        var resolver = new RegistryCredentialResolver(new LocalProvider("[::1]:5000"), _ => null);

        Assert.NotNull(await resolver.ResolveAsync("[0:0:0:0:0:0:0:1]:5000"));
        Assert.Null(await resolver.ResolveAsync("[::1]"));
    }

    [Fact]
    public async Task ResolveAsync_FailsClosedForAnIncompleteEnvironmentCredential()
    {
        var environment = new Dictionary<string, string>
        {
            ["HELMSHARP_REGISTRY_HOST"] = "registry.example",
            ["HELMSHARP_REGISTRY_USERNAME"] = "sentinel-secret"
        };
        var resolver = new RegistryCredentialResolver(new LocalProvider("registry.example"), environment.GetValueOrDefault);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => resolver.ResolveAsync("registry.example"));
        Assert.DoesNotContain("sentinel-secret", error.ToString());
        Assert.DoesNotContain("credential-secret", new RegistryCredential("user", "credential-secret").ToString());
    }

    [Fact]
    public async Task ResolveAsync_HonorsCancellationBeforeSelectingExplicitCredentials()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var resolver = new RegistryCredentialResolver(null, _ => null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.ResolveAsync("registry.example", new RegistryCredential("user", "secret"),
                cancellationToken: cancellation.Token));
    }

    public void Dispose() => File.Delete(_configPath);

    private void WriteConfig(object config) => File.WriteAllText(_configPath, JsonSerializer.Serialize(config));

    private static string Auth(string credential) => Convert.ToBase64String(Encoding.UTF8.GetBytes(credential));

    private sealed class LocalProvider(params string[] authorities) : IRegistryCredentialProvider
    {
        public Task<RegistryCredential?> GetAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<RegistryCredential?>(authorities.Contains(host) ? new RegistryCredential("provider", "provider-secret") : null);
    }
}
