using System.Formats.Tar;
using System.IO.Compression;
using HelmSharp.Repo;

namespace HelmSharp.Tests;

/// <summary>
/// Chart archive extraction: chart-root stripping into the destination directory and
/// rejection of zip entries that would escape it (zip-slip protection).
/// </summary>
public sealed class HelmChartRepositoryExtractionTests : IDisposable
{
    private readonly string _tempDir;

    public HelmChartRepositoryExtractionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "helmsharp-repo-extraction-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task ExtractChartArchiveAsync_StripsChartRootAndWritesInsideDestination()
    {
        var extractDir = Path.Combine(_tempDir, "extract");
        var archiveBytes = CreateArchive(
            ("repo-chart/Chart.yaml", """
                apiVersion: v2
                name: repo-chart
                version: 1.0.0
                """u8.ToArray()),
            ("repo-chart/templates/deployment.yaml", "kind: Deployment\n"u8.ToArray()));

        await HelmChartRepository.ExtractChartArchiveAsync(archiveBytes, extractDir, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(extractDir, "Chart.yaml")));
        Assert.True(File.Exists(Path.Combine(extractDir, "templates", "deployment.yaml")));
        Assert.False(File.Exists(Path.Combine(extractDir, "repo-chart", "Chart.yaml")));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("repo-chart/../evil.txt")]
    public async Task ExtractChartArchiveAsync_RejectsUnsafeEntriesWithoutEscapingDestination(string unsafeEntryName)
    {
        var extractDir = Path.Combine(_tempDir, "extract-unsafe");
        Directory.CreateDirectory(extractDir);
        var archiveBytes = CreateArchive(
            ("repo-chart/Chart.yaml", """
                apiVersion: v2
                name: repo-chart
                version: 1.0.0
                """u8.ToArray()),
            (unsafeEntryName, "evil"u8.ToArray()));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => HelmChartRepository.ExtractChartArchiveAsync(archiveBytes, extractDir, CancellationToken.None));

        Assert.Contains("unsafe", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_tempDir, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(extractDir, "evil.txt")));
    }

    private static byte[] CreateArchive(params (string Name, byte[] Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            foreach (var (name, content) in entries)
            {
                var entry = new GnuTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(content)
                };
                tar.WriteEntry(entry);
            }
        }

        return memory.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
