using System.Diagnostics;
using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Chunkers;
using PinkRooster.Extensions.DataIngestion.Samples.Formatting;

namespace PinkRooster.Extensions.DataIngestion.Samples.Scenarios;

/// <summary>
/// Showcases <see cref="HierarchicalTokenChunker"/> for dual-granularity small-to-big retrieval pipelines.
/// Emits large contextual parent chunks linked to small high-precision child chunks via deterministic metadata.
/// </summary>
public sealed class HierarchicalChunkerScenario : ISampleScenario
{
    private readonly Tokenizer _tokenizer;

    /// <summary>
    /// Initializes a new instance of <see cref="HierarchicalChunkerScenario"/>.
    /// </summary>
    /// <param name="tokenizer">The tokenizer to use for token counting.</param>
    public HierarchicalChunkerScenario(Tokenizer tokenizer)
    {
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
    }

    /// <inheritdoc/>
    public string Name => "hierarchical";

    /// <inheritdoc/>
    public string Description =>
        "Dual-granularity parent-child chunking: large contextual parent chunks linked to small precision child chunks via deterministic metadata.";

    /// <inheritdoc/>
    public async Task<ScenarioResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        ConsoleFormatter.PrintScenarioHeader(Name, Description);

        var options = new HierarchicalChunkerOptions(_tokenizer)
        {
            ParentMaxTokens = 100,
            OverlapTokens = 8,
            MaxTokensPerChunk = 36,
            EmissionMode = HierarchicalEmissionMode.Interleaved,
        };

        var chunker = new HierarchicalTokenChunker(options);
        var document = CreateDocument();

        var chunks = new List<IngestionChunk<string>>();
        int totalTokens = 0;
        int chunkIndex = 0;

        await foreach (var chunk in chunker.ProcessAsync(document, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            chunks.Add(chunk);
            chunkIndex++;

            int tokens = _tokenizer.CountTokens(chunk.Content, considerNormalization: false);
            totalTokens += tokens;

            int ceiling = (string)chunk.Metadata[ChunkMetadataKeys.ChunkRole] == ChunkMetadataKeys.ParentRole ? options.ParentMaxTokens : options.MaxTokensPerChunk;
            ConsoleFormatter.PrintChunk(Name, chunkIndex, chunk, tokens, ceiling);
        }

        stopwatch.Stop();
        var result = new ScenarioResult(Name, chunks.Count, totalTokens, chunks);
        ConsoleFormatter.PrintScenarioSummary(result, stopwatch.Elapsed);
        return result;
    }

    private static IngestionDocument CreateDocument()
    {
        var doc = new IngestionDocument("telemetry-pipeline-spec");
        var section = new IngestionDocumentSection();

        section.Elements.Add(new IngestionDocumentHeader("Edge Telemetry Ingestion Architecture") { Level = 1 });
        section.Elements.Add(new IngestionDocumentParagraph(
            "The edge telemetry ingestion pipeline streams continuous time-series metrics from IoT gateway clusters into central analytical storage. " +
            "Payloads undergo schema validation, deduplication against a Redis sliding window buffer, and compression before persistent ledger write."));

        section.Elements.Add(new IngestionDocumentHeader("High-Availability Replication Matrix") { Level = 2 });
        section.Elements.Add(new IngestionDocumentParagraph(
            "Primary consensus uses Raft leader leases across three fault domains. When network partitions isolate an edge gateway, " +
            "local storage spools backpressure events onto NVMe ring buffers until quorum reconnection is re-established."));

        doc.Sections.Add(section);
        return doc;
    }
}
