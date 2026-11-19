using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Chunkers;
using PinkRooster.Extensions.DataIngestion.CodeAnalysis;

namespace PinkRooster.Extensions.DataIngestion.Benchmarks;

/// <summary>
/// Evaluation runner for the reference corpus comparing baseline chunking against extended chunkers.
/// </summary>
internal static class ReferenceEvaluation
{
    internal const string Profile = "reference";

    internal static async Task<BenchmarkReport> RunAsync(string referenceRoot, CancellationToken cancellationToken = default)
    {
        var tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
        const int maxChunkTokens = 256;
        const int maxRetrievalTokens = 1280;

        // Prose suite: DocumentTokenChunker vs RecursiveTokenChunker
        string prosePath = Path.Combine(referenceRoot, "prose");
        var proseCorpus = CorpusLoader.Load(prosePath, cancellationToken);
        var baselineProseChunks = new List<EvaluationChunk>();
        var recursiveProseChunks = new List<EvaluationChunk>();

        var baselineProse = SmokeChunking.Baseline(tokenizer, maxChunkTokens);
        var recursiveProse = new RecursiveTokenChunker(new RecursiveChunkerOptions(tokenizer)
        {
            MaxTokensPerChunk = maxChunkTokens,
            ReservedHeaderTokens = 16,
            OverlapTokens = 0,
            PreserveAtomicElements = true,
        });

        foreach (var source in proseCorpus.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var doc = await ReadDocumentAsync(source, cancellationToken);
            baselineProseChunks.AddRange(await SmokeChunking.ChunkAsync(source, doc, baselineProse, cancellationToken));
            recursiveProseChunks.AddRange(await SmokeChunking.ChunkAsync(source, doc, recursiveProse, cancellationToken));
        }

        var baselineProseResult = EvaluateVariant("DocumentTokenChunker", baselineProseChunks, proseCorpus, tokenizer, maxRetrievalTokens, cancellationToken);
        var recursiveProseResult = EvaluateVariant("RecursiveTokenChunker", recursiveProseChunks, proseCorpus, tokenizer, maxRetrievalTokens, cancellationToken);

        // CSharp suite: DocumentTokenChunker vs RoslynCodeChunker
        string csharpPath = Path.Combine(referenceRoot, "csharp");
        var csharpCorpus = CorpusLoader.Load(csharpPath, cancellationToken);
        var baselineCodeChunks = new List<EvaluationChunk>();
        var roslynCodeChunks = new List<EvaluationChunk>();

        var baselineCode = SmokeChunking.Baseline(tokenizer, maxChunkTokens);
        var roslynCode = new RoslynCodeChunker(new RoslynChunkerOptions(tokenizer)
        {
            MaxTokensPerChunk = maxChunkTokens,
            OverlapTokens = 0,
            IncludeEnclosingTypeSignature = true,
        });

        foreach (var source in csharpCorpus.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var doc = await ReadDocumentAsync(source, cancellationToken);
            baselineCodeChunks.AddRange(await SmokeChunking.ChunkAsync(source, doc, baselineCode, cancellationToken));
            roslynCodeChunks.AddRange(await SmokeChunking.ChunkAsync(source, doc, roslynCode, cancellationToken));
        }

        var baselineCodeResult = EvaluateVariant("DocumentTokenChunker", baselineCodeChunks, csharpCorpus, tokenizer, maxRetrievalTokens, cancellationToken);
        var roslynCodeResult = EvaluateVariant("RoslynCodeChunker", roslynCodeChunks, csharpCorpus, tokenizer, maxRetrievalTokens, cancellationToken);

        var proseComparison = new VariantComparison(baselineProseResult.Variant, recursiveProseResult.Variant,
            MetricComparison.Compare(baselineProseResult.RecallAt5, recursiveProseResult.RecallAt5),
            MetricComparison.Compare(baselineProseResult.MeanReciprocalRank, recursiveProseResult.MeanReciprocalRank));

        var csharpComparison = new VariantComparison(baselineCodeResult.Variant, roslynCodeResult.Variant,
            MetricComparison.Compare(baselineCodeResult.RecallAt5, roslynCodeResult.RecallAt5),
            MetricComparison.Compare(baselineCodeResult.MeanReciprocalRank, roslynCodeResult.MeanReciprocalRank));

        var suite = new SuiteResult(
            proseCorpus.Sources.Count + csharpCorpus.Sources.Count,
            proseCorpus.Questions.Count + csharpCorpus.Questions.Count,
            [baselineProseResult, recursiveProseResult, baselineCodeResult, roslynCodeResult],
            [proseComparison, csharpComparison]);

        var settings = new BenchmarkSettings(
            Profile, "Technical prose and C# source suites", "gpt-4o (o200k_base)", maxChunkTokens,
            0, true, maxRetrievalTokens,
            "sum of token counts of each retained payload", "distinct case-insensitive terms shared with the query",
            "ordinal source ID, then chunk ordinal");

        string? revision = typeof(ReferenceEvaluation).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return new BenchmarkReport("complete", Profile, CorpusLoader.Digest(JsonSerializer.Serialize(settings)),
            proseCorpus.Fingerprint + "-" + csharpCorpus.Fingerprint, revision, settings, suite);
    }

    private static async Task<IngestionDocument> ReadDocumentAsync(EvaluationSource source, CancellationToken cancellationToken)
    {
        if (source.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var reader = new SourceCodeDocumentReader();
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(source.Text));
            return await reader.ReadAsync(stream, source.Id, "text/x-csharp", cancellationToken);
        }

        var document = new IngestionDocument(source.Id);
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph(source.Text));
        document.Sections.Add(section);
        return document;
    }

    private static VariantResult EvaluateVariant(string name, IReadOnlyList<EvaluationChunk> chunks,
        EvaluationCorpus corpus, Tokenizer tokenizer, int maxRetrievalTokens, CancellationToken cancellationToken)
    {
        var results = corpus.Questions.Select(question => RetrievalScorer.Score(question,
            Ranking.RankSmoke(question.Prompt, chunks), tokenizer, maxRetrievalTokens, cancellationToken)).ToArray();
        return new VariantResult(name, results.Average(r => r.RecallAt5), results.Average(r => r.ReciprocalRank), results);
    }
}
