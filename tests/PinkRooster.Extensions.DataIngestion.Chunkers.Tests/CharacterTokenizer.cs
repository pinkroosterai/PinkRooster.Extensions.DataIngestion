using System.Buffers;
using Microsoft.ML.Tokenizers;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

/// <summary>
/// One token per character, with a prefix index that can be made to under-report, so tests can exercise the code
/// that treats a tokenizer's reported boundary as advisory.
/// </summary>
internal sealed class CharacterTokenizer(bool underReportPrefix) : Tokenizer
{
    protected override EncodeResults<EncodedToken> EncodeToTokens(string? text, ReadOnlySpan<char> textSpan, EncodeSettings settings)
    {
        ReadOnlySpan<char> span = text is null ? textSpan : text.AsSpan();
        List<EncodedToken> tokens = new(span.Length);
        for (int i = 0; i < span.Length; i++)
        {
            tokens.Add(new EncodedToken(span[i], span[i].ToString(), new Range(i, i + 1)));
        }

        return new EncodeResults<EncodedToken> { Tokens = tokens, CharsConsumed = span.Length };
    }

    protected override int CountTokens(string? text, ReadOnlySpan<char> textSpan, EncodeSettings settings) =>
        text?.Length ?? textSpan.Length;

    protected override int GetIndexByTokenCount(string? text, ReadOnlySpan<char> textSpan, EncodeSettings settings,
        bool fromEnd, out string? normalizedText, out int tokenCount)
    {
        int length = text?.Length ?? textSpan.Length;
        normalizedText = null;
        tokenCount = Math.Min(settings.MaxTokenCount, length);
        if (fromEnd)
        {
            return length - tokenCount;
        }

        return underReportPrefix ? tokenCount / 2 : tokenCount;
    }

    public override OperationStatus Decode(IEnumerable<int> ids, Span<char> destination, out int idsConsumed, out int charsWritten)
    {
        idsConsumed = 0;
        charsWritten = 0;
        foreach (int id in ids)
        {
            if (charsWritten == destination.Length)
            {
                return OperationStatus.DestinationTooSmall;
            }

            destination[charsWritten++] = (char)id;
            idsConsumed++;
        }

        return OperationStatus.Done;
    }
}
