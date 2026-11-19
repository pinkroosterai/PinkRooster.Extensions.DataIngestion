using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Chunkers;
using PinkRooster.Extensions.DataIngestion.Samples.Formatting;

namespace PinkRooster.Extensions.DataIngestion.Samples.Scenarios;

/// <summary>
/// Showcases <see cref="RecursiveTokenChunker"/> across hierarchical prose, multi-level headings,
/// atomic tables, oversized table continuation with repeated headers, and atomic code fences.
/// </summary>
public sealed class RecursiveChunkerScenario : ISampleScenario
{
    private readonly Tokenizer _tokenizer;

    /// <summary>
    /// Initializes a new instance of <see cref="RecursiveChunkerScenario"/>.
    /// </summary>
    /// <param name="tokenizer">The tokenizer to use for token counting.</param>
    public RecursiveChunkerScenario(Tokenizer tokenizer)
    {
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
    }

    /// <inheritdoc/>
    public string Name => "recursive";

    /// <inheritdoc/>
    public string Description =>
        "Hierarchical prose chunking, heading breadcrumbs, atomic code fences, and multi-row table splitting with repeated headers.";

    /// <inheritdoc/>
    public async Task<ScenarioResult> RunAsync(CancellationToken cancellationToken = default)
    {
        ConsoleFormatter.PrintScenarioHeader(Name, Description);

        var options = new RecursiveChunkerOptions(_tokenizer)
        {
            MaxTokensPerChunk = 64,
            ReservedHeaderTokens = 12,
            OverlapTokens = 8,
            PreserveAtomicElements = true,
            KeepSeparator = SeparatorPlacement.Trailing,
        };

        var chunker = new RecursiveTokenChunker(options);
        var document = CreateDocument();

        List<IngestionChunk<string>> chunks = [];
        int totalTokens = 0;
        int chunkIndex = 0;

        await foreach (var chunk in chunker.ProcessAsync(document, cancellationToken).ConfigureAwait(false))
        {
            int tokens = _tokenizer.CountTokens(chunk.Content, considerNormalization: false);
            totalTokens += tokens;
            chunks.Add(chunk);
            ConsoleFormatter.PrintChunk("RecursiveTokenChunker", ++chunkIndex, chunk, tokens, options.MaxTokensPerChunk);
        }

        var result = new ScenarioResult(Name, chunks.Count, totalTokens, chunks);
        ConsoleFormatter.PrintScenarioSummary(result, TimeSpan.Zero);
        return result;
    }

    private static IngestionDocument CreateDocument()
    {
        var document = new IngestionDocument("docs/logistics-dispatch-guide.md");

        // Section 1: Top-level prose and general shipping rules
        var section1 = new IngestionDocumentSection();
        section1.Elements.Add(new IngestionDocumentHeader("# Logistics & Fulfillment Dispatch Guide") { Level = 1 });
        section1.Elements.Add(new IngestionDocumentParagraph(
            "Standard customer orders ship within two business days from the regional distribution center. " +
            "Priority express orders ship the same day when placed prior to 14:00 UTC. " +
            "All outbound consignments undergo barcode verification and weigh-in before dock loading."));
        document.Sections.Add(section1);

        // Section 2: Nested subsection with an oversized table demonstrating repeated headers on continuations
        var section2 = new IngestionDocumentSection();
        section2.Elements.Add(new IngestionDocumentHeader("## Freight Schedules & Service Levels") { Level = 2 });
        section2.Elements.Add(new IngestionDocumentHeader("### Regional Transit Matrix") { Level = 3 });
        section2.Elements.Add(new IngestionDocumentParagraph(
            "The following matrix defines cutoff deadlines and transit commitments across all freight modes:"));

        const string tableMarkdown = """
            | Tier | Cutoff | Lead Time | Carrier Network |
            |---|---|---|---|
            | Priority Express | 14:00 UTC | Same day dispatch | Air Cargo Express |
            | Regional Standard | 18:00 UTC | 2 business days | Interstate Ground |
            | Economy Freight | 20:00 UTC | 4 business days | Intermodal Rail |
            | Maritime Container | 12:00 UTC | 14 business days | Ocean Freight Line |
            | Temperature Controlled | 11:00 UTC | Next flight out | Pharma ColdChain |
            | Hazardous Materials | 09:00 UTC | 3 business days | Certified HazMat |
            """;

        var cells = new IngestionDocumentElement?[7, 4]
        {
            { new IngestionDocumentParagraph("Tier"), new IngestionDocumentParagraph("Cutoff"), new IngestionDocumentParagraph("Lead Time"), new IngestionDocumentParagraph("Carrier Network") },
            { new IngestionDocumentParagraph("Priority Express"), new IngestionDocumentParagraph("14:00 UTC"), new IngestionDocumentParagraph("Same day dispatch"), new IngestionDocumentParagraph("Air Cargo Express") },
            { new IngestionDocumentParagraph("Regional Standard"), new IngestionDocumentParagraph("18:00 UTC"), new IngestionDocumentParagraph("2 business days"), new IngestionDocumentParagraph("Interstate Ground") },
            { new IngestionDocumentParagraph("Economy Freight"), new IngestionDocumentParagraph("20:00 UTC"), new IngestionDocumentParagraph("4 business days"), new IngestionDocumentParagraph("Intermodal Rail") },
            { new IngestionDocumentParagraph("Maritime Container"), new IngestionDocumentParagraph("12:00 UTC"), new IngestionDocumentParagraph("14 business days"), new IngestionDocumentParagraph("Ocean Freight Line") },
            { new IngestionDocumentParagraph("Temperature Controlled"), new IngestionDocumentParagraph("11:00 UTC"), new IngestionDocumentParagraph("Next flight out"), new IngestionDocumentParagraph("Pharma ColdChain") },
            { new IngestionDocumentParagraph("Hazardous Materials"), new IngestionDocumentParagraph("09:00 UTC"), new IngestionDocumentParagraph("3 business days"), new IngestionDocumentParagraph("Certified HazMat") },
        };

        var table = new IngestionDocumentTable(tableMarkdown, cells);
        section2.Elements.Add(table);
        document.Sections.Add(section2);

        // Section 3: Webhook payload schema with atomic fenced code block
        var section3 = new IngestionDocumentSection();
        section3.Elements.Add(new IngestionDocumentHeader("## Dispatch Event Schema") { Level = 2 });
        section3.Elements.Add(new IngestionDocumentParagraph(
            "Upon consignment release, fulfillment services emit this structured JSON event notification:"));

        const string jsonBlock = """
            ```json
            {
              "event": "order.dispatched",
              "tracking_number": "TRK-9824110",
              "carrier": "Air Cargo Express",
              "departure_utc": "2026-11-20T14:30:00Z"
            }
            ```
            """;
        section3.Elements.Add(new IngestionDocumentParagraph(jsonBlock));
        document.Sections.Add(section3);

        return document;
    }
}
