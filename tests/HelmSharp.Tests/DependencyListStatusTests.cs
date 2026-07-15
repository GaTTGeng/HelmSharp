using System.Text;
using System.Text.RegularExpressions;
using HelmSharp.Action;

namespace HelmSharp.Tests;

public sealed class DependencyListStatusTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "helmsharp-dependency-list-tests",
        Guid.NewGuid().ToString("N"));

    public DependencyListStatusTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [HelmCliFact]
    public async Task DependencyListAsync_WithLockMatchesHelmStatusRows()
    {
        var chartDirectory = CopyFixture("with-lock");
        await AddPackagedDependencyAsync(chartDirectory, "archive-ok", "1.2.3");
        await AddPackagedDependencyAsync(chartDirectory, "archive-wrong", "1.5.0");
        await AddUnpackedDependencyAsync(chartDirectory, "unpacked", "unpacked", "3.1.0");
        await AddPackagedDependencyAsync(chartDirectory, "alias-target", "4.0.0");
        await AddUnpackedDependencyAsync(chartDirectory, "local-dep", "local-dep", "5.0.0");

        var sharpResult = await CreateClient().DependencyListAsync(chartDirectory);
        var helmResult = await HelmCliRunner.DependencyListAsync(chartDirectory, CancellationToken.None);

        AssertSuccess("HelmSharp dependency list", sharpResult.ExitCode, sharpResult.StandardError);
        AssertSuccess("helm dependency list", helmResult.ExitCode, helmResult.Stderr);
        var sharpRows = ParseRows(sharpResult.StandardOutput);
        var helmRows = ParseRows(helmResult.Stdout);
        Assert.Equal(helmRows, sharpRows);
        Assert.Equal("ok", FindStatus(sharpRows, "archive-ok"));
        Assert.Equal("wrong version", FindStatus(sharpRows, "archive-wrong"));
        Assert.Equal("unpacked", FindStatus(sharpRows, "unpacked"));
        Assert.Equal("ok", FindStatus(sharpRows, "alias-target"));
        Assert.Equal("unpacked", FindStatus(sharpRows, "local-dep"));
    }

    [HelmCliFact]
    public async Task DependencyListAsync_WithoutLockMatchesHelmSemVerStatus()
    {
        var chartDirectory = CopyFixture("without-lock");
        await AddPackagedDependencyAsync(chartDirectory, "range-dep", "1.2.7");

        var sharpResult = await CreateClient().DependencyListAsync(chartDirectory);
        var helmResult = await HelmCliRunner.DependencyListAsync(chartDirectory, CancellationToken.None);

        AssertSuccess("HelmSharp dependency list", sharpResult.ExitCode, sharpResult.StandardError);
        AssertSuccess("helm dependency list", helmResult.ExitCode, helmResult.Stderr);
        Assert.Equal(ParseRows(helmResult.Stdout), ParseRows(sharpResult.StandardOutput));
        Assert.Equal("ok", FindStatus(ParseRows(sharpResult.StandardOutput), "range-dep"));
    }

    [HelmCliFact]
    public async Task DependencyListAsync_NoDependenciesMatchesHelmWarning()
    {
        var chartDirectory = CopyFixture("no-dependencies");

        var sharpResult = await CreateClient().DependencyListAsync(chartDirectory);
        var helmResult = await HelmCliRunner.DependencyListAsync(chartDirectory, CancellationToken.None);

        AssertSuccess("HelmSharp dependency list", sharpResult.ExitCode, sharpResult.StandardError);
        AssertSuccess("helm dependency list", helmResult.ExitCode, helmResult.Stderr);
        Assert.Equal(NormalizeNewlines(helmResult.Stdout), NormalizeNewlines(sharpResult.StandardOutput));
    }

    [Fact]
    public async Task DependencyListAsync_LockVersionOverridesPermissiveDeclaration()
    {
        var chartDirectory = CopyFixture("with-lock");
        await AddPackagedDependencyAsync(chartDirectory, "archive-ok", "1.2.7");

        var result = await CreateClient().DependencyListAsync(chartDirectory);

        Assert.Equal("wrong version", FindStatus(ParseRows(result.StandardOutput), "archive-ok"));
    }

    [Fact]
    public async Task DependencyListAsync_RecognizesAliasDirectoryAndDisabledCondition()
    {
        var chartDirectory = Path.Combine(_tempDirectory, "alias-and-condition");
        await WriteTextAsync(Path.Combine(chartDirectory, "Chart.yaml"), """
            apiVersion: v2
            name: alias-and-condition
            version: 0.1.0
            dependencies:
              - name: alias-target
                alias: cache
                version: 4.0.0
                repository: https://example.test/charts
              - name: optional-dep
                version: 1.0.0
                repository: https://example.test/charts
                condition: optional.enabled
            """);
        await WriteTextAsync(Path.Combine(chartDirectory, "values.yaml"), """
            optional:
              enabled: false
            """);
        await AddUnpackedDependencyAsync(chartDirectory, "cache", "alias-target", "4.0.0");

        var result = await CreateClient().DependencyListAsync(chartDirectory);
        var rows = ParseRows(result.StandardOutput);

        Assert.Equal("unpacked", FindStatus(rows, "alias-target"));
        Assert.Equal("disabled", FindStatus(rows, "optional-dep"));
    }

    [Fact]
    public async Task DependencyListAsync_IgnoresPrefixSharingArchiveWhenCountingVersions()
    {
        var chartDirectory = Path.Combine(_tempDirectory, "prefix-sharing");
        await WriteTextAsync(Path.Combine(chartDirectory, "Chart.yaml"), """
            apiVersion: v2
            name: prefix-sharing
            version: 0.1.0
            dependencies:
              - name: foo
                version: 1.0.0
                repository: https://example.test/charts
            """);
        await AddPackagedDependencyAsync(chartDirectory, "foo", "1.0.0");
        await AddPackagedDependencyAsync(chartDirectory, "foo-bar", "1.0.0");

        var result = await CreateClient().DependencyListAsync(chartDirectory);

        Assert.Equal("ok", FindStatus(ParseRows(result.StandardOutput), "foo"));
    }

    private string CopyFixture(string fixtureName)
    {
        var source = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Charts",
            "dependency-list",
            fixtureName);
        var destination = Path.Combine(_tempDirectory, fixtureName);
        CopyDirectory(source, destination);
        return destination;
    }

    private async Task AddPackagedDependencyAsync(
        string parentDirectory,
        string name,
        string version)
    {
        var sourceDirectory = Path.Combine(
            _tempDirectory,
            $"source-{name}-{version}-{Guid.NewGuid():N}");
        await WriteDependencyChartAsync(sourceDirectory, name, version);
        var chartsDirectory = Path.Combine(parentDirectory, "charts");
        Directory.CreateDirectory(chartsDirectory);
        await HelmChartPackager.PackageAsync(
            sourceDirectory,
            chartsDirectory,
            cancellationToken: CancellationToken.None);
    }

    private static async Task AddUnpackedDependencyAsync(
        string parentDirectory,
        string directoryName,
        string chartName,
        string version)
    {
        var directory = Path.Combine(parentDirectory, "charts", directoryName);
        await WriteDependencyChartAsync(directory, chartName, version);
    }

    private static async Task WriteDependencyChartAsync(
        string directory,
        string name,
        string version)
    {
        await WriteTextAsync(Path.Combine(directory, "Chart.yaml"), $"""
            apiVersion: v2
            name: {name}
            version: {version}
            """);
        await WriteTextAsync(Path.Combine(directory, "values.yaml"), "fixture: dependency-list\n");
    }

    private static IReadOnlyList<DependencyRow> ParseRows(string output)
    {
        var rows = new List<DependencyRow>();
        foreach (var line in NormalizeNewlines(output).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("NAME", StringComparison.Ordinal) ||
                trimmed.StartsWith("WARNING:", StringComparison.Ordinal))
            {
                continue;
            }

            var columns = Regex.Split(trimmed, @"\s+");
            if (columns.Length < 4)
                continue;
            rows.Add(new DependencyRow(
                columns[0],
                columns[1],
                columns[2],
                string.Join(' ', columns.Skip(3))));
        }

        return rows;
    }

    private static string FindStatus(IReadOnlyList<DependencyRow> rows, string name)
        => Assert.Single(rows, row => row.Name == name).Status;

    private static string NormalizeNewlines(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void AssertSuccess(string operation, int exitCode, string standardError)
        => Assert.True(exitCode == 0, $"{operation} failed with exit code {exitCode}: {standardError}");

    private static async Task WriteTextAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }
    }

    private static HelmClient CreateClient()
        => new(new StaticHelmOptionsProvider());

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    private sealed record DependencyRow(
        string Name,
        string Version,
        string Repository,
        string Status);

    private sealed class StaticHelmOptionsProvider : IHelmOptionsProvider
    {
        public ValueTask<HelmExecutionOptions> GetHelmAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new HelmExecutionOptions { DefaultNamespace = "default" });
    }
}
