using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

internal sealed class ProtectedChunker(TokenBudget budget)
{
    /// <summary>Margin added to prefix-index character estimates to catch boundary candidates.</summary>
    private const int SearchSlackChars = 32;

    private readonly TableSplitter _tableSplitter = new(budget);

    /// <summary>
    /// Splits a Markdown table found inside some Markdown. An oversized table becomes row windows that each repeat
    /// the header when headers repeat, and lossless slices of its text otherwise.
    /// </summary>
    internal IReadOnlyList<TableChunk> Table(string markdown, MarkdownTable table, bool repeatHeaders,
        CancellationToken cancellationToken)
    {
        if (repeatHeaders || budget.Count(markdown.AsSpan(table.Start, table.End - table.Start)) <= budget.Limit)
        {
            return _tableSplitter.Split(markdown, table, cancellationToken);
        }

        return AsTableChunks(Lossless(markdown, table.Start, table.End, "table", cancellationToken));
    }

    /// <summary>
    /// Splits a document table. Its Markdown is split like any other Markdown table when it parses as one; the cells
    /// are the fallback for Markdown that does not, and lossless slicing the last resort.
    /// </summary>
    internal IReadOnlyList<TableChunk> Table(IngestionDocumentTable table, string markdown, bool repeatHeaders,
        CancellationToken cancellationToken)
    {
        using IEnumerator<MarkdownTable> enumerator = MarkdownTable.Find(markdown, cancellationToken).GetEnumerator();
        if (enumerator.MoveNext())
        {
            MarkdownTable parsed = enumerator.Current;
            if (markdown.AsSpan(0, parsed.Start).IsWhiteSpace() && markdown.AsSpan(parsed.End).IsWhiteSpace())
            {
                return Table(markdown, parsed, repeatHeaders, cancellationToken);
            }
        }

        int dataRows = Math.Max(table.Cells.GetLength(0) - 1, 0);
        if (budget.Count(markdown) <= budget.Limit)
        {
            return [new TableChunk(TableSplitter.WithoutFinalLineBreak(markdown.AsSpan()), 0, false, dataRows > 0 ? 1 : 0, dataRows)];
        }

        if (repeatHeaders && _tableSplitter.Split(table.Cells, cancellationToken) is { } rows)
        {
            return rows;
        }

        return AsTableChunks(Lossless(markdown, 0, markdown.Length, nameof(IngestionDocumentTable), cancellationToken));
    }

    private static TableChunk[] AsTableChunks(string[] parts)
    {
        TableChunk[] chunks = new TableChunk[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            chunks[i] = new TableChunk(parts[i], i, i > 0, 0, 0);
        }

        return chunks;
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

        int wrapperTokens = budget.Count(markdown.AsSpan(fence.Start, fence.BodyStart - fence.Start)) +
            budget.Count(markdown.AsSpan(fence.BodyEnd, fence.End - fence.BodyEnd));
        List<int> lineEnds = [];

        int PreferredEnd(int start)
        {
            int lines = FittingLineEnd(markdown, fence, start, wrapperTokens, lineEnds, Fits, cancellationToken);
            if (lines > start)
            {
                return lines;
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

    /// <summary>
    /// Returns the end of the last whole line of a fence body that fits, starting at <paramref name="start"/>, or
    /// <paramref name="start"/> when not even the first line fits.
    /// </summary>
    private int FittingLineEnd(string markdown, MarkdownFence fence, int start, int wrapperTokens, List<int> lineEnds,
        Func<int, int, bool> fits, CancellationToken cancellationToken)
    {
        lineEnds.Clear();
        int LineEnd(int count)
        {
            while (lineEnds.Count < count)
            {
                int from = lineEnds.Count == 0 ? start : lineEnds[^1];
                if (from >= fence.BodyEnd)
                {
                    return -1;
                }

                lineEnds.Add(MarkdownFence.EndOfLine(markdown, from));
            }

            return lineEnds[count - 1];
        }

        int bodyBudget = budget.Limit - wrapperTokens;
        int reach = bodyBudget > 0
            ? start + budget.PrefixIndex(markdown.AsSpan(start, fence.BodyEnd - start), bodyBudget)
            : start;
        int estimate = 0;
        while (LineEnd(estimate + 1) is int end && end >= 0 && end <= reach)
        {
            cancellationToken.ThrowIfCancellationRequested();
            estimate++;
        }

        int lines = FitSearch.Count(int.MaxValue - 1, estimate, count =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return LineEnd(count) is int end && end >= 0 && fits(start, end);
        });

        return lines > 0 ? LineEnd(lines) : start;
    }

    private string[] Lossless(string text, int start, int end, string element, CancellationToken cancellationToken)
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

    internal List<Range>? Partition(string text, int start, int end, Func<int, int> preferredEnd,
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

            int remaining = end - first;
            int maxChars = budget.PrefixIndex(text.AsSpan(first, remaining), budget.Limit);
            int maxLast = Math.Min(end, first + maxChars + SearchSlackChars);
            while (maxLast < end && !TextBoundaries.IsScalarBoundary(text, maxLast))
            {
                maxLast++;
            }

            for (int last = maxLast; last >= TextBoundaries.NextScalar(text, first); last--)
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
