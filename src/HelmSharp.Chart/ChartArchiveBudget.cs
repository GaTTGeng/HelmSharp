namespace HelmSharp.Chart;

/// <summary>
/// Cumulative decompression budget shared by one chart tree load: root archive, nested
/// packaged dependency archives, and directory-sourced dependency archives all draw from
/// the same counters so a tree of small archives cannot evade total-size and entry-count
/// limits by resetting per child. Every decompressed byte the tar reader consumes and
/// every tar entry (including directory and other structural entries) is metered.
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
    /// Validates a known compressed size against the compressed-input budget before the
    /// bytes are buffered, so oversized inputs are rejected without allocating them.
    /// </summary>
    /// <param name="compressedBytes">Compressed size of the archive about to be read.</param>
    /// <exception cref="ChartArchiveLimitExceededException">Thrown when the compressed input limit is exceeded.</exception>
    internal void CheckCompressedBytes(long compressedBytes)
    {
        if (compressedBytes > _limits.MaxCompressedBytes)
            throw new ChartArchiveLimitExceededException(
                ChartArchiveLimitKind.CompressedBytes,
                _limits.MaxCompressedBytes,
                compressedBytes);
    }

    /// <summary>
    /// Validates the compressed size of one archive stream and opens a scope whose
    /// metered decompression stream and entry reads enforce per-entry, total-extracted,
    /// entry-count, and compression-ratio budgets on the actual bytes produced.
    /// </summary>
    /// <param name="compressedBytes">
    /// Compressed size of this archive stream when known (file or byte-array length);
    /// pass 0 when unknown so only extracted-side budgets apply.
    /// </param>
    /// <returns>A scope that budgets bytes as the archive is read.</returns>
    /// <exception cref="ChartArchiveLimitExceededException">Thrown when the compressed input limit is exceeded.</exception>
    internal ArchiveScope BeginArchive(long compressedBytes)
    {
        CheckCompressedBytes(compressedBytes);
        return new ArchiveScope(this, compressedBytes);
    }

    /// <summary>
    /// Per-archive read scope. Tracks bytes decompressed from one compressed stream for
    /// the ratio bound while charging every byte and every entry to the shared
    /// tree-wide budget.
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
        /// Wraps the decompressed stream presented to the tar reader so every byte it
        /// consumes — tar headers, padding, and entry payloads alike — is charged to the
        /// extracted-byte and compression-ratio budgets. Without this meter, structural
        /// tar data would be decompressed outside any budget.
        /// </summary>
        /// <param name="decompressed">The decompressed archive stream (for example a gzip stream).</param>
        /// <returns>A stream that meters every byte read.</returns>
        internal MeteredDecompressionStream MeterDecompressedStream(Stream decompressed)
            => new(decompressed, this);

        /// <summary>
        /// Checks raw tar headers before TarReader can buffer hidden PAX or GNU metadata.
        /// Reads are capped at one tar block so a declared oversized metadata record is
        /// rejected immediately after its header is parsed.
        /// </summary>
        /// <param name="metered">The decompressed stream with total-byte and ratio metering.</param>
        /// <returns>A stream that accounts for every raw tar entry and bounds metadata records.</returns>
        internal Stream GuardTarStream(Stream metered) => new TarEntryGuardStream(metered, this);

        /// <summary>
        /// Charges one tar entry of any kind — regular file, directory, or other
        /// structural entry — against the shared entry-count budget so header-only
        /// floods cannot evade the count limit.
        /// </summary>
        /// <exception cref="ChartArchiveLimitExceededException">Thrown when the entry-count limit is exceeded.</exception>
        internal void CountEntry()
        {
            _budget._totalEntryCount++;
            if (_budget._totalEntryCount > _budget._limits.MaxEntryCount)
                throw new ChartArchiveLimitExceededException(
                    ChartArchiveLimitKind.EntryCount,
                    _budget._limits.MaxEntryCount,
                    _budget._totalEntryCount);
        }

        private void CheckMetadataSize(long size)
        {
            if (size > _budget._limits.MaxEntryBytes)
                throw new ChartArchiveLimitExceededException(
                    ChartArchiveLimitKind.EntryBytes,
                    _budget._limits.MaxEntryBytes,
                    size);
        }

        /// <summary>
        /// Reads one tar entry's content into memory under the per-entry byte budget.
        /// Tar header size fields are treated as untrusted metadata; only bytes actually
        /// delivered are charged. Total extracted bytes and amplification are charged by
        /// the metered decompression stream, not here.
        /// </summary>
        /// <param name="source">The tar entry data stream.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The entry content.</returns>
        /// <exception cref="ChartArchiveLimitExceededException">Thrown when the per-entry byte budget is exceeded.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled.</exception>
        internal async Task<byte[]> ReadEntryAsync(Stream source, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            long entryBytes = 0;
            int read;
            while ((read = await source.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                entryBytes += read;
                if (entryBytes > _budget._limits.MaxEntryBytes)
                    throw new ChartArchiveLimitExceededException(
                        ChartArchiveLimitKind.EntryBytes,
                        _budget._limits.MaxEntryBytes,
                        entryBytes);

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }

        /// <summary>
        /// Charges decompressed bytes to the tree-wide total and to this archive's
        /// amplification bound, stopping at the first exceeded budget.
        /// </summary>
        /// <param name="count">Number of decompressed bytes just delivered.</param>
        /// <exception cref="ChartArchiveLimitExceededException">Thrown when a byte budget is exceeded.</exception>
        private void ChargeDecompressedBytes(int count)
        {
            _archiveExtractedBytes += count;
            _budget._totalExtractedBytes += count;

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
        }

        /// <summary>
        /// Read-only stream wrapper that charges every byte consumed from a decompression
        /// stream to the archive budget. The tar reader consumes headers, padding, and
        /// entry payloads through this stream, so no decompressed byte escapes the
        /// extracted-byte and amplification budgets.
        /// </summary>
        internal sealed class MeteredDecompressionStream : Stream
        {
            private readonly Stream _inner;
            private readonly ArchiveScope _scope;

            internal MeteredDecompressionStream(Stream inner, ArchiveScope scope)
            {
                _inner = inner;
                _scope = scope;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = _inner.Read(buffer, offset, count);
                if (read > 0)
                    _scope.ChargeDecompressedBytes(read);
                return read;
            }

            public override int Read(Span<byte> buffer)
            {
                var read = _inner.Read(buffer);
                if (read > 0)
                    _scope.ChargeDecompressedBytes(read);
                return read;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read > 0)
                    _scope.ChargeDecompressedBytes(read);
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            // The decompression stream is disposed by its owner; this wrapper only meters reads.
        }

        private sealed class TarEntryGuardStream : Stream
        {
            private const int TarBlockSize = 512;
            private readonly Stream _inner;
            private readonly ArchiveScope _scope;
            private readonly byte[] _header = new byte[TarBlockSize];
            private int _headerBytes;
            private long _remainingDataBytes;
            private int _remainingPaddingBytes;

            internal TarEntryGuardStream(Stream inner, ArchiveScope scope)
            {
                _inner = inner;
                _scope = scope;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = _inner.Read(buffer, offset, GetReadLimit(count));
                if (read > 0)
                    Process(buffer.AsSpan(offset, read));
                return read;
            }

            public override int Read(Span<byte> buffer)
            {
                var read = _inner.Read(buffer[..GetReadLimit(buffer.Length)]);
                if (read > 0)
                    Process(buffer[..read]);
                return read;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var read = await _inner.ReadAsync(buffer[..GetReadLimit(buffer.Length)], cancellationToken).ConfigureAwait(false);
                if (read > 0)
                    Process(buffer.Span[..read]);
                return read;
            }

            private int GetReadLimit(int requested)
            {
                if (_remainingDataBytes > 0)
                    return (int)Math.Min(requested, Math.Min(_remainingDataBytes, int.MaxValue));

                if (_remainingPaddingBytes > 0)
                    return Math.Min(requested, _remainingPaddingBytes);

                return Math.Min(requested, TarBlockSize - _headerBytes);
            }

            private void Process(ReadOnlySpan<byte> bytes)
            {
                var offset = 0;
                while (offset < bytes.Length)
                {
                    if (_remainingDataBytes > 0)
                    {
                        var consumed = (int)Math.Min(_remainingDataBytes, bytes.Length - offset);
                        _remainingDataBytes -= consumed;
                        offset += consumed;
                        if (_remainingDataBytes == 0)
                            _remainingPaddingBytes = (int)((TarBlockSize - (_headerSize % TarBlockSize)) % TarBlockSize);
                        continue;
                    }

                    if (_remainingPaddingBytes > 0)
                    {
                        var consumed = Math.Min(_remainingPaddingBytes, bytes.Length - offset);
                        _remainingPaddingBytes -= consumed;
                        offset += consumed;
                        continue;
                    }

                    var headerBytes = Math.Min(TarBlockSize - _headerBytes, bytes.Length - offset);
                    bytes.Slice(offset, headerBytes).CopyTo(_header.AsSpan(_headerBytes));
                    _headerBytes += headerBytes;
                    offset += headerBytes;
                    if (_headerBytes != TarBlockSize)
                        continue;

                    _headerBytes = 0;
                    if (IsEndOfArchiveBlock(_header))
                        continue;

                    _scope.CountEntry();
                    var size = ReadTarSize(_header.AsSpan(124, 12));
                    if (_header[156] is (byte)'x' or (byte)'g' or (byte)'L' or (byte)'K')
                        _scope.CheckMetadataSize(size);
                    _headerSize = size;
                    _remainingDataBytes = size;
                    if (_remainingDataBytes == 0)
                        _remainingPaddingBytes = 0;
                }
            }

            private long _headerSize;

            private static bool IsEndOfArchiveBlock(ReadOnlySpan<byte> header)
            {
                foreach (var value in header)
                {
                    if (value != 0)
                        return false;
                }

                return true;
            }

            private static long ReadTarSize(ReadOnlySpan<byte> field)
            {
                if ((field[0] & 0x80) != 0)
                {
                    if ((field[0] & 0x40) != 0)
                        return 0;

                    long value = field[0] & 0x3f;
                    for (var index = 1; index < field.Length; index++)
                    {
                        if (value > (long.MaxValue >> 8))
                            return long.MaxValue;
                        value = (value << 8) | field[index];
                    }

                    return value;
                }

                long octal = 0;
                var sawDigit = false;
                foreach (var value in field)
                {
                    if (value is 0 or (byte)' ')
                    {
                        if (sawDigit)
                            break;
                        continue;
                    }

                    if (value is < (byte)'0' or > (byte)'7')
                        return 0;
                    sawDigit = true;
                    if (octal > (long.MaxValue >> 3))
                        return long.MaxValue;
                    octal = (octal << 3) | (long)(value - (byte)'0');
                }

                return octal;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
