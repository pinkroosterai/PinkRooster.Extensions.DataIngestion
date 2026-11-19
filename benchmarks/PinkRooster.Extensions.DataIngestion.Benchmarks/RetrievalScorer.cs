using Microsoft.ML.Tokenizers;

namespace PinkRooster.Extensions.DataIngestion.Benchmarks;

internal sealed record RetainedChunk(int Rank, string SourceId, int ChunkOrdinal, double Score, int Tokens,
    int? SourceOffset, int VerifiedLength);

internal sealed record ScoredEvidence(EvidenceSpan Evidence, int? RecoveredAtRank);

internal sealed record QueryResult(string QuestionId, double RecallAt5, double ReciprocalRank, int Tokens,
    IReadOnlyList<RetainedChunk> Chunks, IReadOnlyList<ScoredEvidence> Evidence);

internal static class RetrievalScorer
{
    internal static QueryResult Score(EvaluationQuestion question, IEnumerable<RankedChunk> ranking, Tokenizer tokenizer,
        int tokenBudget, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokenBudget);
        cancellationToken.ThrowIfCancellationRequested();
        var evidence = question.Evidence.Distinct().ToDictionary(span => span, _ => (int?)null);
        if (evidence.Count == 0)
        {
            throw new InvalidDataException($"Question '{question.Id}' has no evidence.");
        }

        var retained = new List<RetainedChunk>();
        int tokens = 0;
        foreach (var result in ranking)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = result.Chunk;
            int length = PrefixLength(chunk.Payload, tokenizer, tokenBudget - tokens, out int usedTokens);
            bool truncated = length < chunk.Payload.Length;
            if (length == 0 && truncated)
            {
                break;
            }

            int verifiedLength = chunk.Source is { } span
                ? Math.Clamp(length - span.PayloadOffset, 0, span.Length)
                : 0;
            int rank = retained.Count + 1;
            var verified = new RetainedChunk(rank, chunk.SourceId, chunk.Ordinal, result.Score, usedTokens,
                verifiedLength == 0 ? null : chunk.Source?.SourceOffset, verifiedLength);
            retained.Add(verified);
            tokens += usedTokens;

            if (verifiedLength > 0)
            {
                foreach (var label in evidence.Keys)
                {
                    if (label.SourceId == chunk.SourceId && evidence[label] is null && Covers(label, retained))
                    {
                        evidence[label] = rank;
                    }
                }
            }

            if (truncated || tokens >= tokenBudget)
            {
                break;
            }
        }

        double recall = (double)evidence.Values.Count(rank => rank is <= 5) / evidence.Count;
        int? firstRelevantRank = evidence.Values.Min();
        double reciprocalRank = firstRelevantRank is int first ? 1d / first : 0;
        return new QueryResult(question.Id, recall, reciprocalRank, tokens, retained,
            evidence.Select(result => new ScoredEvidence(result.Key, result.Value)).ToArray());
    }

    private static bool Covers(EvidenceSpan evidence, IEnumerable<RetainedChunk> chunks)
    {
        int cursor = evidence.Offset;
        int end = evidence.Offset + evidence.Length;
        var relevant = chunks.Where(chunk => chunk.SourceId == evidence.SourceId && chunk.SourceOffset.HasValue)
            .OrderBy(chunk => chunk.SourceOffset);
        foreach (var chunk in relevant)
        {
            int start = chunk.SourceOffset.GetValueOrDefault();
            if (start > cursor)
            {
                return false;
            }

            cursor = Math.Max(cursor, start + chunk.VerifiedLength);
            if (cursor >= end)
            {
                return true;
            }
        }

        return false;
    }

    private static int PrefixLength(string payload, Tokenizer tokenizer, int budget, out int tokens)
    {
        tokens = tokenizer.CountTokens(payload, considerNormalization: false);
        if (tokens <= budget)
        {
            return payload.Length;
        }

        int length = tokenizer.GetIndexByTokenCount(payload, budget, out _, out _, considerNormalization: false);
        while (length > 0)
        {
            if (length < payload.Length && char.IsSurrogatePair(payload[length - 1], payload[length]))
            {
                length--;
            }

            tokens = tokenizer.CountTokens(payload.AsSpan(0, length), considerNormalization: false);
            if (tokens <= budget)
            {
                return length;
            }

            length--;
        }

        tokens = 0;
        return 0;
    }
}
