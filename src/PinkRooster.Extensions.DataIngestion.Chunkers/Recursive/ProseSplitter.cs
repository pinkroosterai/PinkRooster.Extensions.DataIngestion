namespace PinkRooster.Extensions.DataIngestion.Chunkers;

internal sealed class ProseSplitter(
    TokenBudget budget, string[] separators, SeparatorPlacement placement, bool useDefaults)
{
    internal IEnumerable<Range> Split(string text, string element, CancellationToken cancellationToken) =>
        Split(text, 0, text.Length, 0, element, cancellationToken);

    internal bool IsOverlapBoundary(ReadOnlySpan<char> text, int start)
    {
        if (start == 0 || char.IsWhiteSpace(text[start - 1]) || char.IsWhiteSpace(text[start]))
        {
            return true;
        }

        foreach (string separator in separators)
        {
            if (separator.Length != 0 &&
                (text[start..].StartsWith(separator, StringComparison.Ordinal) ||
                 text[..start].EndsWith(separator, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerable<Range> Split(
        string text, int start, int end, int level, string element, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (start == end)
        {
            yield break;
        }

        if (text.AsSpan(start, end - start).IsWhiteSpace() || budget.Count(text.AsSpan(start, end - start)) <= budget.Limit)
        {
            yield return start..end;
            yield break;
        }

        if (level == separators.Length || separators[level].Length == 0)
        {
            while (start < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (text.AsSpan(start, end - start).IsWhiteSpace())
                {
                    yield return start..end;
                    yield break;
                }

                int length = TextBoundaries.FindPrefix(budget, text.AsSpan(start, end - start),
                    budget.Limit, cancellationToken);
                if (length == 0)
                {
                    throw TextBoundaries.NoFit(element, budget.Limit);
                }

                yield return start..(start + length);
                start += length;
            }

            yield break;
        }

        string separator = separators[level];
        int previous = start;
        int search = start;
        while (search < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (int index, int length) = FindSeparator(text, start, search, end, separator);
            if (index < 0)
            {
                break;
            }

            int splitEnd = placement == SeparatorPlacement.Trailing ? index + length : index;
            foreach (Range fragment in Split(text, previous, splitEnd, level + 1, element, cancellationToken))
            {
                yield return fragment;
            }

            previous = placement == SeparatorPlacement.Leading ? index : index + length;
            search = index + length;
        }

        foreach (Range fragment in Split(text, previous, end, level + 1, element, cancellationToken))
        {
            yield return fragment;
        }
    }

    private (int Index, int Length) FindSeparator(string text, int start, int search, int end, string separator)
    {
        if (useDefaults && separator is "\n" or "\n\n")
        {
            return FindLineBoundary(text, start, search, end, separator.Length == 2);
        }

        while (search < end)
        {
            int offset = text.AsSpan(search, end - search).IndexOf(separator, StringComparison.Ordinal);
            if (offset < 0)
            {
                break;
            }

            int index = search + offset;
            if (!useDefaults || separator != ". " || !IsAbbreviation(text.AsSpan(start, index - start + 1)))
            {
                if (TextBoundaries.IsScalarBoundary(text, index) &&
                    TextBoundaries.IsScalarBoundary(text, index + separator.Length))
                {
                    return (index, separator.Length);
                }
            }

            search = index + 1;
        }

        return (-1, 0);
    }

    private static (int Index, int Length) FindLineBoundary(
        string text, int start, int search, int end, bool paragraph)
    {
        for (int i = search; i < end; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            int first = i > start && text[i - 1] == '\r' ? i - 1 : i;
            if (!paragraph)
            {
                return (first, i + 1 - first);
            }

            int next = i + 1;
            if (next < end && text[next] == '\r')
            {
                next++;
            }

            if (next < end && text[next] == '\n')
            {
                return (first, next + 1 - first);
            }
        }

        return (-1, 0);
    }

    private static bool IsAbbreviation(ReadOnlySpan<char> text) =>
        text.EndsWith("e.g.", StringComparison.OrdinalIgnoreCase) ||
        text.EndsWith("i.e.", StringComparison.OrdinalIgnoreCase) ||
        text.EndsWith("Dr.", StringComparison.OrdinalIgnoreCase) ||
        text.EndsWith("Mr.", StringComparison.OrdinalIgnoreCase);
}
