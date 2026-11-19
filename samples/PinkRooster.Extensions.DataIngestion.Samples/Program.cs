using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Chunkers;
using PinkRooster.Extensions.DataIngestion.CodeAnalysis;

namespace PinkRooster.Extensions.DataIngestion.Samples;

/// <summary>
/// End-to-end sample application demonstrating RecursiveTokenChunker, ContextualChunkProcessor,
/// and RoslynCodeChunker pipelines.
/// </summary>
public static class Program
{
    /// <summary>
    /// Entry point for sample console runner.
    /// </summary>
    public static async Task Main() => await RunAsync();

    /// <summary>
    /// Runs the sample pipeline and returns the total number of chunks generated.
    /// </summary>
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine(" PinkRooster.Extensions.DataIngestion - End-to-End Pipeline Sample (.NET 10)");
        Console.WriteLine("================================================================================");

        // quickstart:start
        var tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

        var recursiveOptions = new RecursiveChunkerOptions(tokenizer)
        {
            MaxTokensPerChunk = 64,
            ReservedHeaderTokens = 12,
            OverlapTokens = 8,
            PreserveAtomicElements = true,
        };

        var recursiveChunker = new RecursiveTokenChunker(recursiveOptions);
        // quickstart:end

        // -----------------------------------------------------------------------------------------
        // 1. Technical Markdown Prose Ingestion with RecursiveTokenChunker & ContextualChunkProcessor
        // -----------------------------------------------------------------------------------------
        Console.WriteLine("\n[1] Processing technical Markdown documentation with recursive chunking & contextual enrichment...");

        var proseDoc = new IngestionDocument("docs/logistics-dispatch.md");
        var proseSection = new IngestionDocumentSection();
        proseSection.Elements.Add(new IngestionDocumentHeader("# Logistics & Fulfillment Dispatch Guide") { Level = 1 });
        proseSection.Elements.Add(new IngestionDocumentParagraph(
            "Standard customer orders ship within two business days from the regional distribution center. " +
            "Priority express orders ship the same day when placed prior to 14:00 UTC."));

        const string tableMarkdown = """
            | Tier | Cutoff | SLA |
            |---|---|---|
            | Standard | 18:00 UTC | 2 business days |
            """;
        var cells = new IngestionDocumentElement?[2, 3]
        {
            { new IngestionDocumentParagraph("Tier"), new IngestionDocumentParagraph("Cutoff"), new IngestionDocumentParagraph("SLA") },
            { new IngestionDocumentParagraph("Standard"), new IngestionDocumentParagraph("18:00 UTC"), new IngestionDocumentParagraph("2 business days") },
        };
        var table = new IngestionDocumentTable(tableMarkdown, cells);
        proseSection.Elements.Add(table);
        proseDoc.Sections.Add(proseSection);

        // Simulated IChatClient for deterministic contextual enrichment demonstration
        var chatClient = new SampleContextualChatClient();
        var contextualOptions = new ContextualChunkerOptions(chatClient, tokenizer)
        {
            EmbeddingTokenizer = tokenizer,
            MaxEnrichedTokens = 128,
        };
        var contextualProcessor = new ContextualChunkProcessor(contextualOptions);

        // Stream chunks through the chunker and contextual processor
        var rawChunks = recursiveChunker.ProcessAsync(proseDoc);
        var enrichedChunks = contextualProcessor.ProcessAsync(rawChunks);

        int proseIndex = 0;
        await foreach (var chunk in enrichedChunks)
        {
            int tokens = tokenizer.CountTokens(chunk.Content, considerNormalization: false);
            Console.WriteLine($"\n--- Prose Chunk #{++proseIndex} ({tokens} tokens) ---");
            if (!string.IsNullOrEmpty(chunk.Context))
            {
                Console.WriteLine($"Context: {chunk.Context}");
            }
            if (chunk.Metadata.TryGetValue("contextual_header", out var header))
            {
                Console.WriteLine($"Situational Header: \"{header}\"");
            }
            Console.WriteLine($"Content:\n{chunk.Content.Trim()}");
        }

        // -----------------------------------------------------------------------------------------
        // 2. Syntax-Aware C# Source Code Ingestion with SourceCodeDocumentReader & RoslynCodeChunker
        // -----------------------------------------------------------------------------------------
        Console.WriteLine("\n\n[2] Ingesting and chunking C# source code with syntax awareness...");

        const string csharpSource = """
            namespace Logistics.Fulfillment;

            /// <summary>Defines clock provider contract for dispatch calculation.</summary>
            public interface IDispatchClock
            {
                DateTimeOffset UtcNow { get; }
            }

            /// <summary>Calculates dispatch deadlines and transit windows.</summary>
            public sealed class DispatchCoordinator(IDispatchClock clock, int defaultLeadDays = 2)
            {
                /// <summary>Calculates shipping date for standard or priority orders.</summary>
                public DateTimeOffset CalculateShipDate(bool isPriority)
                {
                    var now = clock.UtcNow;
                    return isPriority ? now : now.AddDays(defaultLeadDays);
                }

                /// <summary>Determines if cutoff time has passed for today's dispatch.</summary>
                public bool HasCutoffPassed(TimeOnly cutoffTime) =>
                    TimeOnly.FromDateTime(clock.UtcNow.DateTime) > cutoffTime;
            }
            """;

        var codeReader = new SourceCodeDocumentReader();
        using var codeStream = new MemoryStream(Encoding.UTF8.GetBytes(csharpSource));
        var codeDoc = await codeReader.ReadAsync(codeStream, "DispatchCoordinator.cs", "text/x-csharp");

        var roslynOptions = new RoslynChunkerOptions(tokenizer)
        {
            MaxTokensPerChunk = 64,
            OverlapTokens = 0,
            IncludeEnclosingTypeSignature = true,
        };
        var roslynChunker = new RoslynCodeChunker(roslynOptions);

        int codeIndex = 0;
        await foreach (var chunk in roslynChunker.ProcessAsync(codeDoc))
        {
            int tokens = tokenizer.CountTokens(chunk.Content, considerNormalization: false);
            Console.WriteLine($"\n--- C# Code Chunk #{++codeIndex} ({tokens} tokens) ---");
            if (!string.IsNullOrEmpty(chunk.Context))
            {
                Console.WriteLine($"Symbol Breadcrumb: {chunk.Context}");
            }
            Console.WriteLine($"Content:\n{chunk.Content.Trim()}");
        }

        Console.WriteLine("\n================================================================================");
        Console.WriteLine(" Sample execution completed successfully.");
        Console.WriteLine("================================================================================");

        return proseIndex + codeIndex;
    }
}

/// <summary>
/// A lightweight sample chat client demonstrating contextual prompt resolution.
/// </summary>
file sealed class SampleContextualChatClient : IChatClient
{
    public ChatClientMetadata Metadata => new("SampleContextualClient");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "This document excerpt details logistics dispatch SLA rules and scheduling cutoff times."));
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
