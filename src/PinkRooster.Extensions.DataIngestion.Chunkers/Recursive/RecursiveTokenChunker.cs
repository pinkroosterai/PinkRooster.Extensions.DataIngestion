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

    /// <summary>Initializes the chunker with a validated snapshot of its configuration.</summary>
    /// <param name="options">The chunker configuration, including its tokenizer.</param>
    public RecursiveTokenChunker(RecursiveChunkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Tokenizer);
        ArgumentOutOfRangeException.ThrowIfNegative(options.ReservedHeaderTokens);
        int limit = options.MaxTokensPerChunk - options.ReservedHeaderTokens;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit, nameof(options.ReservedHeaderTokens));
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
            throw new ArgumentOutOfRangeException(nameof(options.KeepSeparator));
        }

        _budget = new TokenBudget(options.Tokenizer, limit);
        _splitter = new ProseSplitter(_budget, separators, options.KeepSeparator,
            separators.SequenceEqual(RecursiveChunkerOptions.DefaultSeparators, StringComparer.Ordinal));
        _overlap = options.OverlapTokens;
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
            foreach (IngestionChunk<string> chunk in ProcessSection(document, section, [], cancellationToken))
            {
                yield return chunk;
            }
        }
    }

    private IEnumerable<IngestionChunk<string>> ProcessSection(
        IngestionDocument document, IngestionDocumentSection section,
        List<(int Level, string Text)> headings, CancellationToken cancellationToken)
    {
        using PooledText buffer = new PooledText();
        string? context = Breadcrumb(headings);
        bool followingProse = false;
        foreach (IngestionDocumentElement element in section.Elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element is IngestionDocumentHeader header)
            {
                if (Take(buffer) is { } text)
                {
                    yield return new IngestionChunk<string>(text, document, context);
                }

                cancellationToken.ThrowIfCancellationRequested();
                UpdateHeadings(headings, header);
                context = Breadcrumb(headings);
                followingProse = false;
                continue;
            }

            if (element is IngestionDocumentSection child)
            {
                if (Take(buffer) is { } text)
                {
                    yield return new IngestionChunk<string>(text, document, context);
                }

                foreach (IngestionChunk<string> chunk in ProcessSection(document, child, [.. headings], cancellationToken))
                {
                    yield return chunk;
                }

                followingProse = false;
                continue;
            }

            string? markdown = element.GetMarkdown();
            if (string.IsNullOrWhiteSpace(markdown))
            {
                continue;
            }

            if (_protectedChunker is not null && element is IngestionDocumentTable table)
            {
                if (Take(buffer) is { } text)
                {
                    yield return new IngestionChunk<string>(text, document, context);
                }

                cancellationToken.ThrowIfCancellationRequested();
                foreach (string chunk in _protectedChunker.Table(table, markdown, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new IngestionChunk<string>(chunk, document, context);
                }

                followingProse = false;
                continue;
            }

            string elementName = element.GetType().Name;
            int start = 0;
            if (_protectedChunker is not null)
            {
                foreach (MarkdownFence fence in MarkdownFence.Find(markdown, cancellationToken))
                {
                    foreach (string chunk in AddProse(buffer, markdown, start, fence.Start, elementName,
                                 followingProse, cancellationToken))
                    {
                        yield return new IngestionChunk<string>(chunk, document, context);
                    }

                    if (Take(buffer) is { } text)
                    {
                        yield return new IngestionChunk<string>(text, document, context);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (string chunk in _protectedChunker.Fence(markdown, fence, cancellationToken))
                    {
                        yield return new IngestionChunk<string>(chunk, document, context);
                    }

                    start = fence.End;
                    followingProse = false;
                }
            }

            if (!markdown.AsSpan(start).IsWhiteSpace())
            {
                foreach (string chunk in AddProse(buffer, markdown, start, markdown.Length, elementName,
                             followingProse, cancellationToken))
                {
                    yield return new IngestionChunk<string>(chunk, document, context);
                }

                followingProse = true;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (Take(buffer) is { } final)
        {
            yield return new IngestionChunk<string>(final, document, context);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private IEnumerable<string> AddProse(
        PooledText buffer, string text, int start, int end, string element, bool separate, CancellationToken cancellationToken)
    {
        foreach (Range range in _splitter.Split(text, start, end, element, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryAppend(buffer, text, range, separate))
            {
                if (Take(buffer) is { } previous)
                {
                    yield return previous;
                    cancellationToken.ThrowIfCancellationRequested();
                    CarryOverlap(buffer, previous, text, range, separate, cancellationToken);
                }

                if (buffer.Length == 0)
                {
                    _ = TryAppend(buffer, text, range, separate: false);
                }
            }

            separate = false;
        }
    }

    private bool TryAppend(PooledText buffer, string text, Range range, bool separate)
    {
        int length = buffer.Length;
        if (length > 0 && separate)
        {
            buffer.Append("\n");
        }

        buffer.Append(text.AsSpan(range));
        if (_budget.Count(buffer.Span) <= _budget.Limit)
        {
            return true;
        }

        buffer.Length = length;
        return false;
    }

    private void CarryOverlap(PooledText buffer, string previous, string text, Range range, bool separate,
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
                previous.AsSpan(start).IsWhiteSpace() || _budget.Count(previous.AsSpan(start)) > _overlap)
            {
                continue;
            }

            buffer.Append(previous.AsSpan(start));
            if (TryAppend(buffer, text, range, separate))
            {
                return;
            }

            buffer.Length = 0;
        }
    }

    private static string? Take(PooledText buffer)
    {
        string? text = buffer.Span.IsWhiteSpace() ? null : buffer.ToString();
        buffer.Length = 0;
        return text;
    }

    private static string? Breadcrumb(List<(int Level, string Text)> headings) =>
        headings.Count == 0 ? null : string.Join(" > ", headings.Select(heading => heading.Text));

    private static void UpdateHeadings(List<(int Level, string Text)> headings, IngestionDocumentHeader header)
    {
        int level = header.Level ?? int.MaxValue;
        while (headings.Count > 0 && headings[^1].Level >= level)
        {
            headings.RemoveAt(headings.Count - 1);
        }

        headings.Add((level, header.GetMarkdown()));
    }
}
