namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>Represents the byte/character offsets and structure of a Markdown pipe table.</summary>
internal readonly record struct MarkdownTable(
    int Start,
    int HeaderStart,
    int HeaderEnd,
    int DelimiterStart,
    int DelimiterEnd,
    int RowsStart,
    int End,
    int ColumnCount,
    int RowCount)
{
    internal bool IsHeaderOnly => RowCount == 0;

    /// <summary>Scans text for GFM-compliant pipe tables outside of code fences.</summary>
    internal static IEnumerable<MarkdownTable> Find(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        // Build sorted list of code fences to avoid detecting tables inside code blocks
        List<MarkdownFence> fences = [];
        foreach (MarkdownFence fence in MarkdownFence.Find(text, cancellationToken))
        {
            fences.Add(fence);
        }

        int start = 0;
        while (start < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip any region covered by a code fence
            int fenceIndex = FindEnclosingFence(fences, start);
            if (fenceIndex >= 0)
            {
                start = fences[fenceIndex].End;
                continue;
            }

            int line1End = MarkdownFence.EndOfLine(text, start);
            ReadOnlySpan<char> line1 = text.AsSpan(start, line1End - start).Trim();

            // A table header must contain at least one pipe and not be a fence or heading
            if (!line1.Contains('|') || line1.StartsWith('#') || line1.StartsWith('`') || line1.StartsWith('~'))
            {
                start = line1End;
                continue;
            }

            // Check if line 2 is a valid delimiter line
            if (line1End >= text.Length)
            {
                break;
            }

            int line2End = MarkdownFence.EndOfLine(text, line1End);
            ReadOnlySpan<char> line2 = text.AsSpan(line1End, line2End - line1End).Trim();

            int colCount = CountDelimiterColumns(line2);
            if (colCount <= 0)
            {
                start = line1End;
                continue;
            }

            // Valid table found! Now collect data rows until empty line or non-table line
            int rowsStart = line2End;
            int current = rowsStart;
            int rowCount = 0;

            while (current < text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // If current line enters a code fence, table ends
                if (FindEnclosingFence(fences, current) >= 0)
                {
                    break;
                }

                int rowEnd = MarkdownFence.EndOfLine(text, current);
                ReadOnlySpan<char> rowLine = text.AsSpan(current, rowEnd - current);
                ReadOnlySpan<char> trimmedRow = rowLine.Trim();

                if (trimmedRow.IsEmpty || !trimmedRow.Contains('|') || trimmedRow.StartsWith('#') ||
                    trimmedRow.StartsWith('`') || trimmedRow.StartsWith('~'))
                {
                    break;
                }

                rowCount++;
                current = rowEnd;
            }

            int tableEnd = current;
            yield return new MarkdownTable(start, start, line1End, line1End, line2End, rowsStart, tableEnd, colCount, rowCount);

            start = tableEnd;
        }
    }

    /// <summary>Returns the slice ranges of all data rows within this table.</summary>
    internal IReadOnlyList<Range> GetRowRanges(string text)
    {
        if (RowCount == 0 || RowsStart >= End)
        {
            return [];
        }

        List<Range> ranges = new(RowCount);
        int current = RowsStart;
        while (current < End)
        {
            int lineEnd = MarkdownFence.EndOfLine(text, current);
            int effectiveEnd = Math.Min(lineEnd, End);
            ranges.Add(new Range(current, effectiveEnd));
            current = effectiveEnd;
        }

        return ranges;
    }

    /// <summary>Validates whether a line matches GFM table delimiter syntax and counts the columns.</summary>
    internal static int CountDelimiterColumns(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty || !line.Contains('-') || !line.Contains('|'))
        {
            return 0;
        }

        ReadOnlySpan<char> trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        int columns = 0;
        while (!trimmed.IsEmpty)
        {
            int nextPipe = FindUnescapedPipe(trimmed);
            ReadOnlySpan<char> cell = nextPipe < 0 ? trimmed : trimmed[..nextPipe];
            if (!IsValidDelimiterCell(cell))
            {
                return 0;
            }

            columns++;
            if (nextPipe < 0)
            {
                break;
            }

            trimmed = trimmed[(nextPipe + 1)..];
        }

        return columns;
    }

    private static bool IsValidDelimiterCell(ReadOnlySpan<char> cell)
    {
        ReadOnlySpan<char> trimmed = cell.Trim();
        if (trimmed.IsEmpty)
        {
            return false;
        }

        if (trimmed.StartsWith(':'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith(':'))
        {
            trimmed = trimmed[..^1];
        }

        if (trimmed.IsEmpty)
        {
            return false;
        }

        foreach (char ch in trimmed)
        {
            if (ch != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static int FindUnescapedPipe(ReadOnlySpan<char> span)
    {
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == '|')
            {
                int backslashes = 0;
                for (int j = i - 1; j >= 0 && span[j] == '\\'; j--)
                {
                    backslashes++;
                }

                if (backslashes % 2 == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int FindEnclosingFence(List<MarkdownFence> fences, int index)
    {
        for (int i = 0; i < fences.Count; i++)
        {
            if (index >= fences[i].Start && index < fences[i].End)
            {
                return i;
            }
        }

        return -1;
    }
}
