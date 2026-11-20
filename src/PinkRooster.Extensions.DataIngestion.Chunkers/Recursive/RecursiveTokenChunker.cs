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
        _splitter = new ProseSplitter(_budget, separators, options.KeepSeparator,
            separators.SequenceEqual(RecursiveChunkerOptions.DefaultSeparators, StringComparer.Ordinal));
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

        if (_protectedChunker is not null && element is IngestionDocumentTable table)
        {
            foreach (IngestionChunk<string> text in Flush(state, cancellationToken))
            {
                yield return text;
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (TableChunk part in _protectedChunker.Table(table, markdown, _repeatTableHeaders, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return TableSplitter.CreateChunk(part, state.Document, state.Context);
            }

            state.FollowingProse = false;
            yield break;
        }

        string elementName = element.GetType().Name;
        int start = 0;
        foreach (ProtectedRegion region in CollectRegions(markdown, cancellationToken))
        {
            foreach (IngestionChunk<string> chunk in Prose(state, markdown, start, region.Start, elementName, cancellationToken))
            {
                yield return chunk;
            }

            foreach (IngestionChunk<string> text in Flush(state, cancellationToken))
            {
                yield return text;
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (IngestionChunk<string> chunk in EmitRegion(state, markdown, region, cancellationToken))
            {
                yield return chunk;
            }

            start = region.End;
            state.FollowingProse = false;
        }

        if (!markdown.AsSpan(start).IsWhiteSpace())
        {
            foreach (IngestionChunk<string> chunk in Prose(state, markdown, start, markdown.Length, elementName, cancellationToken))
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
        CancellationToken cancellationToken)
    {
        ProtectedChunker protectedChunker = _protectedChunker!;
        if (region.Fence is { } fence)
        {
            foreach (string chunk in protectedChunker.Fence(markdown, fence, cancellationToken))
            {
                yield return new IngestionChunk<string>(chunk, state.Document, state.Context);
            }
        }
        else if (region.Table is { } mdTable)
        {
            foreach (TableChunk part in protectedChunker.Table(markdown, mdTable, _repeatTableHeaders, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return TableSplitter.CreateChunk(part, state.Document, state.Context);
            }
        }
    }

    private IEnumerable<IngestionChunk<string>> Prose(SectionState state, string markdown, int start, int end,
        string elementName, CancellationToken cancellationToken)
    {
        if (end <= start)
        {
            yield break;
        }

        foreach (string chunk in AddProse(state, markdown, start, end, elementName, state.FollowingProse,
                     cancellationToken))
        {
            yield return new IngestionChunk<string>(chunk, state.Document, state.Context);
        }
    }

    /// <summary>Settles the queued prose and emits whatever the buffer holds.</summary>
    private IEnumerable<IngestionChunk<string>> Flush(SectionState state, CancellationToken cancellationToken)
    {
        foreach (string chunk in Settle(state, cancellationToken))
        {
            yield return new IngestionChunk<string>(chunk, state.Document, state.Context);
        }

        if (Take(state) is { } text)
        {
            yield return new IngestionChunk<string>(text, state.Document, state.Context);
        }
    }

    /// <summary>
    /// Queues prose pieces while their estimated tokens (the buffer's exact count plus each piece counted alone) fit
    /// the budget, and settles the queue once they may not. Joining text can change its token count either way, so
    /// the estimate only decides when to look; every chunk is cut from exact counts in <see cref="Settle"/>.
    /// </summary>
    private IEnumerable<string> AddProse(
        SectionState state, string text, int start, int end, string element, bool separate, CancellationToken cancellationToken)
    {
        foreach (Range range in _splitter.Split(text, start, end, element, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int tokens = _budget.Count(text.AsSpan(range)) + (separate ? _newlineTokens : 0);
            state.Pending.Add(new PendingPiece(text, range, separate, tokens));
            state.PendingTokens += tokens;
            separate = false;
            if (state.BufferTokens + state.PendingTokens > _budget.Limit)
            {
                foreach (string chunk in Settle(state, cancellationToken))
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
    private IEnumerable<string> Settle(SectionState state, CancellationToken cancellationToken)
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
                next += fit;
                continue;
            }

            PendingPiece piece = pending[next++];
            if (Take(state) is { } previous)
            {
                yield return previous;
                cancellationToken.ThrowIfCancellationRequested();
                CarryOverlap(state, previous, piece.Text, piece.Range, piece.Separate, cancellationToken);
            }

            if (state.Buffer.Length == 0)
            {
                _ = TryAppend(state, piece.Text, piece.Range, separate: false);
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
    private bool TryAppend(SectionState state, string text, Range range, bool separate)
    {
        PooledText buffer = state.Buffer;
        int length = buffer.Length;
        if (length > 0 && separate)
        {
            buffer.Append("\n");
        }

        buffer.Append(text.AsSpan(range));
        int tokens = _budget.Count(buffer.Span);
        if (tokens <= _budget.Limit)
        {
            state.BufferTokens = tokens;
            return true;
        }

        buffer.Length = length;
        return false;
    }

    private void CarryOverlap(SectionState state, string previous, string text, Range range, bool separate,
        CancellationToken cancellationToken)
    {
        if (_overlap == 0 || text.AsSpan(range).IsWhiteSpace())
        {
            return;
        }

        int start = Math.Clamp(_budget.SuffixIndex(previous, _overlap), 0, previous.Length);
        for (; start < previous.Length; start++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TextBoundaries.IsScalarBoundary(previous, start) || !_splitter.IsOverlapBoundary(previous, start) ||
                previous.AsSpan(start).IsWhiteSpace())
            {
                continue;
            }

            int overlapTokens = _budget.Count(previous.AsSpan(start));
            if (overlapTokens > _overlap)
            {
                continue;
            }

            state.Buffer.Append(previous.AsSpan(start));
            if (TryAppend(state, text, range, separate))
            {
                return;
            }

            state.Buffer.Length = 0;
            state.BufferTokens = 0;
        }
    }

    private static string? Take(SectionState state)
    {
        PooledText buffer = state.Buffer;
        string? text = buffer.Span.IsWhiteSpace() ? null : buffer.ToString();
        buffer.Length = 0;
        state.BufferTokens = 0;
        return text;
    }

    private readonly record struct ProtectedRegion(int Start, int End, MarkdownFence? Fence, MarkdownTable? Table);

    /// <summary>The mutable state shared by the steps that process one section.</summary>
    private sealed class SectionState(IngestionDocument document, PooledText buffer, string? context)
    {
        internal IngestionDocument Document { get; } = document;

        internal PooledText Buffer { get; } = buffer;

        internal string? Context { get; set; } = context;

        internal bool FollowingProse { get; set; }

        /// <summary>Gets or sets the exact token count of <see cref="Buffer"/>; zero when it is empty.</summary>
        internal int BufferTokens { get; set; }

        /// <summary>Gets the prose pieces waiting to join the buffer.</summary>
        internal List<PendingPiece> Pending { get; } = [];

        /// <summary>Gets or sets the sum of the pending pieces' individual token counts.</summary>
        internal int PendingTokens { get; set; }
    }

    private readonly record struct PendingPiece(string Text, Range Range, bool Separate, int Tokens);
}
