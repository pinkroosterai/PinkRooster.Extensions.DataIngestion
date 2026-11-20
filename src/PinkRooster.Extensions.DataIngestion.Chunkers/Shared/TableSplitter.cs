using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>Internal representation of a chunk produced from a Markdown table.</summary>
internal readonly record struct TableChunk(
    string Content,
    int PartIndex,
    bool IsContinuation,
    int StartRow,
    int EndRow);

/// <summary>Splits Markdown tables by row windows while re-injecting headers and delimiter rows.</summary>
internal sealed class TableSplitter(TokenBudget budget)
{
    internal IReadOnlyList<TableChunk> Split(string markdown, MarkdownTable table, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        cancellationToken.ThrowIfCancellationRequested();

        List<TableChunk> chunks = [];
        int tableLength = table.End - table.Start;
        if (budget.Count(markdown.AsSpan(table.Start, tableLength)) <= budget.Limit)
        {
            chunks.Add(new TableChunk(WithoutFinalLineBreak(markdown.AsSpan(table.Start, tableLength)), 0, false,
                table.RowCount > 0 ? 1 : 0, table.RowCount));
            return chunks;
        }

        string headerText = markdown[table.HeaderStart..table.DelimiterEnd];
        if (!headerText.EndsWith('\n'))
        {
            headerText += "\n";
        }

        int headerTokens = budget.Count(headerText);
        if (headerTokens > budget.Limit)
        {
            // If the header itself exceeds budget limit, fall back to span slicing without header duplication
            int start = table.Start;
            int part = 0;
            while (start < table.End)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int remaining = table.End - start;
                int sliceLen = TextBoundaries.ForcedPrefix(budget, markdown.AsSpan(start, remaining), budget.Limit, cancellationToken);

                chunks.Add(new TableChunk(markdown[start..(start + sliceLen)], part++, part > 1, 0, 0));
                start += sliceLen;
            }

            return chunks;
        }

        if (table.RowCount == 0)
        {
            chunks.Add(new TableChunk(WithoutFinalLineBreak(headerText), 0, false, 0, 0));
            return chunks;
        }

        IReadOnlyList<Range> rows = table.GetRowRanges(markdown);
        using PooledText buffer = new PooledText();
        buffer.Append(headerText);
        int headerLength = buffer.Length;
        int partIndex = 0;
        int first = 0;
        while (first < rows.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start = rows[first].Start.Value;
            int rowBudget = budget.Limit - headerTokens;
            int reach = rowBudget > 0 ? start + budget.PrefixIndex(markdown.AsSpan(start, table.End - start), rowBudget) : start;
            int estimate = 0;
            while (first + estimate < rows.Count && rows[first + estimate].End.Value <= reach)
            {
                estimate++;
            }

            int window = first;
            int fit = FitSearch.Count(rows.Count - first, estimate, count =>
            {
                FillRows(buffer, headerLength, markdown, rows, window, count);
                return budget.Count(buffer.Span) <= budget.Limit;
            });

            if (fit == 0)
            {
                // A single row exceeds the budget even beside the header: isolate and slice it.
                buffer.Length = headerLength;
                Range row = rows[first];
                SliceOversizedRow(chunks, headerText, headerTokens, markdown, row.Start.Value,
                    row.End.Value - row.Start.Value, ref partIndex, first + 1, cancellationToken);
                first++;
                continue;
            }

            FillRows(buffer, headerLength, markdown, rows, first, fit);
            chunks.Add(new TableChunk(WithoutFinalLineBreak(buffer.Span), partIndex, partIndex > 0, first + 1, first + fit));
            partIndex++;
            first += fit;
        }

        return chunks;
    }

    /// <summary>Resets the buffer to the header and appends <paramref name="count"/> rows, each ending a line.</summary>
    private static void FillRows(PooledText buffer, int headerLength, string markdown, IReadOnlyList<Range> rows,
        int first, int count)
    {
        buffer.Length = headerLength;
        for (int i = first; i < first + count; i++)
        {
            buffer.Append(markdown.AsSpan(rows[i]));
            if (buffer.Span[^1] != '\n')
            {
                buffer.Append("\n");
            }
        }
    }

    private static string WithoutFinalLineBreak(ReadOnlySpan<char> text) => text.TrimEnd("\r\n").ToString();

    /// <summary>Creates the chunk for one table part, with the table metadata every chunker emits.</summary>
    internal static IngestionChunk<string> CreateChunk(
        TableChunk chunk,
        IngestionDocument document,
        string? context = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        IngestionChunk<string> ingestionChunk = new(chunk.Content, document, context);
        ingestionChunk.Metadata[ChunkMetadataKeys.IsTablePart] = true;
        ingestionChunk.Metadata[ChunkMetadataKeys.IsTableContinuation] = chunk.IsContinuation;
        ingestionChunk.Metadata[ChunkMetadataKeys.TablePartIndex] = chunk.PartIndex;
        ingestionChunk.Metadata[ChunkMetadataKeys.TableRowRange] = chunk.StartRow > 0 ? $"{chunk.StartRow}-{chunk.EndRow}" : "0-0";
        return ingestionChunk;
    }

    private void SliceOversizedRow(
        List<TableChunk> chunks,
        string headerText,
        int headerTokens,
        string markdown,
        int rowStart,
        int rowLength,
        ref int partIndex,
        int rowNumber,
        CancellationToken cancellationToken)
    {
        int availableForPrefix = budget.Limit - headerTokens;
        int currentStart = rowStart;
        int remainingLength = rowLength;

        if (availableForPrefix > 0)
        {
            int prefixLen = TextBoundaries.FindPrefix(budget, markdown.AsSpan(currentStart, remainingLength), availableForPrefix, cancellationToken);
            if (prefixLen > 0)
            {
                chunks.Add(new TableChunk(string.Concat(headerText, markdown.AsSpan(currentStart, prefixLen)), partIndex, partIndex > 0, rowNumber, rowNumber));
                partIndex++;
                currentStart += prefixLen;
                remainingLength -= prefixLen;
            }
        }

        while (remainingLength > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int len = TextBoundaries.ForcedPrefix(budget, markdown.AsSpan(currentStart, remainingLength), budget.Limit, cancellationToken);

            chunks.Add(new TableChunk(markdown[currentStart..(currentStart + len)], partIndex, partIndex > 0, rowNumber, rowNumber));
            partIndex++;
            currentStart += len;
            remainingLength -= len;
        }
    }
}
