using System.Collections;

namespace HelmSharp.Engine;

internal sealed class ApiVersionSet : IReadOnlyList<object?>
{
    private readonly List<object?> _versions;

    public ApiVersionSet(IEnumerable<object?> versions) => _versions = versions.ToList();

    public bool Has(string version)
        => _versions.Any(v => string.Equals(v?.ToString(), version, StringComparison.Ordinal));

    public int Count => _versions.Count;
    public object? this[int index] => _versions[index];
    public IEnumerator<object?> GetEnumerator() => _versions.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
