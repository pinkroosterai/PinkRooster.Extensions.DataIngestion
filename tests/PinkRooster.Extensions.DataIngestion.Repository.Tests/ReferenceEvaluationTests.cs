using AwesomeAssertions;
using PinkRooster.Extensions.DataIngestion.Benchmarks;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Repository.Tests;

public sealed class ReferenceEvaluationTests
{
    private static readonly string ReferenceCorpus = Path.Combine(AppContext.BaseDirectory, "Evaluation", "reference");

    [Fact]
    public async Task ReferenceEvaluationProducesReportsAndAchievesRetrievalLift()
    {
        var token = TestContext.Current.CancellationToken;
        var report = await ReferenceEvaluation.RunAsync(ReferenceCorpus, token);

        report.Status.Should().Be("complete");
        report.Profile.Should().Be("reference");
        report.Suite.Should().NotBeNull();

        var suite = report.Suite!;
        suite.SourceCount.Should().Be(7);
        suite.QuestionCount.Should().Be(16);
        suite.Variants.Should().HaveCount(4);
        suite.Comparisons.Should().HaveCount(2);

        // Prose comparison: RecursiveTokenChunker vs DocumentTokenChunker
        var proseComparison = suite.Comparisons[0];
        proseComparison.Baseline.Should().Be("DocumentTokenChunker");
        proseComparison.Candidate.Should().Be("RecursiveTokenChunker");
        proseComparison.RecallAt5.Candidate.Should().BeGreaterThanOrEqualTo(proseComparison.RecallAt5.Baseline);
        proseComparison.MeanReciprocalRank.RelativeLift.Should().NotBeNull();
        proseComparison.MeanReciprocalRank.RelativeLift.GetValueOrDefault().Should().BeGreaterThanOrEqualTo(0.15);

        // C# comparison: RoslynCodeChunker vs DocumentTokenChunker
        var csharpComparison = suite.Comparisons[1];
        csharpComparison.Baseline.Should().Be("DocumentTokenChunker");
        csharpComparison.Candidate.Should().Be("RoslynCodeChunker");
        csharpComparison.RecallAt5.Candidate.Should().BeGreaterThanOrEqualTo(csharpComparison.RecallAt5.Baseline);
        csharpComparison.MeanReciprocalRank.Candidate.Should().BeGreaterThanOrEqualTo(csharpComparison.MeanReciprocalRank.Baseline);
    }
}
