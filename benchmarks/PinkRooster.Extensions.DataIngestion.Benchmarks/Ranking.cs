using System.Text.RegularExpressions;

namespace PinkRooster.Extensions.DataIngestion.Benchmarks;

internal sealed record RankedChunk(EvaluationChunk Chunk, double Score);

internal static partial class Ranking
{
    internal static IEnumerable<RankedChunk> InStableOrder(IEnumerable<RankedChunk> chunks) =>
        chunks.OrderByDescending(result => result.Score)
            .ThenBy(result => result.Chunk.SourceId, StringComparer.Ordinal)
            .ThenBy(result => result.Chunk.Ordinal);

    internal static IEnumerable<RankedChunk> RankSmoke(string prompt, IEnumerable<EvaluationChunk> chunks)
    {
        var queryTerms = Terms(prompt);
        return InStableOrder(chunks.Select(chunk =>
            new RankedChunk(chunk, Terms(chunk.Payload).Count(queryTerms.Contains))));
    }

    private static HashSet<string> Terms(string text) =>
        Word().Matches(text).Select(match => match.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"[\p{L}\p{Nd}_]+")]
    private static partial Regex Word();
}
