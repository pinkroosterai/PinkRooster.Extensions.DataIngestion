namespace PinkRooster.Extensions.DataIngestion.Benchmarks;

internal sealed record BenchmarkSettings(string Profile, string DocumentRepresentation, string Tokenizer, int ChunkTokens,
    int OverlapTokens, bool PreserveAtomicElements, int RetrievalTokens, string TokenAccounting, string Ranking, string TieBreak);

internal sealed record BenchmarkReport(string Status, string Profile, string? ProfileFingerprint = null,
    string? CorpusFingerprint = null, string? Revision = null, BenchmarkSettings? Settings = null,
    SuiteResult? Suite = null, string? Reason = null);

internal sealed record SuiteResult(int SourceCount, int QuestionCount, IReadOnlyList<VariantResult> Variants,
    IReadOnlyList<VariantComparison> Comparisons);

internal sealed record VariantResult(string Variant, double RecallAt5, double MeanReciprocalRank,
    IReadOnlyList<QueryResult> Questions);

internal sealed record VariantComparison(string Baseline, string Candidate, MetricComparison RecallAt5,
    MetricComparison MeanReciprocalRank);

internal sealed record MetricComparison(double Baseline, double Candidate, double AbsoluteDifference, double? RelativeLift,
    string TargetStatus)
{
    internal static MetricComparison Compare(double baseline, double candidate, double? target = null)
    {
        double difference = candidate - baseline;
        double? relativeLift = baseline == 0 ? null : difference / baseline;
        string status = target switch
        {
            null => "diagnostic",
            _ when relativeLift is null => "unevaluable",
            double required when candidate >= baseline + baseline * required => "met",
            _ => "unmet",
        };

        return new MetricComparison(baseline, candidate, difference, relativeLift, status);
    }
}
