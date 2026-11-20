using System.Text.Json;
using AwesomeAssertions;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Benchmarks;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Repository.Tests;

public sealed class BenchmarkScoringTests
{
    private static readonly Tokenizer Tokenizer = TokenizerFixture.Instance;
    private static readonly string Seed = Path.Combine(AppContext.BaseDirectory, "Evaluation");

    [Fact]
    public void DuplicatePartialAndWrongSourceMatchesDoNotCompleteEvidence()
    {
        var label = new EvidenceSpan("source", 10, 4, 2, 2);
        var otherLabel = new EvidenceSpan("source", 20, 2, 3, 3);
        var question = new EvaluationQuestion("q", "question", [label, label, otherLabel]);
        var chunks = new[]
        {
            Chunk("other", 0, "abcd", 10),
            Chunk("source", 0, "abcd", 30),
            Chunk("source", 1, "ab", 10),
            Chunk("source", 2, "ab", 10),
            Chunk("source", 3, "ef", 20),
            Chunk("source", 4, "cd", 12),
        };

        var score = Score(question, chunks, 64);
        score.RecallAt5.Should().Be(0.5);
        score.ReciprocalRank.Should().Be(1d / 5);
        score.Evidence.Should().Equal(new ScoredEvidence(label, 6), new ScoredEvidence(otherLabel, 5));

        var onlyLate = Score(question with { Evidence = [label] }, chunks, 64);
        onlyLate.RecallAt5.Should().Be(0);
        onlyLate.ReciprocalRank.Should().Be(1d / 6);
    }

    [Fact]
    public void HeaderConsumesBudgetWithoutReceivingEvidenceCredit()
    {
        const string header = "alpha beta gamma delta\n\n";
        const string body = "A unique evidence phrase";
        var label = new EvidenceSpan("source", 0, body.Length, 1, 1);
        var question = new EvaluationQuestion("q", "question", [label]);
        var raw = Chunk("source", 0, body, 0);
        var enriched = raw with
        {
            Chunk = raw.Chunk with
            {
                Payload = header + body,
                Source = new SourceSlice(header.Length, 0, body.Length),
            },
        };
        int budget = Tokenizer.CountTokens(header, considerNormalization: false);
        Score(question, [raw], budget).RecallAt5.Should().Be(1);
        var result = Score(question, [enriched], budget);
        result.RecallAt5.Should().Be(0);
        result.ReciprocalRank.Should().Be(0);
        result.Tokens.Should().Be(budget);
        result.Chunks.Should().ContainSingle().Which.VerifiedLength.Should().Be(0);
        Score(question, [], budget).Should().BeEquivalentTo(
            new QueryResult("q", 0, 0, 0, [], [new ScoredEvidence(label, null)]));
    }

    [Fact]
    public void StableTiesIgnoreInputEnumerationOrder()
    {
        var chunks = new[]
        {
            Chunk("z", 0, "term", 0).Chunk,
            Chunk("a", 2, "term", 0).Chunk,
            Chunk("a", 1, "term", 0).Chunk,
        };
        Ranking.RankSmoke("TERM", chunks).Select(row => (row.Chunk.SourceId, row.Chunk.Ordinal)).Should()
            .Equal(("a", 1), ("a", 2), ("z", 0));
        Ranking.RankSmoke("TERM", chunks.Reverse()).Should().Equal(Ranking.RankSmoke("TERM", chunks));
    }

    [Fact]
    public void RelativeTargetDistinguishesUnevaluableAndUnreachable()
    {
        var zero = MetricComparison.Compare(0, 1, 0.15);
        zero.RelativeLift.Should().BeNull();
        zero.AbsoluteDifference.Should().Be(1);
        zero.TargetStatus.Should().Be("unevaluable");
        MetricComparison.Compare(0.95, 1, 0.15).TargetStatus.Should().Be("unmet");
        MetricComparison.Compare(0.8, 0.92, 0.15).TargetStatus.Should().Be("met");
        MetricComparison.Compare(0.8, 0.9199, 0.15).TargetStatus.Should().Be("unmet");
        MetricComparison.Compare(0, 0).TargetStatus.Should().Be("diagnostic");
    }

    [Fact]
    public async Task SmokeCommandProducesRepeatableDiagnosticsAndMarksCancellation()
    {
        var token = TestContext.Current.CancellationToken;
        using var first = new StringWriter();
        using var again = new StringWriter();
        using var errors = new StringWriter();
        string[] args = ["smoke", Seed];
        (await BenchmarkConsole.RunAsync(args, first, errors, token)).Should().Be(0);
        (await BenchmarkConsole.RunAsync(args, again, errors, token)).Should().Be(0);
        again.ToString().Should().Be(first.ToString());
        using var output = JsonDocument.Parse(first.ToString());
        output.RootElement.GetProperty("status").GetString().Should().Be("complete");
        output.RootElement.GetProperty("profileFingerprint").GetString().Should().HaveLength(64);
        output.RootElement.GetProperty("corpusFingerprint").GetString().Should().HaveLength(64);
        output.RootElement.GetProperty("suite").GetProperty("variants").GetArrayLength().Should().Be(2);

        using var cancelled = new StringWriter();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        await request.CancelAsync();
        (await BenchmarkConsole.RunAsync(args, cancelled, errors, request.Token)).Should().Be(130);
        using var incomplete = JsonDocument.Parse(cancelled.ToString());
        incomplete.RootElement.GetProperty("status").GetString().Should().Be("incomplete");
        incomplete.RootElement.GetProperty("suite").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static QueryResult Score(EvaluationQuestion question, IEnumerable<RankedChunk> chunks, int budget) =>
        RetrievalScorer.Score(question, chunks, Tokenizer, budget, TestContext.Current.CancellationToken);

    private static RankedChunk Chunk(string source, int ordinal, string text, int offset) =>
        new(new EvaluationChunk(source, ordinal, text, new SourceSlice(0, offset, text.Length)), 0);
}
