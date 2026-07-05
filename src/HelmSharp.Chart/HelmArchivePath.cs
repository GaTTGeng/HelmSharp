using System.Text.RegularExpressions;

namespace HelmSharp.Chart;

internal static partial class HelmArchivePath
{
    private static readonly char[] SegmentSeparators = ['/'];

    public static string NormalizeEntryName(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
            throw new InvalidDataException("Chart archive contains an entry with an empty name.");

        var normalized = entryName.Replace('\\', '/');
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

    public static string GetChartRelativePath(string normalizedEntryName, string? chartRoot)
    {
        if (chartRoot is null)
            return normalizedEntryName;

        var prefix = chartRoot + "/";
        return normalizedEntryName.StartsWith(prefix, StringComparison.Ordinal)
            ? normalizedEntryName[prefix.Length..]
            : normalizedEntryName;
    }

    public static string ResolveSafeDestination(string rootDirectory, string relativeArchivePath)
    {
        var destination = Path.GetFullPath(Path.Combine(
            rootDirectory,
            relativeArchivePath.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(rootDirectory);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;

        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Chart archive entry '{relativeArchivePath}' resolves outside the extraction directory.");

        return destination;
    }

    [GeneratedRegex(@"^[A-Za-z]:($|/)", RegexOptions.CultureInvariant)]
    private static partial Regex DriveQualifiedPathPattern();
}
