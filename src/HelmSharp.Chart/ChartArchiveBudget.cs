namespace HelmSharp.Chart;

/// <summary>
/// Cumulative decompression budget shared by one chart tree load: root archive, nested
/// packaged dependency archives, and directory-sourced dependency archives all draw from
/// the same counters so a tree of small archives cannot evade total-size and entry-count
/// limits by resetting per child.
/// </summary>
internal sealed class ChartArchiveBudget
{
    private readonly HelmChartArchiveLimits _limits;
    private long _totalExtractedBytes;
    private long _totalEntryCount;

    internal ChartArchiveBudget(HelmChartArchiveLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.EnsureValid();
        _limits = limits;
    }

    internal HelmChartArchiveLimits Limits => _limits;

    /// <summary>
    /// Rejects packaged dependency nesting deeper than the configured depth.
    /// The root chart is depth 0; each packaged dependency archive increments depth.
    /// </summary>
    /// <param name="depth">Depth of the archive about to be read.</param>
    /// <exception cref="ChartArchiveLimitExceededException">Thrown when the depth limit is exceeded.</exception>
    internal void CheckDependencyDepth(int depth)
    {
        if (depth > _limits.MaxDependencyDepth)
            throw new ChartArchiveLimitExceededException(
                ChartArchiveLimitKind.DependencyDepth,
                _limits.MaxDependencyDepth,
                depth);
    }

    /// <summary>
    /// Validates the compressed size of one archive stream and opens a scope whose entry
    /// reads enforce per-entry, total-extracted, and compression-ratio budgets on the
    /// actual bytes streamed.
    /// </summary>
    /// <param name="compressedBytes">
    /// Compressed size of this archive stream when known (file or byte-array length);
    /// pass 0 when unknown so only extracted-side budgets apply.
    /// </param>
    /// <returns>A scope that budgets bytes as entries are read.</returns>
    /// <exception cref="ChartArchiveLimitExceededException">Thrown when the compressed input limit is exceeded.</exception>
    internal ArchiveScope BeginArchive(long compressedBytes)
    {
        if (compressedBytes > _limits.MaxCompressedBytes)
            throw new ChartArchiveLimitExceededException(
                ChartArchiveLimitKind.CompressedBytes,
                _limits.MaxCompressedBytes,
                compressedBytes);

        return new ArchiveScope(this, compressedBytes);
    }

    /// <summary>
    /// Per-archive read scope. Tracks bytes extracted from one compressed stream for the
    /// ratio bound while charging every byte to the shared tree-wide budget.
    /// </summary>
    internal sealed class ArchiveScope
    {
        private readonly ChartArchiveBudget _budget;
        private readonly long _compressedBytes;
        private long _archiveExtractedBytes;

        internal ArchiveScope(ChartArchiveBudget budget, long compressedBytes)
        {
            _budget = budget;
            _compressedBytes = compressedBytes;
        }

        /// <summary>
        /// Reads one tar entry's content into memory, counting bytes as they are streamed
        /// and stopping at the first exceeded budget. Tar header size fields are treated
        /// as untrusted metadata; only bytes actually delivered are charged.
        /// </summary>
        /// <param name="source">The tar entry data stream.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The entry content.</returns>
        /// <exception cref="ChartArchiveLimitExceededException">Thrown when any budget is exceeded.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled.</exception>
        internal async Task<byte[]> ReadEntryAsync(Stream source, CancellationToken cancellationToken)
        {
            // --- Stage 1: charge the entry against the shared entry-count budget ---
            _budget._totalEntryCount++;
            if (_budget._totalEntryCount > _budget._limits.MaxEntryCount)
                throw new ChartArchiveLimitExceededException(
                    ChartArchiveLimitKind.EntryCount,
                    _budget._limits.MaxEntryCount,
                    _budget._totalEntryCount);

            // --- Stage 2: stream content and enforce byte budgets on actual reads ---
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            long entryBytes = 0;
            int read;
            while ((read = await source.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                entryBytes += read;
                _archiveExtractedBytes += read;
                _budget._totalExtractedBytes += read;

                if (entryBytes > _budget._limits.MaxEntryBytes)
                    throw new ChartArchiveLimitExceededException(
                        ChartArchiveLimitKind.EntryBytes,
                        _budget._limits.MaxEntryBytes,
                        entryBytes);

                if (_budget._totalExtractedBytes > _budget._limits.MaxTotalExtractedBytes)
                    throw new ChartArchiveLimitExceededException(
                        ChartArchiveLimitKind.TotalExtractedBytes,
                        _budget._limits.MaxTotalExtractedBytes,
                        _budget._totalExtractedBytes);

                // Amplification bound per compressed stream. A nested dependency archive is
                // its own stream (its .tgz bytes), so parent and child each get a ratio check
                // while sharing the total extracted budget. Compared in double space so a
                // near-unbounded ratio (trusted-chart profile) cannot overflow the bound.
                if (_compressedBytes > 0 &&
                    (double)_archiveExtractedBytes > _compressedBytes * _budget._limits.MaxCompressionRatio)
                {
                    throw new ChartArchiveLimitExceededException(
                        ChartArchiveLimitKind.CompressionRatio,
                        (long)Math.Min(_compressedBytes * _budget._limits.MaxCompressionRatio, long.MaxValue),
                        _archiveExtractedBytes);
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }
}
