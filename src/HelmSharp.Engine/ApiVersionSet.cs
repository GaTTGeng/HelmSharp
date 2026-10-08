using System.Collections;

namespace HelmSharp.Engine;

/// <summary>
/// The set of API versions exposed as <c>.Capabilities.APIVersions</c> during rendering.
/// Behaves as a list of version strings and adds the Helm-specific <see cref="Has"/> lookup.
/// </summary>
internal sealed class ApiVersionSet : IReadOnlyList<object?>
{
    private readonly List<object?> _versions;

    /// <summary>Creates the set from API version strings (usually from user-supplied capabilities).</summary>
    public ApiVersionSet(IEnumerable<object?> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        _versions = versions.ToList();
    }

    /// <summary>
    /// Returns true when <paramref name="version"/> is present. Matches Helm semantics:
    /// comparison is ordinal and each entry is compared as its string form.
    /// </summary>
    public bool Has(string version)
        => _versions.Any(v => string.Equals(v?.ToString(), version, StringComparison.Ordinal));

    public int Count => _versions.Count;
    public object? this[int index] => _versions[index];
    public IEnumerator<object?> GetEnumerator() => _versions.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
