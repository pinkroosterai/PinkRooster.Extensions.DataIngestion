using System.Runtime.CompilerServices;
using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>Splits Markdown tables by row windows while re-injecting headers into every continuation chunk.</summary>
public sealed class MarkdownTableChunker : IngestionChunker<string>
{
    private readonly TokenBudget _budget;
    private readonly TableSplitter _tableSplitter;
    private readonly bool _emitNonTableContent;

    /// <summary>Initializes a new instance of the <see cref="MarkdownTableChunker"/> class.</summary>
    /// <param name="options">The chunker configuration and tokenizer.</param>
    public MarkdownTableChunker(TableChunkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Tokenizer);
        ArgumentOutOfRangeException.ThrowIfNegative(options.ReservedHeaderTokens);

        int limit = options.MaxTokensPerChunk - options.ReservedHeaderTokens;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit, nameof(options.ReservedHeaderTokens));

        _budget = new TokenBudget(options.Tokenizer, limit);
        _tableSplitter = new TableSplitter(_budget);
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
            foreach (IngestionChunk<string> chunk in ProcessSection(document, section, [], cancellationToken))
            {
                yield return chunk;
            }
        }
    }

    private IEnumerable<IngestionChunk<string>> ProcessSection(
        IngestionDocument document,
        IngestionDocumentSection section,
        List<(int Level, string Text)> headings,
        CancellationToken cancellationToken)
    {
        string? context = Breadcrumb(headings);

        foreach (IngestionDocumentElement element in section.Elements)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (element is IngestionDocumentHeader header)
            {
                UpdateHeadings(headings, header);
                context = Breadcrumb(headings);
                continue;
            }

            if (element is IngestionDocumentSection child)
            {
                foreach (IngestionChunk<string> chunk in ProcessSection(document, child, [.. headings], cancellationToken))
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
                    MarkdownTable found = MarkdownTable.Find(tableMarkdown, cancellationToken).FirstOrDefault();
                    if (found != default)
                    {
                        foreach (TableChunk part in _tableSplitter.Split(tableMarkdown, found, cancellationToken))
                        {
                            yield return TableSplitter.CreateChunk(part, document, context);
                        }
                    }
                    else
                    {
                        // Fallback: emit table markdown directly if delimiter wasn't parsed
                        yield return new IngestionChunk<string>(tableMarkdown, document, context);
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

                // Check for non-table prose before this table
                if (_emitNonTableContent && mdTable.Start > lastIndex)
                {
                    string prose = markdown[lastIndex..mdTable.Start].Trim();
                    if (!string.IsNullOrWhiteSpace(prose))
                    {
                        foreach (IngestionChunk<string> proseChunk in SliceProse(prose, document, context, cancellationToken))
                        {
                            yield return proseChunk;
                        }
                    }
                }

                // Emit table chunks with repeated headers and metadata
                foreach (TableChunk part in _tableSplitter.Split(markdown, mdTable, cancellationToken))
                {
                    yield return TableSplitter.CreateChunk(part, document, context);
                }

                lastIndex = mdTable.End;
            }

            // Check for trailing non-table prose after the last table
            if (_emitNonTableContent && lastIndex < markdown.Length)
            {
                string trailing = markdown[lastIndex..].Trim();
                if (!string.IsNullOrWhiteSpace(trailing))
                {
                    foreach (IngestionChunk<string> proseChunk in SliceProse(trailing, document, context, cancellationToken))
                    {
                        yield return proseChunk;
                    }
                }
            }
        }
    }

    private IEnumerable<IngestionChunk<string>> SliceProse(
        string prose, IngestionDocument document, string? context, CancellationToken cancellationToken)
    {
        int start = 0;
        while (start < prose.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int remaining = prose.Length - start;
            int sliceLen = TextBoundaries.FindPrefix(_budget, prose.AsSpan(start, remaining), _budget.Limit, cancellationToken);
            if (sliceLen <= 0)
            {
                sliceLen = Math.Min(remaining, _budget.PrefixIndex(prose.AsSpan(start, remaining), _budget.Limit));
                if (sliceLen <= 0)
                {
                    sliceLen = 1;
                }
            }

            yield return new IngestionChunk<string>(prose.Substring(start, sliceLen), document, context);
            start += sliceLen;
        }
    }

    private static void UpdateHeadings(List<(int Level, string Text)> headings, IngestionDocumentHeader header)
    {
        int level = header.Level ?? 1;
        string text = (header.GetMarkdown() ?? string.Empty).TrimStart('#', ' ').Trim();
        headings.RemoveAll(h => h.Level >= level);
        if (!string.IsNullOrWhiteSpace(text))
        {
            headings.Add((level, text));
        }
    }

    private static string? Breadcrumb(List<(int Level, string Text)> headings) =>
        headings.Count == 0 ? null : string.Join(" > ", headings.Select(h => h.Text));
}
