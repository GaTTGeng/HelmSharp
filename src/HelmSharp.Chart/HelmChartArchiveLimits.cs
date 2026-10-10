namespace HelmSharp.Chart;

/// <summary>
/// Resource budgets enforced while reading chart archives (packaged <c>.tgz</c> charts and
/// embedded dependency archives), covering decompression amplification (CWE-400/CWE-409).
/// Limits apply to bytes actually streamed while extracting entries, not to tar header
/// size fields, so archives with misleading size metadata cannot bypass enforcement.
/// </summary>
/// <remarks>
/// The <see cref="Default"/> profile fits real-world public charts (for example the
/// ingress-nginx and cert-manager fixtures) while bounding untrusted input. Callers that
/// load operator-supplied archives larger than the defaults can construct an instance with
/// raised budgets; use that explicit options API only for trusted chart sources.
/// All byte budgets are cumulative across a chart tree: nested packaged dependency
/// archives share one extracted-byte and entry-count budget and are depth-limited.
/// </remarks>
public sealed class HelmChartArchiveLimits
{
    /// <summary>Gets the conservative default budgets used when callers do not pass options.</summary>
    public static HelmChartArchiveLimits Default { get; } = new();

    /// <summary>
    /// Gets or sets the maximum compressed archive size in bytes (the <c>.tgz</c> input).
    /// Default: 100 MiB.
    /// </summary>
    public long MaxCompressedBytes { get; init; } = 100L * 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum total extracted bytes across the whole chart tree,
    /// including nested packaged dependency archives. Default: 200 MiB.
    /// </summary>
    public long MaxTotalExtractedBytes { get; init; } = 200L * 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum extracted size in bytes of a single archive entry. Default: 10 MiB.
    /// </summary>
    public long MaxEntryBytes { get; init; } = 10L * 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum number of regular file entries across the whole chart tree.
    /// Default: 10,000.
    /// </summary>
    public int MaxEntryCount { get; init; } = 10_000;

    /// <summary>
    /// Gets or sets the maximum decompression amplification for one archive stream,
    /// expressed as extracted-bytes ÷ compressed-bytes. Default: 200.
    /// </summary>
    public double MaxCompressionRatio { get; init; } = 200;

    /// <summary>
    /// Gets or sets the maximum nesting depth of packaged dependency archives
    /// (<c>charts/*.tgz</c>). Directory subcharts do not consume depth. Default: 10.
    /// </summary>
    public int MaxDependencyDepth { get; init; } = 10;

    /// <summary>
    /// Creates limits with every budget raised for trusted, operator-supplied chart
    /// archives that intentionally exceed the default profile (for example very large
    /// umbrella charts). Prefer keeping <see cref="Default"/> for untrusted input.
    /// </summary>
    /// <param name="maxCompressedBytes">Compressed archive cap in bytes, or null for no cap.</param>
    /// <param name="maxTotalExtractedBytes">Total extracted-byte cap, or null for no cap.</param>
    /// <param name="maxEntryBytes">Per-entry extracted-byte cap, or null for no cap.</param>
    /// <param name="maxEntryCount">Regular-entry count cap, or null for no cap.</param>
    /// <param name="maxCompressionRatio">Per-archive amplification ratio cap, or null for no cap.</param>
    /// <param name="maxDependencyDepth">Packaged dependency depth cap, or null for no cap.</param>
    /// <returns>A limits instance with the requested raised budgets.</returns>
    public static HelmChartArchiveLimits ForTrustedCharts(
        long? maxCompressedBytes = null,
        long? maxTotalExtractedBytes = null,
        long? maxEntryBytes = null,
        int? maxEntryCount = null,
        double? maxCompressionRatio = null,
        int? maxDependencyDepth = null)
        => new()
        {
            MaxCompressedBytes = maxCompressedBytes ?? long.MaxValue,
            MaxTotalExtractedBytes = maxTotalExtractedBytes ?? long.MaxValue,
            MaxEntryBytes = maxEntryBytes ?? long.MaxValue,
            MaxEntryCount = maxEntryCount ?? int.MaxValue,
            MaxCompressionRatio = maxCompressionRatio ?? double.MaxValue,
            MaxDependencyDepth = maxDependencyDepth ?? int.MaxValue,
        };

    /// <summary>
    /// Validates that every budget is positive and finite. Called when enforcement starts.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a budget is zero, negative, or NaN.</exception>
    internal void EnsureValid()
    {
        if (MaxCompressedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxCompressedBytes), "Compressed byte limit must be positive.");
        if (MaxTotalExtractedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxTotalExtractedBytes), "Total extracted byte limit must be positive.");
        if (MaxEntryBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxEntryBytes), "Entry byte limit must be positive.");
        if (MaxEntryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxEntryCount), "Entry count limit must be positive.");
        if (!double.IsFinite(MaxCompressionRatio) || MaxCompressionRatio <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxCompressionRatio), "Compression ratio limit must be a positive finite number.");
        if (MaxDependencyDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxDependencyDepth), "Dependency depth limit must be non-negative.");
    }
}
