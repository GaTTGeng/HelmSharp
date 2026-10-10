namespace HelmSharp.Chart;

/// <summary>Identifies which chart-archive resource budget was exceeded.</summary>
public enum ChartArchiveLimitKind
{
    /// <summary>The compressed archive input exceeded <see cref="HelmChartArchiveLimits.MaxCompressedBytes"/>.</summary>
    CompressedBytes,

    /// <summary>Decompressed bytes (headers, padding, and payloads) exceeded <see cref="HelmChartArchiveLimits.MaxTotalExtractedBytes"/>.</summary>
    TotalExtractedBytes,

    /// <summary>A single entry's extracted bytes exceeded <see cref="HelmChartArchiveLimits.MaxEntryBytes"/>.</summary>
    EntryBytes,

    /// <summary>The tar entry count (all entry kinds) exceeded <see cref="HelmChartArchiveLimits.MaxEntryCount"/>.</summary>
    EntryCount,

    /// <summary>One archive stream expanded past <see cref="HelmChartArchiveLimits.MaxCompressionRatio"/> times its compressed size.</summary>
    CompressionRatio,

    /// <summary>Packaged dependency nesting exceeded <see cref="HelmChartArchiveLimits.MaxDependencyDepth"/>.</summary>
    DependencyDepth,
}

/// <summary>
/// Thrown when chart archive loading or extraction exceeds a configured resource budget
/// (<see cref="HelmChartArchiveLimits"/>). The message reports only the limit kind and
/// numeric magnitudes so untrusted archive content is never echoed.
/// </summary>
public sealed class ChartArchiveLimitExceededException : IOException
{
    /// <summary>
    /// Creates the exception for an exceeded budget.
    /// </summary>
    /// <param name="limit">Which budget was exceeded.</param>
    /// <param name="limitValue">Configured bound in the limit's natural unit (bytes, count, depth, or max extracted bytes implied by the ratio).</param>
    /// <param name="observedValue">Observed value that crossed the bound.</param>
    public ChartArchiveLimitExceededException(ChartArchiveLimitKind limit, long limitValue, long observedValue)
        : base(FormatMessage(limit, limitValue, observedValue))
    {
        Limit = limit;
        LimitValue = limitValue;
        ObservedValue = observedValue;
    }

    /// <summary>Gets the budget that was exceeded.</summary>
    public ChartArchiveLimitKind Limit { get; }

    /// <summary>Gets the configured bound that was exceeded.</summary>
    public long LimitValue { get; }

    /// <summary>Gets the observed value that crossed the bound.</summary>
    public long ObservedValue { get; }

    private static string FormatMessage(ChartArchiveLimitKind limit, long limitValue, long observedValue)
        => limit switch
        {
            ChartArchiveLimitKind.CompressedBytes =>
                $"Chart archive exceeds the compressed input limit of {limitValue} bytes (observed {observedValue} bytes).",
            ChartArchiveLimitKind.TotalExtractedBytes =>
                $"Chart archive exceeds the total extracted size limit of {limitValue} bytes (observed {observedValue} bytes).",
            ChartArchiveLimitKind.EntryBytes =>
                $"Chart archive entry exceeds the extracted entry size limit of {limitValue} bytes (observed {observedValue} bytes).",
            ChartArchiveLimitKind.EntryCount =>
                $"Chart archive exceeds the entry count limit of {limitValue} entries (observed {observedValue} entries).",
            ChartArchiveLimitKind.CompressionRatio =>
                $"Chart archive exceeds the decompression ratio limit (extracted {observedValue} bytes beyond the {limitValue} byte amplification bound).",
            ChartArchiveLimitKind.DependencyDepth =>
                $"Chart archive exceeds the packaged dependency depth limit of {limitValue} (observed depth {observedValue}).",
            _ => $"Chart archive exceeds resource limit {limit} (limit {limitValue}, observed {observedValue}).",
        };
}
