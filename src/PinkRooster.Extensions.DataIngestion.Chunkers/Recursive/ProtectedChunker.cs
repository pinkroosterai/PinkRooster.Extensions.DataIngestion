using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

internal sealed class ProtectedChunker(TokenBudget budget)
{
    private readonly TableSplitter _tableSplitter = new(budget);

    internal IReadOnlyList<TableChunk> Table(string markdown, MarkdownTable table, CancellationToken cancellationToken) =>
        _tableSplitter.Split(markdown, table, cancellationToken);

    internal IReadOnlyList<string> Table(IngestionDocumentTable table, string markdown, CancellationToken cancellationToken)
    {
        if (budget.Count(markdown) <= budget.Limit)
        {
            return [markdown];
        }

        return TryTableRows(table.Cells, cancellationToken) ??
            Lossless(markdown, 0, markdown.Length, nameof(IngestionDocumentTable), cancellationToken);
    }

    internal IEnumerable<string> Fence(string markdown, MarkdownFence fence, CancellationToken cancellationToken)
    {
        if (budget.Count(markdown.AsSpan(fence.Start, fence.End - fence.Start)) <= budget.Limit)
        {
            yield return markdown[fence.Start..fence.End];
            yield break;
        }

        using PooledText buffer = new PooledText();
        buffer.Append(markdown.AsSpan(fence.Start, fence.BodyStart - fence.Start));
        int openingLength = buffer.Length;
        string newline = buffer.Span.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        bool Fits(int start, int end)
        {
            if (SplitsLineEnding(start) || SplitsLineEnding(end))
            {
                return false;
            }

            // Subdividing a source line can expose a fence delimiter at its new start or end.
            if (fence.WouldCloseEarly(markdown.AsSpan(start, end - start)))
            {
                return false;
            }

            Wrap(start, end);
            return budget.Count(buffer.Span) <= budget.Limit;
        }

        bool SplitsLineEnding(int index) =>
            index > fence.BodyStart && index < fence.BodyEnd && markdown[index - 1] == '\r' && markdown[index] == '\n';

        void Wrap(int start, int end)
        {
            buffer.Length = openingLength;
            buffer.Append(markdown.AsSpan(start, end - start));
            if (end > start && markdown[end - 1] != '\n')
            {
                buffer.Append(newline);
            }

            buffer.Append(markdown.AsSpan(fence.BodyEnd, fence.End - fence.BodyEnd));
        }

        int PreferredEnd(int start)
        {
            int lastLine = start;
            while (lastLine < fence.BodyEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int end = MarkdownFence.EndOfLine(markdown, lastLine);
                if (!Fits(start, end))
                {
                    break;
                }

                lastLine = end;
            }

            if (lastLine > start)
            {
                return lastLine;
            }

            int lineEnd = MarkdownFence.EndOfLine(markdown, start);
            int candidate = start + budget.PrefixIndex(markdown.AsSpan(start, lineEnd - start), budget.Limit);
            for (int end = Math.Min(lineEnd, candidate); end > start; end--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TextBoundaries.IsScalarBoundary(markdown, end) && Fits(start, end))
                {
                    return end;
                }
            }

            return start;
        }

        List<Range>? ranges = fence.BodyStart == fence.BodyEnd
            ? null
            : Partition(markdown, fence.BodyStart, fence.BodyEnd, PreferredEnd, Fits, cancellationToken);
        if (ranges is null)
        {
            foreach (string part in Lossless(markdown, fence.Start, fence.End, "fenced code", cancellationToken))
            {
                yield return part;
            }

            yield break;
        }

        foreach (Range range in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Wrap(range.Start.Value, range.End.Value);
            yield return buffer.ToString();
        }
    }

    private List<string>? TryTableRows(IngestionDocumentElement?[,] cells, CancellationToken cancellationToken)
    {
        int rows = cells.GetLength(0);
        int columns = cells.GetLength(1);
        if (rows < 2 || columns == 0)
        {
            return null;
        }

        using PooledText buffer = new PooledText();
        AppendRow(buffer, cells, 0, cancellationToken);
        buffer.Append("\n|");
        for (int i = 0; i < columns; i++)
        {
            buffer.Append(" --- |");
        }

        int headerLength = buffer.Length;
        List<string> chunks = [];
        for (int row = 1; row < rows; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int beforeRow = buffer.Length;
            buffer.Append("\n");
            AppendRow(buffer, cells, row, cancellationToken);
            if (budget.Count(buffer.Span) <= budget.Limit)
            {
                continue;
            }

            if (beforeRow == headerLength)
            {
                return null;
            }

            buffer.Length = beforeRow;
            chunks.Add(buffer.ToString());
            buffer.Length = headerLength;
            buffer.Append("\n");
            AppendRow(buffer, cells, row, cancellationToken);
            if (budget.Count(buffer.Span) > budget.Limit)
            {
                return null;
            }
        }

        chunks.Add(buffer.ToString());
        return chunks;
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

    private IReadOnlyList<string> Lossless(string text, int start, int end, string element, CancellationToken cancellationToken)
    {
        ReadOnlySpan<char> span = text.AsSpan(start, end - start);
        int trimmedLength = span.TrimEnd().Length;
        if (trimmedLength == 0)
        {
            return [];
        }

        int effectiveEnd = start + trimmedLength;
        bool Fits(int first, int last) =>
            !text.AsSpan(first, last - first).IsWhiteSpace() &&
            budget.Count(text.AsSpan(first, last - first)) <= budget.Limit;

        int PreferredEnd(int first) =>
            first + TextBoundaries.FindPrefix(budget, text.AsSpan(first, effectiveEnd - first), budget.Limit, cancellationToken);

        List<Range> ranges = Partition(text, start, effectiveEnd, PreferredEnd, Fits, cancellationToken)
            ?? throw TextBoundaries.NoFit(element, budget.Limit);

        if (effectiveEnd < end && ranges.Count > 0)
        {
            ranges[^1] = ranges[^1].Start..end;
        }

        return ranges.Select(range => text[range]).ToArray();
    }

    private static List<Range>? Partition(string text, int start, int end, Func<int, int> preferredEnd,
        Func<int, int, bool> fits, CancellationToken cancellationToken)
    {
        List<Range> ranges = [];
        int current = start;
        while (current < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int next = preferredEnd(current);
            if (next <= current)
            {
                break;
            }

            ranges.Add(current..next);
            current = next;
        }

        if (current == end)
        {
            return ranges;
        }

        // A greedy split may strand blank text or choose a prefix that prevents a later
        // fit. Check reachability at scalar boundaries before declaring the block impossible.
        int[] nextBoundary = new int[end - start + 1];
        nextBoundary[^1] = end;
        for (int first = end - 1; first >= start; first--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TextBoundaries.IsScalarBoundary(text, first))
            {
                continue;
            }

            for (int last = end; last >= TextBoundaries.NextScalar(text, first); last--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TextBoundaries.IsScalarBoundary(text, last) && nextBoundary[last - start] != 0 && fits(first, last))
                {
                    nextBoundary[first - start] = last;
                    break;
                }
            }
        }

        if (nextBoundary[0] == 0)
        {
            return null;
        }

        ranges.Clear();
        for (current = start; current < end; current = nextBoundary[current - start])
        {
            ranges.Add(current..nextBoundary[current - start]);
        }

        return ranges;
    }
}
