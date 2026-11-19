using System.Reflection;
using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace PinkRooster.Extensions.DataIngestion.Benchmarks;

internal static class SmokeEvaluation
{
    internal const string Profile = "smoke";

    private static readonly BenchmarkSettings Settings = new(
        Profile, "single LF-normalized source paragraph per fixture", "gpt-4o (o200k_base)", ChunkTokens: 64,
        OverlapTokens: 0, PreserveAtomicElements: false, RetrievalTokens: 256,
        TokenAccounting: "sum of token counts of each retained payload, with normalization disabled",
        Ranking: "distinct case-insensitive terms shared with the query", TieBreak: "ordinal source ID, then chunk ordinal");

    internal static async Task<BenchmarkReport> RunAsync(string corpusRoot, CancellationToken cancellationToken)
    {
        var corpus = CorpusLoader.Load(corpusRoot, cancellationToken);
        var tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
        var baselineChunks = new List<EvaluationChunk>();
        var recursiveChunks = new List<EvaluationChunk>();
        var baseline = SmokeChunking.Baseline(tokenizer, Settings.ChunkTokens);
        var recursive = SmokeChunking.Recursive(tokenizer, Settings.ChunkTokens);

        foreach (var source in corpus.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = SmokeChunking.Read(source);
            baselineChunks.AddRange(await SmokeChunking.ChunkAsync(source, document, baseline, cancellationToken));
            recursiveChunks.AddRange(await SmokeChunking.ChunkAsync(source, document, recursive, cancellationToken));
        }

        var baselineResult = Evaluate(nameof(Microsoft.Extensions.DataIngestion.Chunkers.DocumentTokenChunker),
            baselineChunks, corpus, tokenizer, cancellationToken);
        var recursiveResult = Evaluate(nameof(Chunkers.RecursiveTokenChunker), recursiveChunks, corpus, tokenizer,
            cancellationToken);
        var comparison = new VariantComparison(baselineResult.Variant, recursiveResult.Variant,
            MetricComparison.Compare(baselineResult.RecallAt5, recursiveResult.RecallAt5),
            MetricComparison.Compare(baselineResult.MeanReciprocalRank, recursiveResult.MeanReciprocalRank));
        var suite = new SuiteResult(corpus.Sources.Count, corpus.Questions.Count, [baselineResult, recursiveResult],
            [comparison]);
        string? revision = typeof(SmokeEvaluation).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return new BenchmarkReport("complete", Profile, CorpusLoader.Digest(JsonSerializer.Serialize(Settings)),
            corpus.Fingerprint, revision, Settings, suite);
    }

    private static VariantResult Evaluate(string name, IReadOnlyList<EvaluationChunk> chunks, EvaluationCorpus corpus,
        Tokenizer tokenizer, CancellationToken cancellationToken)
    {
        var results = corpus.Questions.Select(question => RetrievalScorer.Score(question,
            Ranking.RankSmoke(question.Prompt, chunks), tokenizer, Settings.RetrievalTokens, cancellationToken)).ToArray();
        return new VariantResult(name, results.Average(result => result.RecallAt5),
            results.Average(result => result.ReciprocalRank), results);
    }
}
