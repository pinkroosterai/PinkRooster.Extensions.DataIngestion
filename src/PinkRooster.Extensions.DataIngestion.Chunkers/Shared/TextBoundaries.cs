namespace PinkRooster.Extensions.DataIngestion.Chunkers;

internal static class TextBoundaries
{
    internal static int NextScalar(ReadOnlySpan<char> text, int index) =>
        index + (index + 1 < text.Length && char.IsSurrogatePair(text[index], text[index + 1]) ? 2 : 1);

    internal static bool IsScalarBoundary(ReadOnlySpan<char> text, int index) =>
        index == 0 || index == text.Length || !char.IsSurrogatePair(text[index - 1], text[index]);

    internal static int FindPrefix(
        TokenBudget budget, ReadOnlySpan<char> text, int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0 || text.IsWhiteSpace())
        {
            return 0;
        }

        int candidate = Math.Clamp(budget.PrefixIndex(text, limit), 0, text.Length);
        for (int end = candidate; end > 0; end--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsScalarBoundary(text, end) && !text[..end].IsWhiteSpace() && budget.Count(text[..end]) <= limit)
            {
                return end;
            }

            if (text[..end].IsWhiteSpace())
            {
                break;
            }
        }

        // A tokenizer's reported boundary is advisory. A longer prefix can tokenize
        // differently or include the first nonblank scalar after leading whitespace.
        int best = 0;
        for (int end = NextScalar(text, 0); end <= text.Length; end = NextScalar(text, end))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (end > candidate && !text[..end].IsWhiteSpace())
            {
                if (budget.Count(text[..end]) <= limit)
                {
                    best = end;
                }
                else
                {
                    break;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Returns the longest prefix that fits <paramref name="limit"/> via token search; if none fits, returns the
    /// estimated character prefix from <see cref="TokenBudget.PrefixIndex"/> aligned to a scalar boundary; if even that
    /// is empty, returns the first scalar alone so callers always advance without splitting a surrogate pair.
    /// </summary>
    internal static int ForcedPrefix(
        TokenBudget budget, ReadOnlySpan<char> text, int limit, CancellationToken cancellationToken)
    {
        int length = FindPrefix(budget, text, limit, cancellationToken);
        if (length > 0)
        {
            return length;
        }

        int prefix = Math.Min(text.Length, budget.PrefixIndex(text, limit));
        while (prefix > 0 && !IsScalarBoundary(text, prefix))
        {
            prefix--;
        }

        return prefix > 0 ? prefix : Math.Min(text.Length, NextScalar(text, 0));
    }

    internal static InvalidOperationException NoFit(string element, int limit) =>
        new($"Cannot split {element} into nonblank chunks within the effective token budget of {limit}.");
}
