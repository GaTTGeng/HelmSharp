using System.Text;
using System.Text.Json;

namespace HelmSharp.Registry;

/// <summary>Resolves credentials for a registry from explicit input, the environment, Helm config, or a provider.</summary>
public sealed class RegistryCredentialResolver
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly IRegistryCredentialProvider? _provider;

    /// <summary>Creates a resolver with an optional host-scoped provider as the last source.</summary>
    public RegistryCredentialResolver(IRegistryCredentialProvider? provider = null)
        : this(provider, Environment.GetEnvironmentVariable)
    {
    }

    internal RegistryCredentialResolver(IRegistryCredentialProvider? provider, Func<string, string?> getEnvironmentVariable)
    {
        _provider = provider;
        _getEnvironmentVariable = getEnvironmentVariable;
    }

    /// <summary>Returns the first credential for the registry, or null when no source has a match.</summary>
    /// <param name="registry">Registry hostname or IP address, optionally with a port.</param>
    /// <param name="explicitCredential">Credential explicitly supplied for this registry operation.</param>
    /// <param name="helmConfigPath">Optional Helm config path. Defaults to the value of HELM_REGISTRY_CONFIG when omitted.</param>
    /// <param name="cancellationToken">Cancellation token for config reads and provider lookups.</param>
    /// <exception cref="ArgumentException">The registry authority is invalid.</exception>
    /// <exception cref="InvalidDataException">The applicable credential source is invalid or uses unsupported settings.</exception>
    public async Task<RegistryCredential?> ResolveAsync(
        string registry,
        RegistryCredential? explicitCredential = null,
        string? helmConfigPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var authority = NormalizeAuthority(registry) ?? throw new ArgumentException("Invalid registry authority.", nameof(registry));
        if (explicitCredential is not null)
            return explicitCredential;

        var environmentHost = _getEnvironmentVariable("HELMSHARP_REGISTRY_HOST");
        if (environmentHost is not null)
        {
            var environmentAuthority = NormalizeAuthority(environmentHost)
                ?? throw new InvalidDataException("Invalid environment registry authority.");
            if (environmentAuthority == authority)
            {
                return ReadCredential(
                    _getEnvironmentVariable("HELMSHARP_REGISTRY_USERNAME"),
                    _getEnvironmentVariable("HELMSHARP_REGISTRY_PASSWORD"));
            }
        }

        var path = helmConfigPath ?? _getEnvironmentVariable("HELM_REGISTRY_CONFIG");
        if (!string.IsNullOrEmpty(path))
        {
            var configCredential = await ReadConfigAsync(path, authority, cancellationToken).ConfigureAwait(false);
            if (configCredential is not null)
                return configCredential;
        }

        return _provider is null ? null : await _provider.GetAsync(authority, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RegistryCredential?> ReadConfigAsync(
        string path, string authority, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var config = document.RootElement;

            // External helpers take precedence over inline auth in Helm's config format.
            if (config.TryGetProperty("credsStore", out var store) && !string.IsNullOrEmpty(store.GetString()) ||
                config.TryGetProperty("credHelpers", out var helpers) && FindRegistryEntry(helpers, authority) is not null)
            {
                throw new InvalidDataException("External registry credential helpers are unsupported.");
            }

            if (!config.TryGetProperty("auths", out var auths) || FindRegistryEntry(auths, authority) is not { } entry)
                return null;

            if (entry.TryGetProperty("identitytoken", out _) || entry.TryGetProperty("registrytoken", out _))
                throw new InvalidDataException("Registry config token fields are unsupported.");

            if (!entry.TryGetProperty("auth", out var auth))
            {
                if (entry.TryGetProperty("username", out _) || entry.TryGetProperty("password", out _))
                    throw new InvalidDataException("Registry config credentials require the auth field.");

                return null;
            }

            var decoded = StrictUtf8.GetString(Convert.FromBase64String(auth.GetString() ?? ""));
            var separator = decoded.IndexOf(':');
            if (separator < 0)
                throw new InvalidDataException("Invalid registry config auth entry.");

            return ReadCredential(decoded[..separator], decoded[(separator + 1)..]);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                   InvalidOperationException or FormatException or ArgumentException or NotSupportedException)
        {
            // File paths and parser exceptions can contain caller-supplied secret material.
            throw new InvalidDataException("Unable to read registry credential config.");
        }
    }

    private static JsonElement? FindRegistryEntry(JsonElement entries, string authority)
    {
        JsonElement? match = null;
        foreach (var entry in entries.EnumerateObject())
        {
            var key = entry.Name;
            if (key.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                key = key[8..];
                if (key.EndsWith('/'))
                    key = key[..^1];
            }

            if (NormalizeAuthority(key) != authority)
                continue;

            if (match is not null)
                throw new InvalidDataException("Registry config contains duplicate entries for the registry.");

            match = entry.Value;
        }

        return match;
    }

    private static RegistryCredential ReadCredential(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            throw new InvalidDataException("A registry credential requires both a username and a secret.");

        return new RegistryCredential(username, password);
    }

    private static string? NormalizeAuthority(string authority)
    {
        if (string.IsNullOrEmpty(authority) ||
            authority.IndexOfAny(['/', '\\', '@', '%', '?', '#']) >= 0 ||
            authority.Any(char.IsWhiteSpace) || authority.Any(char.IsControl) ||
            authority.EndsWith(':') ||
            !Uri.TryCreate($"https://{authority}/", UriKind.Absolute, out var uri) ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            return null;
        }

        // Retain explicit ports so transport defaults cannot broaden the credential scope.
        var hostEnd = authority.IndexOf(']') + 1;
        if (hostEnd > 0 && hostEnd < authority.Length && authority[hostEnd] != ':')
            return null;

        var host = uri.IdnHost.ToLowerInvariant();
        if (uri.HostNameType == UriHostNameType.IPv6)
            host = $"[{host}]";

        return authority.LastIndexOf(':') > authority.LastIndexOf(']') ? $"{host}:{uri.Port}" : host;
    }
}
