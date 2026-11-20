using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

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

        string headerText = markdown[table.Start..table.DelimiterEnd];
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

                chunks.Add(new TableChunk(markdown[start..(start + sliceLen)], part, part > 0, 0, 0));
                part++;
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
                SliceOversizedRow(chunks, headerText, headerTokens, markdown, row, ref partIndex, first + 1, cancellationToken);
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

    internal IReadOnlyList<TableChunk>? Split(IngestionDocumentElement?[,] cells, CancellationToken cancellationToken)
    {
        int rows = cells.GetLength(0);
        int columns = cells.GetLength(1);
        if (rows < 2 || columns == 0 || !HasAnyCell(cells))
        {
            return null;
        }

        string markdown = FormatTable(cells, cancellationToken);
        using IEnumerator<MarkdownTable> enumerator = MarkdownTable.Find(markdown, cancellationToken).GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return null;
        }

        return Split(markdown, enumerator.Current, cancellationToken);
    }

    private static bool HasAnyCell(IngestionDocumentElement?[,] cells)
    {
        foreach (IngestionDocumentElement? cell in cells)
        {
            if (cell is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatTable(IngestionDocumentElement?[,] cells, CancellationToken cancellationToken)
    {
        using PooledText buffer = new();
        AppendRow(buffer, cells, 0, cancellationToken);
        buffer.Append("\n|");
        int columns = cells.GetLength(1);
        for (int i = 0; i < columns; i++)
        {
            buffer.Append(" --- |");
        }

        int rows = cells.GetLength(0);
        for (int r = 1; r < rows; r++)
        {
            buffer.Append("\n");
            AppendRow(buffer, cells, r, cancellationToken);
        }

        return buffer.ToString();
    }

    private static void AppendRow(PooledText buffer, IngestionDocumentElement?[,] cells, int row,
        CancellationToken cancellationToken)
    {
        buffer.Append("|");
        for (int column = 0; column < cells.GetLength(1); column++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buffer.Append(" ");
            if (cells[row, column] is { } cell)
            {
                AppendCell(buffer, cell.GetMarkdown());
            }

            buffer.Append(" |");
        }
    }

    private static void AppendCell(PooledText buffer, ReadOnlySpan<char> cell)
    {
        int start = 0;
        int escapes = 0;
        for (int i = 0; i < cell.Length; i++)
        {
            char ch = cell[i];
            if (ch == '|' && escapes % 2 == 0)
            {
                buffer.Append(cell[start..i]);
                buffer.Append("\\");
                start = i;
            }
            else if (ch is '\r' or '\n')
            {
                buffer.Append(cell[start..i]);
                buffer.Append("<br />");
                if (ch == '\r' && i + 1 < cell.Length && cell[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }

            escapes = ch == '\\' ? escapes + 1 : 0;
        }

        buffer.Append(cell[start..]);
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

    internal static string WithoutFinalLineBreak(ReadOnlySpan<char> text) => text.TrimEnd("\r\n").ToString();

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
        ingestionChunk.Metadata[ChunkMetadataKeys.TableRowRange] = TableRowRange.Format(chunk.StartRow, chunk.EndRow);
        return ingestionChunk;
    }

    private void SliceOversizedRow(
        List<TableChunk> chunks,
        string headerText,
        int headerTokens,
        string markdown,
        Range row,
        ref int partIndex,
        int rowNumber,
        CancellationToken cancellationToken)
    {
        int availableForPrefix = budget.Limit - headerTokens;
        int currentStart = row.Start.Value;
        int rowEnd = row.End.Value;

        if (availableForPrefix > 0)
        {
            int remaining = rowEnd - currentStart;
            int prefixLen = TextBoundaries.FindPrefix(budget, markdown.AsSpan(currentStart, remaining), availableForPrefix, cancellationToken);
            if (prefixLen > 0)
            {
                chunks.Add(new TableChunk(string.Concat(headerText, markdown.AsSpan(currentStart, prefixLen)), partIndex, partIndex > 0, rowNumber, rowNumber));
                partIndex++;
                currentStart += prefixLen;
            }
        }

        while (currentStart < rowEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int remaining = rowEnd - currentStart;
            int sliceLen = TextBoundaries.ForcedPrefix(budget, markdown.AsSpan(currentStart, remaining), budget.Limit, cancellationToken);

            chunks.Add(new TableChunk(markdown[currentStart..(currentStart + sliceLen)], partIndex, partIndex > 0, rowNumber, rowNumber));
            partIndex++;
            currentStart += sliceLen;
        }
    }
}
