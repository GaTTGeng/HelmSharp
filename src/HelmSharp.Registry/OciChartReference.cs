using System.Text.RegularExpressions;

namespace HelmSharp.Registry;

/// <summary>Parses the supported OCI chart reference syntax without performing I/O.</summary>
internal sealed class OciChartReference
{
    private static readonly Regex RepositoryPattern = new(
        @"\A[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex TagPattern = new(@"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z", RegexOptions.CultureInvariant);
    private static readonly Regex DigestPattern = new(@"\A(?:sha256:[a-f0-9]{64}|sha512:[a-f0-9]{128})\z", RegexOptions.CultureInvariant);

    private OciChartReference(string registry, string repository, string? tag, string? digest)
    {
        Registry = registry;
        Repository = repository;
        Tag = tag;
        Digest = digest;
    }

    public string Registry { get; }
    public string Repository { get; }
    public string Name => Repository[(Repository.LastIndexOf('/') + 1)..];
    public string? Tag { get; }
    public string? Digest { get; }

    public static OciChartReference Parse(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        const string scheme = "oci://";
        if (!reference.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            throw InvalidReference();

        var value = reference[scheme.Length..];
        var slash = value.IndexOf('/');
        if (slash <= 0 || slash == value.Length - 1)
            throw InvalidReference();

        var registry = value[..slash];
        var path = value[(slash + 1)..];
        if (!Uri.TryCreate($"https://{registry}/", UriKind.Absolute, out var registryUri) ||
            registryUri.HostNameType == UriHostNameType.Unknown || registryUri.UserInfo.Length != 0 ||
            registryUri.AbsolutePath != "/" || registryUri.Query.Length != 0 || registryUri.Fragment.Length != 0 ||
            registry.IndexOfAny(['%', '\\', ' ', '\t', '\r', '\n']) >= 0 ||
            registry.EndsWith(':'))
            throw InvalidReference();

        string? digest = null;
        var at = path.IndexOf('@');
        if (at >= 0)
        {
            digest = path[(at + 1)..];
            path = path[..at];
            if (!DigestPattern.IsMatch(digest))
                throw InvalidReference();
        }

        string? tag = null;
        var colon = path.LastIndexOf(':');
        if (colon >= 0)
        {
            tag = path[(colon + 1)..];
            path = path[..colon];
            if (digest is not null || !TagPattern.IsMatch(tag))
                throw InvalidReference();
        }

        if (path.Length > 255 || !RepositoryPattern.IsMatch(path))
            throw InvalidReference();

        return new OciChartReference(registry.ToLowerInvariant(), path, tag, digest);
    }

    public static string NormalizeVersion(string version)
    {
        // Helm translates SemVer build metadata into an OCI-compatible tag.
        var tag = version.Replace('+', '_');
        if (!TagPattern.IsMatch(tag))
            throw InvalidReference();
        return tag;
    }

    public override string ToString()
        => $"oci://{Registry}/{Repository}{(Tag is not null ? $":{Tag}" : Digest is not null ? $"@{Digest}" : "")}";

    private static ArgumentException InvalidReference() => new("Invalid OCI chart reference.");
}
