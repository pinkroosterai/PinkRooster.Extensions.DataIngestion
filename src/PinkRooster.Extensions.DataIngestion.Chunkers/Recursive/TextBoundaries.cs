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
        if (text.IsEmpty || limit <= 0)
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
        for (int end = NextScalar(text, 0); end <= text.Length; end = NextScalar(text, end))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (end > candidate && !text[..end].IsWhiteSpace() && budget.Count(text[..end]) <= limit)
            {
                return end;
            }
        }

        return 0;
    }

    internal static InvalidOperationException NoFit(string element, int limit) =>
        new($"Cannot split {element} into nonblank chunks within the effective token budget of {limit}.");
}
