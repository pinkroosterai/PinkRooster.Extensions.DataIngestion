using System.Runtime.CompilerServices;
using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>Splits Markdown tables by row windows while re-injecting headers into every continuation chunk.</summary>
public sealed class MarkdownTableChunker : IngestionChunker<string>
{
    private readonly TokenBudget _budget;
    private readonly ProtectedChunker _tables;
    private readonly bool _emitNonTableContent;

    /// <summary>Initializes a new instance of the <see cref="MarkdownTableChunker"/> class.</summary>
    /// <param name="options">
    /// The chunker configuration and tokenizer. <see cref="IngestionChunkerOptions.OverlapTokens"/> does not apply:
    /// table parts repeat the header instead, and prose between tables is sliced without overlap.
    /// </param>
    public MarkdownTableChunker(TableChunkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Tokenizer);
        ArgumentOutOfRangeException.ThrowIfNegative(options.ReservedHeaderTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(options.OverlapTokens);

        int limit = options.MaxTokensPerChunk - options.ReservedHeaderTokens;
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ReservedHeaderTokens,
                $"{nameof(options.ReservedHeaderTokens)} must be less than {nameof(options.MaxTokensPerChunk)}.");
        }


        _budget = new TokenBudget(options.Tokenizer, limit);
        _tables = new ProtectedChunker(_budget);
        _emitNonTableContent = options.EmitNonTableContent;
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
        IngestionDocument document,
        IngestionDocumentSection section,
        HeadingTrail headings,
        CancellationToken cancellationToken)
    {
        string? context = headings.Breadcrumb;

        foreach (IngestionDocumentElement element in section.Elements)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (element is IngestionDocumentHeader header)
            {
                headings.Open(header);
                context = headings.Breadcrumb;
                continue;
            }

            if (element is IngestionDocumentSection child)
            {
                foreach (IngestionChunk<string> chunk in ProcessSection(document, child, headings.Clone(), cancellationToken))
                {
                    yield return chunk;
                }

                continue;
            }

            if (element is IngestionDocumentTable table)
            {
                string? tableMarkdown = table.GetMarkdown();
                if (!string.IsNullOrWhiteSpace(tableMarkdown))
                {
                    foreach (TableChunk part in _tables.Table(table, tableMarkdown, repeatHeaders: true, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return TableSplitter.CreateChunk(part, document, context);
                    }
                }

                continue;
            }

            string? markdown = element.GetMarkdown();
            if (string.IsNullOrWhiteSpace(markdown))
            {
                continue;
            }

            int lastIndex = 0;
            foreach (MarkdownTable mdTable in MarkdownTable.Find(markdown, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (IngestionChunk<string> proseChunk in SliceProse(markdown, lastIndex, mdTable.Start, document, context, cancellationToken))
                {
                    yield return proseChunk;
                }

                foreach (TableChunk part in _tables.Table(markdown, mdTable, repeatHeaders: true, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return TableSplitter.CreateChunk(part, document, context);
                }

                lastIndex = mdTable.End;
            }

            foreach (IngestionChunk<string> proseChunk in SliceProse(markdown, lastIndex, markdown.Length, document, context, cancellationToken))
            {
                yield return proseChunk;
            }
        }
    }

    /// <summary>Slices the trimmed prose in <c>markdown[start..end]</c>, allocating only the emitted chunk strings.</summary>
    private IEnumerable<IngestionChunk<string>> SliceProse(
        string markdown, int start, int end, IngestionDocument document, string? context, CancellationToken cancellationToken)
    {
        if (!_emitNonTableContent || end <= start)
        {
            yield break;
        }

        ReadOnlySpan<char> span = markdown.AsSpan(start, end - start);
        int leading = span.Length - span.TrimStart().Length;
        start += leading;
        end = start + span.Trim().Length;
        while (start < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sliceLen = TextBoundaries.ForcedPrefix(_budget, markdown.AsSpan(start, end - start), _budget.Limit, cancellationToken);
            yield return new IngestionChunk<string>(markdown.Substring(start, sliceLen), document, context);
            start += sliceLen;
        }
    }
}
