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
            chunks.Add(new TableChunk(markdown[table.Start..table.End], 0, false, table.RowCount > 0 ? 1 : 0, table.RowCount));
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
                int sliceLen = TextBoundaries.FindPrefix(budget, markdown.AsSpan(start, remaining), budget.Limit, cancellationToken);
                if (sliceLen <= 0)
                {
                    sliceLen = Math.Min(remaining, budget.PrefixIndex(markdown.AsSpan(start, remaining), budget.Limit));
                    if (sliceLen <= 0)
                    {
                        sliceLen = 1;
                    }
                }

                chunks.Add(new TableChunk(markdown[start..(start + sliceLen)], part++, part > 1, 0, 0));
                start += sliceLen;
            }

            return chunks;
        }

        if (table.RowCount == 0)
        {
            chunks.Add(new TableChunk(headerText, 0, false, 0, 0));
            return chunks;
        }

        IReadOnlyList<Range> rows = table.GetRowRanges(markdown);
        using PooledText buffer = new PooledText();
        buffer.Append(headerText);
        int headerLength = buffer.Length;
        int partIndex = 0;
        int startRowIndex = 1;
        int lastRowIndex = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Range range = rows[i];
            int rowStart = range.Start.Value;
            int rowLength = range.End.Value - rowStart;

            int beforeRowLength = buffer.Length;
            buffer.Append(markdown.AsSpan(rowStart, rowLength));
            if (buffer.Span[^1] != '\n')
            {
                buffer.Append("\n");
            }

            if (budget.Count(buffer.Span) <= budget.Limit)
            {
                lastRowIndex = i + 1;
                continue;
            }

            // Row exceeded budget when appended
            if (beforeRowLength > headerLength)
            {
                buffer.Length = beforeRowLength;
                chunks.Add(new TableChunk(buffer.ToString(), partIndex, partIndex > 0, startRowIndex, lastRowIndex));
                partIndex++;

                buffer.Length = headerLength;
                startRowIndex = i + 1;
                buffer.Append(markdown.AsSpan(rowStart, rowLength));
                if (buffer.Span[^1] != '\n')
                {
                    buffer.Append("\n");
                }

                if (budget.Count(buffer.Span) <= budget.Limit)
                {
                    lastRowIndex = i + 1;
                    continue;
                }
            }

            // Single row exceeds budget even when alone with header: isolate and slice it gracefully
            buffer.Length = headerLength;
            SliceOversizedRow(chunks, headerText, headerTokens, markdown, rowStart, rowLength, ref partIndex, i + 1, cancellationToken);

            startRowIndex = i + 2;
            lastRowIndex = i + 1;
        }

        if (buffer.Length > headerLength)
        {
            chunks.Add(new TableChunk(buffer.ToString(), partIndex, partIndex > 0, startRowIndex, rows.Count));
        }

        return chunks;
    }

    internal static IngestionChunk<string> CreateChunk(
        TableChunk chunk,
        IngestionDocument document,
        string? context = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        IngestionChunk<string> ingestionChunk = new(chunk.Content, document, context);
        ingestionChunk.Metadata["IsTablePart"] = true;
        ingestionChunk.Metadata["IsTableContinuation"] = chunk.IsContinuation;
        ingestionChunk.Metadata["TablePartIndex"] = chunk.PartIndex;
        ingestionChunk.Metadata["TableRowRange"] = chunk.StartRow > 0 ? $"{chunk.StartRow}-{chunk.EndRow}" : "0-0";
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
            int len = TextBoundaries.FindPrefix(budget, markdown.AsSpan(currentStart, remainingLength), budget.Limit, cancellationToken);
            if (len <= 0)
            {
                len = Math.Min(remainingLength, budget.PrefixIndex(markdown.AsSpan(currentStart, remainingLength), budget.Limit));
                if (len <= 0)
                {
                    len = 1;
                }
            }

            chunks.Add(new TableChunk(markdown[currentStart..(currentStart + len)], partIndex, partIndex > 0, rowNumber, rowNumber));
            partIndex++;
            currentStart += len;
            remainingLength -= len;
        }
    }
}
