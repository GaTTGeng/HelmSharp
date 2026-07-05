using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using HelmSharp.Chart;
using YamlDotNet.Core;

namespace HelmSharp.Action;

/// <summary>
/// Packages Helm charts into .tgz archives, matching `helm package` behavior.
/// </summary>
internal static class HelmChartPackager
{
    private const UnixFileMode RegularFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.GroupRead |
        UnixFileMode.OtherRead;

    private static readonly Regex ChartNamePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9_.-]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex VersionPattern = new(
        @"^v?[0-9]+(?:\.[0-9]+){0,2}(?:-(?<prerelease>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Packages a chart directory into a .tgz archive.
    /// Returns the path to the created archive.
    /// </summary>
    public static async Task<string> PackageAsync(
        string chartPath,
        string? destination = null,
        string? version = null,
        string? appVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(chartPath))
            throw new DirectoryNotFoundException($"Chart directory not found: {chartPath}");

        var chartYamlPath = Path.Combine(chartPath, "Chart.yaml");
        if (!File.Exists(chartYamlPath))
            throw new FileNotFoundException("Chart.yaml file is missing");

        var chartYamlContent = await File.ReadAllTextAsync(chartYamlPath, Encoding.UTF8, cancellationToken);
        var metadata = LoadChartMetadata(chartYamlContent);

        var chartName = GetRequiredMetadataString(metadata, "name");
        ValidateChartName(chartName);

        var sourceVersion = GetRequiredMetadataString(metadata, "version");
        ValidateChartVersion(sourceVersion);

        var apiVersion = GetMetadataString(metadata, "apiVersion");
        ValidateApiVersion(apiVersion);

        var chartType = GetMetadataString(metadata, "type");
        ValidateChartType(chartType);

        var chartVersion = sourceVersion;

        if (version is not null)
        {
            ValidateVersionOverride(version);
            chartVersion = version;
            metadata["version"] = version;
        }

        if (appVersion is not null)
        {
            metadata["appVersion"] = appVersion;
        }

        if (version is not null || appVersion is not null)
            chartYamlContent = HelmYaml.Serialize(metadata);

        if (!chartYamlContent.EndsWith("\n", StringComparison.Ordinal))
            chartYamlContent += "\n";

        var fileName = $"{chartName}-{chartVersion}.tgz";
        var destDir = destination ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(destDir);
        var outputPath = Path.Combine(destDir, fileName);
        var outputFullPath = Path.GetFullPath(outputPath);

        await using var fileStream = File.Create(outputPath);
        await using var gzip = new GZipStream(fileStream, CompressionLevel.Optimal);
        await using var tar = new TarWriter(gzip);

        var ignoreRules = await HelmIgnoreRules.LoadAsync(chartPath, cancellationToken);
        await AddDirectoryToTarAsync(tar, chartPath, chartName, chartYamlContent, outputFullPath, ignoreRules, cancellationToken);

        return outputPath;
    }

    private static Dictionary<string, object?> LoadChartMetadata(string chartYamlContent)
    {
        try
        {
            return HelmYaml.DeserializeDictionary(chartYamlContent);
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException($"cannot load Chart.yaml: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException($"cannot load Chart.yaml: {ex.Message}", ex);
        }
    }

    private static string GetRequiredMetadataString(IDictionary<string, object?> metadata, string key)
    {
        var value = GetMetadataString(metadata, key);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"validation: chart.metadata.{key} is required");

        return value;
    }

    private static string? GetMetadataString(IDictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null)
            return null;

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static void ValidateChartName(string chartName)
    {
        if (!ChartNamePattern.IsMatch(chartName) ||
            chartName is "." or ".." ||
            chartName.Contains('/', StringComparison.Ordinal) ||
            chartName.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"validation: chart.metadata.name \"{chartName}\" is invalid");
        }
    }

    private static void ValidateChartVersion(string chartVersion)
    {
        if (!IsValidChartVersion(chartVersion))
            throw new InvalidDataException($"validation: chart.metadata.version \"{chartVersion}\" is invalid");
    }

    private static void ValidateVersionOverride(string chartVersion)
    {
        if (string.IsNullOrWhiteSpace(chartVersion) || !IsValidChartVersion(chartVersion))
            throw new InvalidDataException("Invalid Semantic Version");
    }

    private static bool IsValidChartVersion(string chartVersion)
    {
        var match = VersionPattern.Match(chartVersion);
        if (!match.Success)
            return false;

        var prerelease = match.Groups["prerelease"];
        if (!prerelease.Success)
            return true;

        foreach (var identifier in prerelease.Value.Split('.'))
        {
            if (identifier.Length > 1 &&
                identifier[0] == '0' &&
                identifier.All(char.IsDigit))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateApiVersion(string? apiVersion)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return;

        if (!apiVersion.Equals("v1", StringComparison.Ordinal) &&
            !apiVersion.Equals("v2", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"validation: chart.metadata.apiVersion \"{apiVersion}\" is unsupported");
        }
    }

    private static void ValidateChartType(string? chartType)
    {
        if (string.IsNullOrEmpty(chartType))
            return;

        if (chartType is not ("application" or "library"))
            throw new InvalidDataException("validation: chart.metadata.type must be application or library");
    }

    private static async Task AddDirectoryToTarAsync(
        TarWriter tar,
        string sourceDir,
        string archiveRoot,
        string chartYamlContent,
        string outputFullPath,
        HelmIgnoreRules ignoreRules,
        CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Select(filePath => new
            {
                FullPath = filePath,
                Attributes = File.GetAttributes(filePath),
                RelativePath = NormalizeRelativePath(Path.GetRelativePath(sourceDir, filePath))
            })
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (PathsEqual(Path.GetFullPath(file.FullPath), outputFullPath))
                continue;

            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;

            if (ignoreRules.IgnoreFile(file.RelativePath))
                continue;

            var archivePath = $"{archiveRoot}/{file.RelativePath}";

            var entry = new GnuTarEntry(TarEntryType.RegularFile, archivePath)
            {
                Mode = RegularFileMode,
                ModificationTime = File.GetLastWriteTimeUtc(file.FullPath)
            };
            var fileBytes = file.RelativePath.Equals("Chart.yaml", StringComparison.OrdinalIgnoreCase)
                ? Encoding.UTF8.GetBytes(chartYamlContent)
                : await ReadPackagedFileBytesAsync(file.FullPath, file.RelativePath, cancellationToken);
            entry.DataStream = new MemoryStream(fileBytes);
            await tar.WriteEntryAsync(entry, cancellationToken);
        }
    }

    private static async Task<byte[]> ReadPackagedFileBytesAsync(
        string fullPath,
        string relativePath,
        CancellationToken cancellationToken)
    {
        if (!relativePath.EndsWith("/Chart.yaml", StringComparison.OrdinalIgnoreCase))
            return await File.ReadAllBytesAsync(fullPath, cancellationToken);

        var chartYaml = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken);
        if (!chartYaml.EndsWith("\n", StringComparison.Ordinal))
            chartYaml += "\n";

        return Encoding.UTF8.GetBytes(chartYaml);
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(left, right, comparison);
    }

    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/');

    private sealed class HelmIgnoreRules
    {
        private readonly IReadOnlyList<HelmIgnorePattern> _patterns;

        private HelmIgnoreRules(IReadOnlyList<HelmIgnorePattern> patterns)
        {
            _patterns = patterns;
        }

        public static async Task<HelmIgnoreRules> LoadAsync(
            string chartPath,
            CancellationToken cancellationToken)
        {
            var ignorePath = Path.Combine(chartPath, ".helmignore");
            if (!File.Exists(ignorePath))
                return new HelmIgnoreRules([]);

            var lines = await File.ReadAllLinesAsync(ignorePath, Encoding.UTF8, cancellationToken);
            var patterns = new List<HelmIgnorePattern>();
            for (var i = 0; i < lines.Length; i++)
            {
                var line = i == 0
                    ? lines[i].TrimStart('\uFEFF')
                    : lines[i];
                line = NormalizeRelativePath(line.Trim());

                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                if (line.Contains("**", StringComparison.Ordinal))
                    throw new InvalidDataException("double-star (**) syntax is not supported");

                patterns.Add(HelmIgnorePattern.Parse(line));
            }

            return new HelmIgnoreRules(patterns);
        }

        public bool IgnoreFile(string relativePath)
            => Ignore(relativePath, isDirectory: false);

        private bool Ignore(string path, bool isDirectory)
        {
            if (string.IsNullOrEmpty(path) || path is "." or "./")
                return false;

            var ignored = false;
            foreach (var pattern in _patterns)
            {
                var matches = pattern.MustBeDirectory && !isDirectory
                    ? PatternMatchesAncestorDirectory(pattern, path)
                    : pattern.IsMatch(path);
                if (!matches)
                    continue;

                ignored = !pattern.Negate;
            }

            return ignored;
        }

        private static bool PatternMatchesAncestorDirectory(HelmIgnorePattern pattern, string relativePath)
        {
            var slash = relativePath.IndexOf('/', StringComparison.Ordinal);
            while (slash >= 0)
            {
                if (pattern.IsMatch(relativePath[..slash]))
                    return true;

                slash = relativePath.IndexOf('/', slash + 1);
            }

            return false;
        }
    }

    private sealed class HelmIgnorePattern
    {
        private readonly Regex _matcher;
        private readonly bool _matchBasenameOnly;

        private HelmIgnorePattern(
            Regex matcher,
            bool matchBasenameOnly,
            bool negate,
            bool mustBeDirectory)
        {
            _matcher = matcher;
            _matchBasenameOnly = matchBasenameOnly;
            Negate = negate;
            MustBeDirectory = mustBeDirectory;
        }

        public bool Negate { get; }

        public bool MustBeDirectory { get; }

        public static HelmIgnorePattern Parse(string rule)
        {
            var negate = rule.StartsWith('!');
            if (negate)
                rule = rule[1..];

            var mustBeDirectory = rule.EndsWith('/');
            if (mustBeDirectory)
                rule = rule[..^1];

            var rooted = rule.StartsWith('/');
            if (rooted)
                rule = rule[1..];

            var matchBasenameOnly = !rooted && !rule.Contains('/', StringComparison.Ordinal);
            var regex = new Regex(
                ConvertGlobToRegex(rule),
                RegexOptions.CultureInvariant | RegexOptions.Compiled);

            return new HelmIgnorePattern(regex, matchBasenameOnly, negate, mustBeDirectory);
        }

        public bool IsMatch(string path)
        {
            var value = _matchBasenameOnly
                ? GetBasename(path)
                : path;
            return _matcher.IsMatch(value);
        }

        private static string GetBasename(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash >= 0 ? path[(slash + 1)..] : path;
        }

        private static string ConvertGlobToRegex(string glob)
        {
            var regex = new StringBuilder("^");
            for (var i = 0; i < glob.Length; i++)
            {
                var ch = glob[i];
                switch (ch)
                {
                    case '*':
                        regex.Append("[^/]*");
                        break;
                    case '?':
                        regex.Append("[^/]");
                        break;
                    case '[':
                        regex.Append(ConvertCharacterClass(glob, ref i));
                        break;
                    case '\\':
                        if (i + 1 < glob.Length)
                        {
                            i++;
                            regex.Append(Regex.Escape(glob[i].ToString()));
                        }
                        else
                        {
                            regex.Append(Regex.Escape(ch.ToString()));
                        }
                        break;
                    default:
                        regex.Append(Regex.Escape(ch.ToString()));
                        break;
                }
            }

            regex.Append('$');
            return regex.ToString();
        }

        private static string ConvertCharacterClass(string glob, ref int index)
        {
            var start = index;
            index++;
            if (index >= glob.Length)
                throw new InvalidDataException($"syntax error in pattern: {glob[start..]}");

            var builder = new StringBuilder("[");
            if (glob[index] == '^')
            {
                builder.Append('^');
                index++;
            }

            var hasContent = false;
            for (; index < glob.Length; index++)
            {
                var ch = glob[index];
                if (ch == ']' && hasContent)
                {
                    builder.Append(']');
                    return builder.ToString();
                }

                hasContent = true;
                if (ch is '\\' or ']')
                    builder.Append('\\');
                builder.Append(ch);
            }

            throw new InvalidDataException($"syntax error in pattern: {glob[start..]}");
        }
    }
}
