namespace PinkRooster.Extensions.DataIngestion.Benchmarks;

internal readonly record struct SourceSlice(int PayloadOffset, int SourceOffset, int Length);

internal static class SourceAttribution
{
    // Only pass a range that the caller knows came from the original chunk body.
    internal static SourceSlice? Match(EvaluationSource source, string payload, int bodyOffset, int length)
    {
        ReadOnlySpan<char> body = payload.AsSpan(bodyOffset, length);
        if (body.IsWhiteSpace())
        {
            return null;
        }

        ReadOnlySpan<char> original = source.Text.AsSpan();
        int offset = original.IndexOf(body, StringComparison.Ordinal);
        if (offset < 0 || original[(offset + 1)..].IndexOf(body, StringComparison.Ordinal) >= 0)
        {
            return null;
        }

        return new SourceSlice(bodyOffset, offset, length);
    }
}
