using System.Runtime.CompilerServices;
using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>Splits an ingestion document into ordered, token-bounded text chunks.</summary>
public sealed class RecursiveTokenChunker : IngestionChunker<string>
{
    private readonly TokenBudget _budget;
    private readonly ProseSplitter _splitter;
    private readonly int _overlap;
    private readonly ProtectedChunker? _protectedChunker;
    private readonly bool _repeatTableHeaders;
    private readonly int _newlineTokens;

    /// <summary>Initializes the chunker with a validated snapshot of its configuration.</summary>
    /// <param name="options">The chunker configuration, including its tokenizer.</param>
    public RecursiveTokenChunker(RecursiveChunkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Tokenizer);
        ArgumentOutOfRangeException.ThrowIfNegative(options.ReservedHeaderTokens);
        int limit = options.MaxTokensPerChunk - options.ReservedHeaderTokens;
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ReservedHeaderTokens,
                $"{nameof(options.ReservedHeaderTokens)} must be less than {nameof(options.MaxTokensPerChunk)}.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.OverlapTokens);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(options.OverlapTokens, limit);
        ArgumentNullException.ThrowIfNull(options.Separators);

        string[] separators = options.Separators.ToArray();
        for (int i = 0; i < separators.Length; i++)
        {
            if (separators[i] is null || (separators[i].Length == 0 && i != separators.Length - 1))
            {
                throw new ArgumentException("Only the final separator may be empty, and separators cannot be null.",
                    nameof(options));
            }
        }

        if (!Enum.IsDefined(options.KeepSeparator))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.KeepSeparator,
                $"{nameof(options.KeepSeparator)} is not a defined {nameof(SeparatorPlacement)} value.");
        }

        _budget = new TokenBudget(options.Tokenizer, limit);
        _splitter = new ProseSplitter(_budget, separators, options.KeepSeparator);
        _overlap = options.OverlapTokens;
        _newlineTokens = _budget.Count("\n");
        _repeatTableHeaders = options.RepeatTableHeaders;
        _protectedChunker = options.PreserveAtomicElements ? new ProtectedChunker(_budget) : null;
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(
        IngestionDocument document, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);

        foreach (IngestionDocumentSection section in document.Sections)
        {
            foreach (IngestionChunk<string> chunk in ProcessSection(document, section, new HeadingTrail(), cancellationToken))
            {
                yield return chunk;
            }
        }
    }

    private IEnumerable<IngestionChunk<string>> ProcessSection(
        IngestionDocument document, IngestionDocumentSection section,
        HeadingTrail headings, CancellationToken cancellationToken)
    {
        using PooledText buffer = new PooledText();
        SectionState state = new(document, buffer, headings.Breadcrumb);
        foreach (IngestionDocumentElement element in section.Elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<IngestionChunk<string>> chunks = element switch
            {
                IngestionDocumentHeader header => StartHeading(state, headings, header, cancellationToken),
                IngestionDocumentSection child => EnterSection(state, headings, child, cancellationToken),
                _ => ProcessElement(state, element, cancellationToken),
            };

            foreach (IngestionChunk<string> chunk in chunks)
            {
                yield return chunk;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (IngestionChunk<string> final in Flush(state, cancellationToken))
        {
            yield return final;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private IEnumerable<IngestionChunk<string>> StartHeading(SectionState state,
        HeadingTrail headings, IngestionDocumentHeader header, CancellationToken cancellationToken)
    {
        foreach (IngestionChunk<string> text in Flush(state, cancellationToken))
        {
            yield return text;
        }

        cancellationToken.ThrowIfCancellationRequested();
        headings.Open(header);
        state.Context = headings.Breadcrumb;
        state.FollowingProse = false;
    }

    private IEnumerable<IngestionChunk<string>> EnterSection(SectionState state,
        HeadingTrail headings, IngestionDocumentSection child, CancellationToken cancellationToken)
    {
        foreach (IngestionChunk<string> text in Flush(state, cancellationToken))
        {
            yield return text;
        }

        foreach (IngestionChunk<string> chunk in ProcessSection(state.Document, child, headings.Clone(), cancellationToken))
        {
            yield return chunk;
        }

        state.FollowingProse = false;
    }

    private IEnumerable<IngestionChunk<string>> ProcessElement(SectionState state, IngestionDocumentElement element,
        CancellationToken cancellationToken)
    {
        string? markdown = element.GetMarkdown();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            yield break;
        }

        int? elementStartLine = element.Metadata.TryGetValue(ChunkMetadataKeys.StartLine, out object? sl) && sl is int l
            ? l
            : null;

        if (_protectedChunker is not null && element is IngestionDocumentTable table)
        {
            foreach (IngestionChunk<string> text in Flush(state, cancellationToken))
            {
                yield return text;
            }

            cancellationToken.ThrowIfCancellationRequested();
            int? tableStartLine = elementStartLine;
            int? tableEndLine = null;
            if (elementStartLine.HasValue)
            {
                int lastIndex = markdown.Length - 1;
                while (lastIndex >= 0 && (markdown[lastIndex] == '\n' || markdown[lastIndex] == '\r'))
                {
                    lastIndex--;
                }

                tableEndLine = lastIndex >= 0
                    ? elementStartLine.Value + CountNewlines(markdown.AsSpan(0, lastIndex))
                    : elementStartLine.Value;
            }

            foreach (TableChunk part in _protectedChunker.Table(table, markdown, _repeatTableHeaders, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return TableSplitter.CreateChunk(part, state.Document, state.Context, tableStartLine, tableEndLine);
            }

            state.FollowingProse = false;
            yield break;
        }

        string elementName = element.GetType().Name;
        int start = 0;
        foreach (ProtectedRegion region in CollectRegions(markdown, cancellationToken))
        {
            foreach (IngestionChunk<string> chunk in Prose(state, markdown, start, region.Start, elementName, elementStartLine, cancellationToken))
            {
                yield return chunk;
            }

            foreach (IngestionChunk<string> text in Flush(state, cancellationToken))
            {
                yield return text;
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (IngestionChunk<string> chunk in EmitRegion(state, markdown, region, elementStartLine, cancellationToken))
            {
                yield return chunk;
            }

            start = region.End;
            state.FollowingProse = false;
        }

        if (!markdown.AsSpan(start).IsWhiteSpace())
        {
            foreach (IngestionChunk<string> chunk in Prose(state, markdown, start, markdown.Length, elementName, elementStartLine, cancellationToken))
            {
                yield return chunk;
            }

            state.FollowingProse = true;
        }
    }

    private List<ProtectedRegion> CollectRegions(string markdown, CancellationToken cancellationToken)
    {
        List<ProtectedRegion> regions = [];
        if (_protectedChunker is null)
        {
            return regions;
        }

        foreach (MarkdownFence fence in MarkdownFence.Find(markdown, cancellationToken))
        {
            regions.Add(new ProtectedRegion(fence.Start, fence.End, fence, null));
        }

        foreach (MarkdownTable mdTable in MarkdownTable.Find(markdown, cancellationToken))
        {
            regions.Add(new ProtectedRegion(mdTable.Start, mdTable.End, null, mdTable));
        }

        regions.Sort((a, b) => a.Start.CompareTo(b.Start));
        return regions;
    }

    private IEnumerable<IngestionChunk<string>> EmitRegion(SectionState state, string markdown, ProtectedRegion region,
        int? elementStartLine, CancellationToken cancellationToken)
    {
        ProtectedChunker protectedChunker = _protectedChunker!;
        if (region.Fence is { } fence)
        {
            int? startLine = null;
            int? endLine = null;
            if (elementStartLine.HasValue)
            {
                startLine = elementStartLine.Value + CountNewlines(markdown.AsSpan(0, fence.Start));
                int lastIndex = fence.End - 1;
                while (lastIndex > fence.Start && (markdown[lastIndex] == '\n' || markdown[lastIndex] == '\r'))
                {
                    lastIndex--;
                }

                endLine = elementStartLine.Value + CountNewlines(markdown.AsSpan(0, lastIndex));
            }

            foreach (string chunk in protectedChunker.Fence(markdown, fence, cancellationToken))
            {
                yield return CreateChunk(chunk, state.Document, state.Context, startLine, endLine);
            }
        }
        else if (region.Table is { } mdTable)
        {
            IReadOnlyList<Range> rows = mdTable.GetRowRanges(markdown);
            int? tableStartLine = elementStartLine.HasValue
                ? elementStartLine.Value + CountNewlines(markdown.AsSpan(0, mdTable.Start))
                : null;

            foreach (TableChunk part in protectedChunker.Table(markdown, mdTable, _repeatTableHeaders, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int? partEndLine = null;
                if (elementStartLine.HasValue)
                {
                    if (part.EndRow > 0 && part.EndRow <= rows.Count)
                    {
                        Range lastRow = rows[part.EndRow - 1];
                        int lastIndex = lastRow.End.GetOffset(markdown.Length) - 1;
                        while (lastIndex > mdTable.Start && (markdown[lastIndex] == '\n' || markdown[lastIndex] == '\r'))
                        {
                            lastIndex--;
                        }

                        partEndLine = elementStartLine.Value + CountNewlines(markdown.AsSpan(0, lastIndex));
                    }
                    else
                    {
                        int lastIndex = mdTable.End - 1;
                        while (lastIndex > mdTable.Start && (markdown[lastIndex] == '\n' || markdown[lastIndex] == '\r'))
                        {
                            lastIndex--;
                        }

                        partEndLine = elementStartLine.Value + CountNewlines(markdown.AsSpan(0, lastIndex));
                    }
                }

                yield return TableSplitter.CreateChunk(part, state.Document, state.Context, tableStartLine, partEndLine);
            }
        }
    }

    private IEnumerable<IngestionChunk<string>> Prose(SectionState state, string markdown, int start, int end,
        string elementName, int? elementStartLine, CancellationToken cancellationToken)
    {
        if (end <= start)
        {
            yield break;
        }

        foreach (BufferedChunk chunk in AddProse(state, markdown, start, end, elementName, state.FollowingProse,
                     elementStartLine, cancellationToken))
        {
            yield return CreateChunk(chunk.Content, state.Document, state.Context, chunk.StartLine, chunk.EndLine);
        }
    }

    /// <summary>Settles the queued prose and emits whatever the buffer holds.</summary>
    private IEnumerable<IngestionChunk<string>> Flush(SectionState state, CancellationToken cancellationToken)
    {
        foreach (BufferedChunk chunk in Settle(state, cancellationToken))
        {
            yield return CreateChunk(chunk.Content, state.Document, state.Context, chunk.StartLine, chunk.EndLine);
        }

        if (Take(state) is { } final)
        {
            yield return CreateChunk(final.Content, state.Document, state.Context, final.StartLine, final.EndLine);
        }
    }

    /// <summary>
    /// Queues prose pieces while their estimated tokens (the buffer's exact count plus each piece counted alone) fit
    /// the budget, and settles the queue once they may not. Joining text can change its token count either way, so
    /// the estimate only decides when to look; every chunk is cut from exact counts in <see cref="Settle"/>.
    /// </summary>
    private IEnumerable<BufferedChunk> AddProse(
        SectionState state, string text, int start, int end, string element, bool separate, int? elementStartLine,
        CancellationToken cancellationToken)
    {
        foreach (Range range in _splitter.Split(text, start, end, element, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int tokens = _budget.Count(text.AsSpan(range)) + (separate ? _newlineTokens : 0);

            int? pieceStartLine = null;
            int? pieceEndLine = null;
            if (elementStartLine.HasValue)
            {
                int startOffset = range.Start.GetOffset(text.Length);
                int endOffset = range.End.GetOffset(text.Length);
                pieceStartLine = elementStartLine.Value + CountNewlines(text.AsSpan(0, startOffset));
                int lastIndex = endOffset - 1;
                while (lastIndex > startOffset && (text[lastIndex] == '\n' || text[lastIndex] == '\r'))
                {
                    lastIndex--;
                }

                pieceEndLine = elementStartLine.Value + CountNewlines(text.AsSpan(0, lastIndex));
            }

            state.Pending.Add(new PendingPiece(text, range, separate, tokens, pieceStartLine, pieceEndLine));
            state.PendingTokens += tokens;
            separate = false;
            if (state.BufferTokens + state.PendingTokens > _budget.Limit)
            {
                foreach (BufferedChunk chunk in Settle(state, cancellationToken))
                {
                    yield return chunk;
                }
            }
        }
    }

    /// <summary>
    /// Appends the queued pieces to the buffer exactly as appending them one at a time would: every piece that fits
    /// joins the buffer, and a piece that does not closes the current chunk and starts the next, carrying overlap.
    /// </summary>
    private IEnumerable<BufferedChunk> Settle(SectionState state, CancellationToken cancellationToken)
    {
        List<PendingPiece> pending = state.Pending;
        int next = 0;
        while (next < pending.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int committed = state.Buffer.Length;
            int estimate = 0;
            for (int total = state.BufferTokens; next + estimate < pending.Count &&
                 total + pending[next + estimate].Tokens <= _budget.Limit; estimate++)
            {
                total += pending[next + estimate].Tokens;
            }

            int first = next;
            int fit = FitSearch.Count(pending.Count - next, estimate, count =>
            {
                AppendPieces(state.Buffer, committed, pending, first, count);
                return _budget.Count(state.Buffer.Span) <= _budget.Limit;
            });

            AppendPieces(state.Buffer, committed, pending, next, fit);
            if (fit > 0)
            {
                state.BufferTokens = _budget.Count(state.Buffer.Span);
                if (state.BufferStartLine == null && pending[next].StartLine.HasValue)
                {
                    state.BufferStartLine = pending[next].StartLine;
                }

                if (pending[next + fit - 1].EndLine.HasValue)
                {
                    state.BufferEndLine = pending[next + fit - 1].EndLine;
                }

                next += fit;
                continue;
            }

            PendingPiece piece = pending[next++];
            if (Take(state) is { } previous)
            {
                yield return previous;
                cancellationToken.ThrowIfCancellationRequested();
                CarryOverlap(state, previous, piece, cancellationToken);
            }

            if (state.Buffer.Length == 0)
            {
                _ = TryAppend(state, piece, separate: false);
            }
        }

        pending.Clear();
        state.PendingTokens = 0;
    }

    /// <summary>Resets the buffer to its committed text and appends <paramref name="count"/> queued pieces.</summary>
    private static void AppendPieces(PooledText buffer, int committed, List<PendingPiece> pending, int first, int count)
    {
        buffer.Length = committed;
        for (int i = first; i < first + count; i++)
        {
            if (buffer.Length > 0 && pending[i].Separate)
            {
                buffer.Append("\n");
            }

            buffer.Append(pending[i].Text.AsSpan(pending[i].Range));
        }
    }

    /// <summary>Appends one piece when the buffer then still fits, measured exactly.</summary>
    private bool TryAppend(SectionState state, PendingPiece piece, bool separate)
    {
        PooledText buffer = state.Buffer;
        int length = buffer.Length;
        int? oldStart = state.BufferStartLine;
        int? oldEnd = state.BufferEndLine;

        if (length > 0 && separate)
        {
            buffer.Append("\n");
        }

        buffer.Append(piece.Text.AsSpan(piece.Range));
        int tokens = _budget.Count(buffer.Span);
        if (tokens <= _budget.Limit)
        {
            state.BufferTokens = tokens;
            if (state.BufferStartLine == null && piece.StartLine.HasValue)
            {
                state.BufferStartLine = piece.StartLine;
            }

            if (piece.EndLine.HasValue)
            {
                state.BufferEndLine = piece.EndLine;
            }

            return true;
        }

        buffer.Length = length;
        state.BufferStartLine = oldStart;
        state.BufferEndLine = oldEnd;
        return false;
    }

    private void CarryOverlap(SectionState state, BufferedChunk previous, PendingPiece piece,
        CancellationToken cancellationToken)
    {
        string previousContent = previous.Content;
        if (_overlap == 0 || piece.Text.AsSpan(piece.Range).IsWhiteSpace())
        {
            return;
        }

        int start = Math.Clamp(_budget.SuffixIndex(previousContent, _overlap), 0, previousContent.Length);
        for (; start < previousContent.Length; start++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TextBoundaries.IsScalarBoundary(previousContent, start) || !_splitter.IsOverlapBoundary(previousContent, start) ||
                previousContent.AsSpan(start).IsWhiteSpace())
            {
                continue;
            }

            int overlapTokens = _budget.Count(previousContent.AsSpan(start));
            if (overlapTokens > _overlap)
            {
                continue;
            }

            state.Buffer.Append(previousContent.AsSpan(start));
            int? overlapStartLine = previous.StartLine.HasValue
                ? previous.StartLine.Value + CountNewlines(previousContent.AsSpan(0, start))
                : null;
            state.BufferStartLine = overlapStartLine;
            state.BufferEndLine = previous.EndLine;

            if (TryAppend(state, piece, piece.Separate))
            {
                return;
            }

            state.Buffer.Length = 0;
            state.BufferTokens = 0;
            state.BufferStartLine = null;
            state.BufferEndLine = null;
        }
    }

    private static BufferedChunk? Take(SectionState state)
    {
        PooledText buffer = state.Buffer;
        if (buffer.Span.IsWhiteSpace())
        {
            buffer.Length = 0;
            state.BufferTokens = 0;
            state.BufferStartLine = null;
            state.BufferEndLine = null;
            return null;
        }

        string text = buffer.ToString();
        int? startLine = state.BufferStartLine;
        int? endLine = state.BufferEndLine;
        buffer.Length = 0;
        state.BufferTokens = 0;
        state.BufferStartLine = null;
        state.BufferEndLine = null;
        return new BufferedChunk(text, startLine, endLine);
    }

    private static IngestionChunk<string> CreateChunk(
        string content,
        IngestionDocument document,
        string? context,
        int? startLine,
        int? endLine)
    {
        IngestionChunk<string> chunk = new(content, document, context);
        if (startLine.HasValue && endLine.HasValue)
        {
            if (document.Identifier is { Length: > 0 } filePath)
            {
                chunk.Metadata[ChunkMetadataKeys.FilePath] = filePath;
            }

            chunk.Metadata[ChunkMetadataKeys.StartLine] = startLine.Value;
            chunk.Metadata[ChunkMetadataKeys.EndLine] = endLine.Value;
            chunk.Metadata[ChunkMetadataKeys.LineRange] = $"{startLine.Value}-{endLine.Value}";
        }

        return chunk;
    }

    private static int CountNewlines(ReadOnlySpan<char> span) => span.Count('\n');

    private readonly record struct ProtectedRegion(int Start, int End, MarkdownFence? Fence, MarkdownTable? Table);

    private readonly record struct BufferedChunk(string Content, int? StartLine, int? EndLine);

    /// <summary>The mutable state shared by the steps that process one section.</summary>
    private sealed class SectionState(IngestionDocument document, PooledText buffer, string? context)
    {
        internal IngestionDocument Document { get; } = document;

        internal PooledText Buffer { get; } = buffer;

        internal string? Context { get; set; } = context;

        internal bool FollowingProse { get; set; }

        /// <summary>Gets or sets the exact token count of <see cref="Buffer"/>; zero when it is empty.</summary>
        internal int BufferTokens { get; set; }

        internal int? BufferStartLine { get; set; }

        internal int? BufferEndLine { get; set; }

        /// <summary>Gets the prose pieces waiting to join the buffer.</summary>
        internal List<PendingPiece> Pending { get; } = [];

        /// <summary>Gets or sets the sum of the pending pieces' individual token counts.</summary>
        internal int PendingTokens { get; set; }
    }

    private readonly record struct PendingPiece(
        string Text,
        Range Range,
        bool Separate,
        int Tokens,
        int? StartLine,
        int? EndLine);
}
