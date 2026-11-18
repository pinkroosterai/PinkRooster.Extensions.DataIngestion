using Microsoft.ML.Tokenizers;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

internal readonly struct TokenBudget(Tokenizer tokenizer, int limit)
{
    internal int Limit { get; } = limit;

    internal int Count(ReadOnlySpan<char> text) =>
        tokenizer.CountTokens(text, considerNormalization: false);

    internal int PrefixIndex(ReadOnlySpan<char> text, int maxTokens) =>
        tokenizer.GetIndexByTokenCount(text, maxTokens, out _, out _, considerNormalization: false);

    internal int SuffixIndex(ReadOnlySpan<char> text, int maxTokens) =>
        tokenizer.GetIndexByTokenCountFromEnd(text, maxTokens, out _, out _, considerNormalization: false);
}
