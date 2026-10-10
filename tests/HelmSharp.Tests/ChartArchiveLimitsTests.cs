using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using HelmSharp.Chart;
using HelmSharp.Repo;

namespace HelmSharp.Tests;

/// <summary>
/// Decompression resource budgets for chart archive loading and extraction: per-limit
/// enforcement on streamed bytes, shared nested-dependency budgets, misleading tar
/// size metadata, cancellation, partial-extraction cleanup, and default-profile fit.
/// </summary>
public sealed class ChartArchiveLimitsTests : IDisposable
{
    private readonly string _tempDir;

    public ChartArchiveLimitsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "helmsharp-archive-limits-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    // --- Per-limit enforcement ---

    [Fact]
    public async Task LoadArchive_EntryExceedingEntryByteLimit_ThrowsEntryBytesLimit()
    {
        var archive = CreateChartTgz(("big.bin", new byte[8 * 1024]));
        var limits = new HelmChartArchiveLimits { MaxEntryBytes = 4 * 1024, MaxTotalExtractedBytes = 1024 * 1024 };

        var ex = await Assert.ThrowsAsync<ChartArchiveLimitExceededException>(
            () => LoadArchiveBytesAsync(archive, limits));

        Assert.Equal(ChartArchiveLimitKind.EntryBytes, ex.Limit);
        Assert.Equal(4 * 1024, ex.LimitValue);
    }

    [Fact]
    public async Task LoadArchive_TotalExtractedExceedsBudget_ThrowsTotalExtractedLimit()
    {
        var archive = CreateChartTgz(
            ("a.bin", new byte[5 * 1024]),
            ("b.bin", new byte[5 * 1024]));
        var limits = new HelmChartArchiveLimits
        {
            MaxEntryBytes = 1024 * 1024,
            MaxTotalExtractedBytes = 8 * 1024,
            MaxCompressionRatio = 10_000,
        };

        var ex = await Assert.ThrowsAsync<ChartArchiveLimitExceededException>(
            () => LoadArchiveBytesAsync(archive, limits));

        Assert.Equal(ChartArchiveLimitKind.TotalExtractedBytes, ex.Limit);
        Assert.Equal(8 * 1024, ex.LimitValue);
    }

    [Fact]
    public async Task LoadArchive_EntryCountExceedsBudget_ThrowsEntryCountLimit()
    {
        var archive = CreateChartTgz(
            ("a.bin", new byte[16]),
            ("b.bin", new byte[16]),
            ("c.bin", new byte[16]),
            ("d.bin", new byte[16]));
        var limits = new HelmChartArchiveLimits { MaxEntryCount = 3 };

        var ex = await Assert.ThrowsAsync<ChartArchiveLimitExceededException>(
            () => LoadArchiveBytesAsync(archive, limits));

        Assert.Equal(ChartArchiveLimitKind.EntryCount, ex.Limit);
        Assert.Equal(3, ex.LimitValue);
    }

    [Fact]
    public async Task LoadArchive_CompressedInputExceedsBudget_ThrowsCompressedBytesLimit()
    {
        var archive = CreateChartTgz(("file.bin", new byte[64]));
        Assert.True(archive.Length > 20);
        var limits = new HelmChartArchiveLimits { MaxCompressedBytes = 20 };

        var ex = await Assert.ThrowsAsync<ChartArchiveLimitExceededException>(
            () => LoadArchiveBytesAsync(archive, limits));

        Assert.Equal(ChartArchiveLimitKind.CompressedBytes, ex.Limit);
    }

    [Fact]
    public async Task LoadArchive_HighlyCompressibleInput_ThrowsCompressionRatioLimit()
    {
        // ~48 KiB of zeros compresses to well under 1 KiB; the ratio bound must fire
        // even though per-entry and total extracted budgets would allow the payload.
        var archive = CreateChartTgz(("zeros.bin", new byte[48 * 1024]));
        Assert.True(archive.Length < 2 * 1024, $"compressed size should be tiny, was {archive.Length}");
        var limits = new HelmChartArchiveLimits
        {
            MaxEntryBytes = 1024 * 1024,
            MaxTotalExtractedBytes = 1024 * 1024,
            MaxCompressionRatio = 10,
        };

        var ex = await Assert.ThrowsAsync<ChartArchiveLimitExceededException>(
            () => LoadArchiveBytesAsync(archive, limits));

        Assert.Equal(ChartArchiveLimitKind.CompressionRatio, ex.Limit);
    }

    [Fact]
    public async Task LoadArchive_NestedDependencyBeyondDepthLimit_ThrowsDependencyDepthLimit()
    {
        var deepest = CreateChartTgz(("leaf.txt", "leaf"u8.ToArray()));
        var middle = CreateNestedChartTgz("middle", ("level2.tgz", deepest));
        var root = CreateNestedChartTgz("root", ("level1.tgz", middle));
        var limits = new HelmChartArchiveLimits { MaxDependencyDepth = 1 };

        var ex = await Assert.ThrowsAsync<ChartArchiveLimitExceededException>(
            () => LoadArchiveBytesAsync(root, limits));

        Assert.Equal(ChartArchiveLimitKind.DependencyDepth, ex.Limit);
        Assert.Equal(1, ex.LimitValue);
    }

    // --- Shared nested budget ---

    [Fact]
    public async Task LoadArchive_NestedDependenciesShareExtractedBudget()
    {
        // Each child alone fits the total budget; together they must not.
        var childA = CreateChartTgz(("a.bin", new byte[6 * 1024]));
        var childB = CreateChartTgz(("b.bin", new byte[6 * 1024]));
        var root = CreateNestedChartTgz("root", ("dep-a.tgz", childA), ("dep-b.tgz", childB));
        var limits = new HelmChartArchiveLimits
        {
            MaxEntryBytes = 1024 * 1024,
            MaxTotalExtractedBytes = 10 * 1024,
            MaxCompressionRatio = 10_000,
        };

        var ex = await Assert.ThrowsAsync<ChartArchiveLimitExceededException>(
            () => LoadArchiveBytesAsync(root, limits));

        Assert.Equal(ChartArchiveLimitKind.TotalExtractedBytes, ex.Limit);
    }

    [Fact]
    public async Task LoadArchive_NestedDependenciesShareEntryCountBudget()
    {
        var childA = CreateChartTgz(("a1.bin", "a1"u8.ToArray()), ("a2.bin", "a2"u8.ToArray()));
        var childB = CreateChartTgz(("b1.bin", "b1"u8.ToArray()), ("b2.bin", "b2"u8.ToArray()));
        var root = CreateNestedChartTgz("root", ("dep-a.tgz", childA), ("dep-b.tgz", childB));
        // Root archive contributes 3 regular entries (Chart.yaml + two dependency
        // archives); each child adds 3 more. A shared budget of 5 fails inside the
        // first child even though each child alone would fit a fresh budget.
        var limits = new HelmChartArchiveLimits { MaxEntryCount = 5 };

        var ex = await Assert.ThrowsAsync<ChartArchiveLimitExceededException>(
            () => LoadArchiveBytesAsync(root, limits));

        Assert.Equal(ChartArchiveLimitKind.EntryCount, ex.Limit);
    }

    // --- Misleading size metadata ---

    [Fact]
    public async Task LoadArchive_InflatedHeaderSize_EnforcesStreamedBytesNotHeaderClaim()
    {
        // Header claims 200 KB while only 64 bytes of payload exist. A per-entry limit
        // between the claim and the real payload must not fire from the claim alone.
        var archive = CreateTgzWithInflatedHeaderSize("chart/Chart.yaml", claimedSize: 200 * 1024, actualPayload: new byte[64]);
        var limits = new HelmChartArchiveLimits
        {
            MaxEntryBytes = 100 * 1024,
            MaxTotalExtractedBytes = 1024 * 1024,
            MaxCompressionRatio = 10_000,
        };

        try
        {
            var files = await LoadArchiveBytesAsync(archive, limits);
            // Success is acceptable when the reader yields only the real payload bytes.
            if (files.TryGetValue("Chart.yaml", out var content))
                Assert.True(content.Length <= 200 * 1024);
        }
        catch (ChartArchiveLimitExceededException ex)
        {
            // Only legitimate streamed-byte overruns are allowed — never the header claim.
            Assert.True(
                ex.Limit is ChartArchiveLimitKind.TotalExtractedBytes or ChartArchiveLimitKind.CompressionRatio,
                $"unexpected limit {ex.Limit} from inflated header claim");
        }
        catch (EndOfStreamException)
        {
            // Truncated entry data relative to the claim is a structural error, not a budget bypass.
        }
    }

    [Fact]
    public async Task LoadArchive_MisleadingCompressedMetadata_RatioStillBounded()
    {
        // File size looks harmless; expansion is not. Streaming ratio enforcement stops it.
        var archive = CreateChartTgz(("zeros.bin", new byte[64 * 1024]));
        var limits = new HelmChartArchiveLimits
        {
            MaxEntryBytes = 1024 * 1024,
            MaxTotalExtractedBytes = 1024 * 1024,
            MaxCompressionRatio = 5,
        };

        var ex = await Assert.ThrowsAsync<ChartArchiveLimitExceededException>(
            () => LoadArchiveBytesAsync(archive, limits));

        Assert.Equal(ChartArchiveLimitKind.CompressionRatio, ex.Limit);
    }

    // --- Cancellation ---

    [Fact]
    public async Task LoadArchive_CancelledToken_ThrowsOperationCanceled()
    {
        var archive = CreateChartTgz(("file.bin", new byte[1024]));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HelmChartLoader.LoadAsync(WriteArchiveToTemp(archive), cts.Token));
    }

    [Fact]
    public async Task ExtractChartArchive_CancelledToken_ThrowsOperationCanceledAndWritesNothing()
    {
        var archive = CreateChartTgz(("file.bin", new byte[1024]));
        var extractDir = Path.Combine(_tempDir, "extract-cancel");
        Directory.CreateDirectory(extractDir);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HelmChartRepository.ExtractChartArchiveAsync(archive, extractDir, cts.Token));

        Assert.Empty(Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories));
    }

    // --- Just-under-limit success ---

    [Fact]
    public async Task LoadArchive_EntryAtExactEntryLimit_Succeeds()
    {
        var archive = CreateChartTgz(("exact.bin", new byte[4 * 1024]));
        var limits = new HelmChartArchiveLimits
        {
            MaxEntryBytes = 4 * 1024,
            MaxTotalExtractedBytes = 4 * 1024 + 4 * 1024,
            MaxCompressionRatio = 10_000,
            MaxEntryCount = 10,
        };

        var chart = await LoadArchiveBytesAsync(archive, limits);

        Assert.True(chart.ContainsKey("Chart.yaml"));
        Assert.Equal(4 * 1024, chart["exact.bin"].Length);
    }

    [Fact]
    public async Task LoadArchive_JustUnderAllLimits_Succeeds()
    {
        var payload = new byte[2 * 1024];
        var archive = CreateChartTgz(("under.bin", payload));
        var limits = new HelmChartArchiveLimits
        {
            MaxCompressedBytes = archive.Length,
            MaxEntryBytes = payload.Length + 64,
            MaxTotalExtractedBytes = payload.Length + 4096,
            MaxCompressionRatio = 10_000,
            MaxEntryCount = 4,
        };

        var chart = await LoadArchiveBytesAsync(archive, limits);

        Assert.Equal(payload.Length, chart["under.bin"].Length);
    }

    // --- Partial-extraction cleanup ---

    [Fact]
    public async Task ExtractChartArchiveAsync_UnsafeEntryAfterValidFiles_RemovesPartialOutput()
    {
        var extractDir = Path.Combine(_tempDir, "extract-unsafe");
        Directory.CreateDirectory(extractDir);
        var archive = CreateChartTgz(
            ("ok.yaml", "kind: ConfigMap"u8.ToArray()),
            ("../escape.txt", "evil"u8.ToArray()));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => HelmChartRepository.ExtractChartArchiveAsync(archive, extractDir, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExtractChartArchiveAsync_FailureAfterValidWrites_RemovesPartialOutput()
    {
        // x/y.txt materializes a directory named x; a later regular entry with the same
        // name fails on File.Create and must remove the files already written.
        var extractDir = Path.Combine(_tempDir, "extract-partial");
        Directory.CreateDirectory(extractDir);
        var archive = CreateChartTgz(
            ("x/y.txt", "written-first"u8.ToArray()),
            ("x", "conflicts-with-directory"u8.ToArray()));

        await Assert.ThrowsAnyAsync<Exception>(
            () => HelmChartRepository.ExtractChartArchiveAsync(archive, extractDir, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories));
    }

    // --- Non-sensitive exception text ---

    [Fact]
    public async Task LoadArchive_LimitExceeded_MessageDoesNotEchoArchiveContent()
    {
        const string secretMarker = "SECRET-ENTRY-CONTENT-MARKER";
        var archive = CreateChartTgz(
            ("leak.txt", Encoding.UTF8.GetBytes(secretMarker)),
            ("pad.bin", new byte[8 * 1024]));
        var limits = new HelmChartArchiveLimits { MaxTotalExtractedBytes = 1024 };

        var ex = await Assert.ThrowsAsync<ChartArchiveLimitExceededException>(
            () => LoadArchiveBytesAsync(archive, limits));

        Assert.DoesNotContain(secretMarker, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("leak.txt", ex.Message, StringComparison.Ordinal);
    }

    // --- Explicit trusted-chart options API ---

    [Fact]
    public async Task LoadArchive_ForTrustedCharts_AllowsLargerBudgets()
    {
        var archive = CreateChartTgz(("big.bin", new byte[32 * 1024]));
        var limits = HelmChartArchiveLimits.ForTrustedCharts(maxEntryBytes: 1024 * 1024);

        var chart = await LoadArchiveBytesAsync(archive, limits);

        Assert.Equal(32 * 1024, chart["big.bin"].Length);
    }

    [Fact]
    public async Task LoadArchive_InvalidLimits_ThrowsArgumentOutOfRange()
    {
        var archive = CreateChartTgz(("file.bin", new byte[8]));
        var limits = new HelmChartArchiveLimits { MaxEntryBytes = 0 };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => LoadArchiveBytesAsync(archive, limits));
    }

    // --- Repository extraction honors limits ---

    [Fact]
    public async Task ExtractChartArchiveAsync_EntryOverLimit_ThrowsAndWritesNothing()
    {
        var extractDir = Path.Combine(_tempDir, "extract-limit");
        Directory.CreateDirectory(extractDir);
        var archive = CreateChartTgz(("big.bin", new byte[16 * 1024]));
        var limits = new HelmChartArchiveLimits
        {
            MaxEntryBytes = 4 * 1024,
            MaxTotalExtractedBytes = 1024 * 1024,
            MaxCompressionRatio = 10_000,
        };

        var ex = await Assert.ThrowsAsync<ChartArchiveLimitExceededException>(
            () => HelmChartRepository.ExtractChartArchiveAsync(archive, extractDir, limits, CancellationToken.None));

        Assert.Equal(ChartArchiveLimitKind.EntryBytes, ex.Limit);
        Assert.Empty(Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories));
    }

    // --- Default profile fits the real public chart fixtures ---

    [Theory]
    [InlineData("cert-manager")]
    [InlineData("external-dns")]
    [InlineData("ingress-nginx")]
    [InlineData("metrics-server")]
    [InlineData("podinfo")]
    public async Task LoadAsync_RealChartFixtureDirectory_LoadsWithinDefaultLimits(string chartName)
    {
        var chartDir = Path.Combine(FixturesRoot, "real", chartName);
        Assert.True(Directory.Exists(chartDir), $"missing fixture {chartDir}");

        var chart = await HelmChartLoader.LoadAsync(chartDir, CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(chart.Name));
    }

    [Fact]
    public async Task LoadAsync_RealChartFixturePackedArchive_LoadsWithinDefaultLimits()
    {
        // Pack the cert-manager fixture and load the archive under default budgets.
        var chartDir = Path.Combine(FixturesRoot, "real", "cert-manager");
        var packed = PackDirectoryAsTgz(chartDir, "cert-manager");
        var archivePath = WriteArchiveToTemp(packed);

        var chart = await HelmChartLoader.LoadAsync(archivePath, CancellationToken.None);

        Assert.Equal("cert-manager", chart.Name);
    }

    // --- Helpers ---

    private static string FixturesRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Charts");

    private async Task<Dictionary<string, byte[]>> LoadArchiveBytesAsync(byte[] archive, HelmChartArchiveLimits limits)
    {
        var path = WriteArchiveToTemp(archive);
        // Route through the public loader so nested dependency budgets are exercised;
        // the returned chart is loaded from the archive bytes on disk.
        var chart = await HelmChartLoader.LoadAsync(path, limits, CancellationToken.None);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in chart.Files)
            files[key] = value;
        foreach (var (key, content) in chart.Templates)
            files[key] = Encoding.UTF8.GetBytes(content);
        files["Chart.yaml"] = Encoding.UTF8.GetBytes($"{chart.Name}\n{chart.Version}");
        return files;
    }

    private string WriteArchiveToTemp(byte[] archive)
    {
        var path = Path.Combine(_tempDir, $"chart-{Guid.NewGuid():N}.tgz");
        File.WriteAllBytes(path, archive);
        return path;
    }

    private static byte[] CreateChartTgz(params (string Name, byte[] Content)[] extraEntries)
    {
        var entries = new List<(string Name, byte[] Content)>
        {
            ("chart/Chart.yaml", Encoding.UTF8.GetBytes("""
                apiVersion: v2
                name: limit-test
                version: 1.0.0
                """)),
        };
        entries.AddRange(extraEntries);
        return CreateTgz(entries.ToArray());
    }

    private static byte[] CreateNestedChartTgz(string chartName, params (string Name, byte[] Content)[] dependencyArchives)
    {
        var entries = new List<(string Name, byte[] Content)>
        {
            ($"{chartName}/Chart.yaml", Encoding.UTF8.GetBytes($"""
                apiVersion: v2
                name: {chartName}
                version: 1.0.0
                """)),
        };
        foreach (var (name, content) in dependencyArchives)
            entries.Add(($"{chartName}/charts/{name}", content));
        return CreateTgz(entries.ToArray());
    }

    private static byte[] CreateTgz((string Name, byte[] Content)[] entries)
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

    /// <summary>
    /// Builds a gzipped tar whose entry header claims <paramref name="claimedSize"/> bytes
    /// while only <paramref name="actualPayload"/> is present, so enforcement can be
    /// checked against streamed bytes rather than the header size field.
    /// </summary>
    private static byte[] CreateTgzWithInflatedHeaderSize(string entryName, long claimedSize, byte[] actualPayload)
    {
        var tarBytes = new MemoryStream();
        var header = new byte[512];
        var nameBytes = Encoding.ASCII.GetBytes(entryName);
        Array.Copy(nameBytes, header, Math.Min(nameBytes.Length, 100));
        WriteOctal(header, 100, 8, 0x1A4); // mode 0644
        WriteOctal(header, 108, 8, 0);
        WriteOctal(header, 116, 8, 0);
        WriteOctal(header, 124, 12, claimedSize);
        WriteOctal(header, 136, 12, 0);
        header[156] = (byte)'0'; // regular file
        Encoding.ASCII.GetBytes("ustar", 0, 5, header, 257);
        header[262] = 0;
        Encoding.ASCII.GetBytes("00", 0, 2, header, 263);
        for (var i = 148; i < 156; i++)
            header[i] = 0x20;
        var checksum = header.Sum(b => b);
        WriteOctal(header, 148, 7, checksum);
        header[155] = 0x20;

        tarBytes.Write(header);
        tarBytes.Write(actualPayload);
        var padding = (512 - (actualPayload.Length % 512)) % 512;
        tarBytes.Write(new byte[padding]);
        tarBytes.Write(new byte[1024]); // end-of-archive
        tarBytes.Flush();

        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, leaveOpen: true))
        {
            tarBytes.WriteTo(gzip);
        }

        return memory.ToArray();
    }

    private static void WriteOctal(byte[] header, int offset, int length, long value)
    {
        var text = Convert.ToString(value, 8).PadLeft(length - 1, '0');
        Encoding.ASCII.GetBytes(text, 0, text.Length, header, offset);
        header[offset + length - 1] = 0;
    }

    private static byte[] PackDirectoryAsTgz(string directory, string rootName)
    {
        var entries = new List<(string Name, byte[] Content)>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            entries.Add(($"{rootName}/{relative}", File.ReadAllBytes(file)));
        }

        return CreateTgz(entries.ToArray());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }
}
