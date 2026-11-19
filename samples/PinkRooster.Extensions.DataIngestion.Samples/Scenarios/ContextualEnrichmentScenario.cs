using System.Diagnostics;
using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Chunkers;
using PinkRooster.Extensions.DataIngestion.Samples.Formatting;

namespace PinkRooster.Extensions.DataIngestion.Samples.Scenarios;

/// <summary>
/// Showcases <see cref="ContextualChunkProcessor"/> performing Anthropic-style contextual retrieval,
/// prompt-cache alignment, stepped bucket token capping, and selective chunk filtering.
/// </summary>
public sealed class ContextualEnrichmentScenario : ISampleScenario
{
    private readonly Tokenizer _tokenizer;

    /// <summary>
    /// Initializes a new instance of <see cref="ContextualEnrichmentScenario"/>.
    /// </summary>
    /// <param name="tokenizer">The tokenizer for prompt budgeting and embedding limits.</param>
    public ContextualEnrichmentScenario(Tokenizer tokenizer)
    {
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
    }

    /// <inheritdoc/>
    public string Name => "contextual";

    /// <inheritdoc/>
    public string Description =>
        "Document-aware contextual retrieval, prompt-cache alignment, token budget capping, and selective chunk filtering.";

    /// <inheritdoc/>
    public async Task<ScenarioResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        ConsoleFormatter.PrintScenarioHeader(Name, Description);

        // 1. Configure the recursive chunker with token headroom for contextual headers
        var recursiveOptions = new RecursiveChunkerOptions(_tokenizer)
        {
            MaxTokensPerChunk = 64,
            ReservedHeaderTokens = 16, // Reserves 16 tokens so downstream contextual headers never overflow
            OverlapTokens = 4,
            PreserveAtomicElements = true,
        };
        var chunker = new RecursiveTokenChunker(recursiveOptions);

        // 2. Configure the contextual chunk processor with prompt cache hooks and selective filtering
        var chatClient = new SampleContextualChatClient();
        var contextualOptions = new ContextualChunkerOptions(chatClient, _tokenizer)
        {
            EmbeddingTokenizer = _tokenizer,
            MaxEnrichedTokens = 96,
            MaxDocumentContextTokens = 512,
            BucketOverlapTokens = 32,
            MaxDegreeOfParallelism = 2,
            FailureMode = ContextualFailureMode.FallbackToOriginalChunk,
            // Skip enrichment for Markdown tables to minimize LLM token costs
            ChunkFilter = chunk => !chunk.Content.StartsWith('|'),
            ConfigureCacheControl = (messages, options) =>
            {
                // Attach prompt cache metadata demonstrating Anthropic/OpenAI prefix caching alignment
                if (messages.Count > 0)
                {
                    messages[0].AdditionalProperties = new() { ["cache_control"] = "ephemeral" };
                }
                options.AdditionalProperties ??= new();
                options.AdditionalProperties["prompt_cache"] = "ephemeral_prefix";
            },
        };
        var processor = new ContextualChunkProcessor(contextualOptions);

        var document = CreateDocument();
        var rawChunks = chunker.ProcessAsync(document, cancellationToken);
        var enrichedChunks = processor.ProcessAsync(rawChunks, cancellationToken);

        List<IngestionChunk<string>> chunks = [];
        int totalTokens = 0;
        int chunkIndex = 0;

        await foreach (var chunk in enrichedChunks.ConfigureAwait(false))
        {
            int tokens = _tokenizer.CountTokens(chunk.Content, considerNormalization: false);
            totalTokens += tokens;
            chunks.Add(chunk);
            ConsoleFormatter.PrintChunk("ContextualChunkProcessor", ++chunkIndex, chunk, tokens, 96);
        }

        stopwatch.Stop();
        var result = new ScenarioResult(Name, chunks.Count, totalTokens, chunks);
        ConsoleFormatter.PrintScenarioSummary(result, stopwatch.Elapsed);
        return result;
    }

    private static IngestionDocument CreateDocument()
    {
        var document = new IngestionDocument("docs/incident-response-playbook.md");

        var section1 = new IngestionDocumentSection();
        section1.Elements.Add(new IngestionDocumentHeader("# Enterprise Incident Response & Disaster Recovery") { Level = 1 });
        section1.Elements.Add(new IngestionDocumentHeader("## Severity-1 Triage & Bridge Protocol") { Level = 2 });
        section1.Elements.Add(new IngestionDocumentParagraph(
            "The designated incident commander must convene the bridge within 15 minutes of an automated paging alert. " +
            "If the outage affects multi-tenant database clusters, public status advisory updates must be posted " +
            "to the external dashboard every 30 minutes until full traffic restoration is verified."));
        document.Sections.Add(section1);

        var section2 = new IngestionDocumentSection();
        section2.Elements.Add(new IngestionDocumentHeader("## Cross-Region Database Failover") { Level = 2 });
        section2.Elements.Add(new IngestionDocumentParagraph(
            "Secondary standby replicas in the paired cloud region assume active read-write traffic upon DNS failover activation. " +
            "All pending asynchronous replication buffers must be drained prior to releasing transaction locks. " +
            "Replication lag exceeding 500ms mandates an immediate read-only freeze to guarantee zero data loss."));

        const string escalationTable = """
            | Severity | Paging Target | SLA Window | Public Advisory |
            |---|---|---|---|
            | Sev-1 | Incident Commander | 15 minutes | Every 30 minutes |
            | Sev-2 | Technical Lead | 30 minutes | Every 60 minutes |
            """;
        var cells = new IngestionDocumentElement?[3, 4]
        {
            { new IngestionDocumentParagraph("Severity"), new IngestionDocumentParagraph("Paging Target"), new IngestionDocumentParagraph("SLA Window"), new IngestionDocumentParagraph("Public Advisory") },
            { new IngestionDocumentParagraph("Sev-1"), new IngestionDocumentParagraph("Incident Commander"), new IngestionDocumentParagraph("15 minutes"), new IngestionDocumentParagraph("Every 30 minutes") },
            { new IngestionDocumentParagraph("Sev-2"), new IngestionDocumentParagraph("Technical Lead"), new IngestionDocumentParagraph("30 minutes"), new IngestionDocumentParagraph("Every 60 minutes") },
        };
        section2.Elements.Add(new IngestionDocumentTable(escalationTable, cells));
        document.Sections.Add(section2);

        return document;
    }
}
