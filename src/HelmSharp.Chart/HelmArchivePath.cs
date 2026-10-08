using System.Text.RegularExpressions;

namespace HelmSharp.Chart;

/// <summary>
/// Path safety and normalization helpers for chart archive entries.
/// Guards extraction against zip-slip and absolute-path entries so a malicious or
/// malformed archive cannot write outside the destination directory.
/// </summary>
internal static partial class HelmArchivePath
{
    private static readonly char[] SegmentSeparators = ['/'];

    /// <summary>
    /// Normalizes an archive entry name to forward slashes and rejects empty, absolute,
    /// drive-qualified, or <c>.</c>/<c>..</c> containing paths.
    /// </summary>
    /// <exception cref="InvalidDataException">The entry name is empty or unsafe to extract.</exception>
    public static string NormalizeEntryName(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
            throw new InvalidDataException("Chart archive contains an entry with an empty name.");

        var normalized = entryName.Replace('\\', '/');
        // Zip-slip guard: absolute, UNC, and Windows drive-qualified names can escape the
        // extraction root when combined with Path.Combine, so reject them before any path math.
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.StartsWith("//", StringComparison.Ordinal) ||
            DriveQualifiedPathPattern().IsMatch(normalized))
        {
            throw new InvalidDataException($"Chart archive entry '{entryName}' uses an unsafe absolute path.");
        }

        var segments = normalized.Split(SegmentSeparators, StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException($"Chart archive entry '{entryName}' uses an unsafe relative path.");

        return normalized;
    }

    /// <summary>
    /// Finds the single top-level directory that wraps the chart (Helm packs charts under one
    /// root folder containing Chart.yaml). Returns null when entries are not uniformly rooted
    /// or when no rooted Chart.yaml is present.
    /// </summary>
    public static string? FindChartRoot(IEnumerable<string> normalizedEntryNames)
    {
        string? root = null;
        var sawRootedChartYaml = false;

        foreach (var entryName in normalizedEntryNames)
        {
            var slash = entryName.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0)
                return null;

            var currentRoot = entryName[..slash];
            root ??= currentRoot;
            if (!string.Equals(root, currentRoot, StringComparison.Ordinal))
                return null;

            if (entryName.Equals($"{root}/Chart.yaml", StringComparison.OrdinalIgnoreCase))
                sawRootedChartYaml = true;
        }

        return sawRootedChartYaml ? root : null;
    }

    /// <summary>Strips the chart root prefix so the path is relative to the chart directory.</summary>
    public static string GetChartRelativePath(string normalizedEntryName, string? chartRoot)
    {
        if (chartRoot is null)
            return normalizedEntryName;

        var prefix = chartRoot + "/";
        return normalizedEntryName.StartsWith(prefix, StringComparison.Ordinal)
            ? normalizedEntryName[prefix.Length..]
            : normalizedEntryName;
    }

    /// <summary>
    /// Resolves an entry's extraction path and asserts it stays under the extraction root.
    /// </summary>
    /// <exception cref="InvalidDataException">The resolved path escapes <paramref name="rootDirectory"/>.</exception>
    public static string ResolveSafeDestination(string rootDirectory, string relativeArchivePath)
    {
        var destination = Path.GetFullPath(Path.Combine(
            rootDirectory,
            relativeArchivePath.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(rootDirectory);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;

        // Final zip-slip check on the fully resolved path, since segment validation alone
        // cannot catch entries that resolve outside the root after path canonicalization.
        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Chart archive entry '{relativeArchivePath}' resolves outside the extraction directory.");

        return destination;
    }

    [GeneratedRegex(@"^[A-Za-z]:($|/)", RegexOptions.CultureInvariant)]
    private static partial Regex DriveQualifiedPathPattern();
}
