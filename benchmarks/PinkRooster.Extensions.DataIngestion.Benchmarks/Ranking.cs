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
        {
            var terms = Terms(chunk.Payload);
            if (!string.IsNullOrEmpty(chunk.Context))
            {
                terms.UnionWith(Terms(chunk.Context));
            }

            return new RankedChunk(chunk, terms.Count(queryTerms.Contains));
        }));
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "in", "on", "at", "to", "for", "with", "by", "from", "of", "as",
        "is", "are", "was", "were", "be", "been", "being", "have", "has", "had", "do", "does", "did",
        "how", "what", "when", "where", "which", "who", "whom", "this", "that", "these", "those", "it", "its"
    };

    private static HashSet<string> Terms(string text)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Word().Matches(text))
        {
            if (!StopWords.Contains(match.Value))
            {
                terms.Add(match.Value);
            }

            foreach (Match sub in IdentifierPart().Matches(match.Value))
            {
                if (sub.Length > 1 && !StopWords.Contains(sub.Value))
                {
                    terms.Add(sub.Value);
                }
            }
        }

        return terms;
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}_]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z][a-z]+|[0-9]+|[a-z]+")]
    private static partial Regex IdentifierPart();
}
