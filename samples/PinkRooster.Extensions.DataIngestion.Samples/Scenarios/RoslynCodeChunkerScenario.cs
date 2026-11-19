using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.CodeAnalysis;
using PinkRooster.Extensions.DataIngestion.Samples.Formatting;
using Spectre.Console;

namespace PinkRooster.Extensions.DataIngestion.Samples.Scenarios;

/// <summary>
/// Showcases <see cref="RoslynCodeChunker"/> and <see cref="SourceCodeDocumentReader"/> performing
/// syntax-aware C# AST chunking, primary constructor context propagation, trivia preservation,
/// statement fallback for oversized methods, conditional compilation symbols, and embedded Markdown C# chunking.
/// </summary>
public sealed class RoslynCodeChunkerScenario : ISampleScenario
{
    private readonly Tokenizer _tokenizer;

    /// <summary>
    /// Initializes a new instance of <see cref="RoslynCodeChunkerScenario"/>.
    /// </summary>
    /// <param name="tokenizer">The tokenizer for syntax token budget tracking.</param>
    public RoslynCodeChunkerScenario(Tokenizer tokenizer)
    {
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
    }

    /// <inheritdoc/>
    public string Name => "roslyn";

    /// <inheritdoc/>
    public string Description =>
        "Syntax-aware C# AST chunking, primary constructor signatures, XML doc trivia, preprocessor symbols, and statement fallback.";

    /// <inheritdoc/>
    public async Task<ScenarioResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        ConsoleFormatter.PrintScenarioHeader(Name, Description);

        var roslynOptions = new RoslynChunkerOptions(_tokenizer)
        {
            MaxTokensPerChunk = 64,
            OverlapTokens = 0,
            IncludeEnclosingTypeSignature = true,
            IncludeUsingDirectives = true,
            StripRegionTrivia = true,
        };
        var chunker = new RoslynCodeChunker(roslynOptions);

        List<IngestionChunk<string>> chunks = [];
        int totalTokens = 0;
        int chunkIndex = 0;

        // -----------------------------------------------------------------------------------------
        // Part 1: Direct C# Source File Ingestion via SourceCodeDocumentReader
        // -----------------------------------------------------------------------------------------
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold cyan]── Part 1: Ingesting raw C# source code via SourceCodeDocumentReader ──[/]");
        var reader = new SourceCodeDocumentReader();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SampleCSharpSource));
        var codeDoc = await reader.ReadAsync(stream, "DispatchCoordinator.cs", "text/x-csharp", cancellationToken).ConfigureAwait(false);

        await foreach (var chunk in chunker.ProcessAsync(codeDoc, cancellationToken).ConfigureAwait(false))
        {
            int tokens = _tokenizer.CountTokens(chunk.Content, considerNormalization: false);
            totalTokens += tokens;
            chunks.Add(chunk);
            ConsoleFormatter.PrintChunk("Roslyn (Raw C#)", ++chunkIndex, chunk, tokens, roslynOptions.MaxTokensPerChunk);
        }

        // -----------------------------------------------------------------------------------------
        // Part 2: Syntax Chunking of Embedded ```csharp Fenced Blocks in Markdown
        // -----------------------------------------------------------------------------------------
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold cyan]── Part 2: Extracting and syntax-chunking embedded C# from Markdown documentation ──[/]");
        var mdDoc = CreateMarkdownWithCSharp();
        await foreach (var chunk in chunker.ProcessAsync(mdDoc, cancellationToken).ConfigureAwait(false))
        {
            int tokens = _tokenizer.CountTokens(chunk.Content, considerNormalization: false);
            totalTokens += tokens;
            chunks.Add(chunk);
            ConsoleFormatter.PrintChunk("Roslyn (Markdown Fenced)", ++chunkIndex, chunk, tokens, roslynOptions.MaxTokensPerChunk);
        }

        stopwatch.Stop();
        var result = new ScenarioResult(Name, chunks.Count, totalTokens, chunks);
        ConsoleFormatter.PrintScenarioSummary(result, stopwatch.Elapsed);
        return result;
    }

    private static IngestionDocument CreateMarkdownWithCSharp()
    {
        var doc = new IngestionDocument("docs/integration-guide.md");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentHeader("# Integration Guide") { Level = 1 });
        section.Elements.Add(new IngestionDocumentParagraph(
            "To consume the dispatch engine from a client service, instantiate the coordinator and calculate transit windows:"));

        const string fencedCSharp = """
            ```csharp
            namespace Logistics.Integration;

            /// <summary>Sample client consumption runner.</summary>
            public static class DispatchRunner
            {
                public static DateTimeOffset Run(IDispatchClock clock)
                {
                    var coordinator = new DispatchCoordinator(clock, defaultLeadDays: 3);
                    return coordinator.CalculateShipDate(isPriority: true);
                }
            }
            ```
            """;
        section.Elements.Add(new IngestionDocumentParagraph(fencedCSharp));
        doc.Sections.Add(section);
        return doc;
    }

    private const string SampleCSharpSource = """
        #define DEBUG

        namespace Logistics.Fulfillment;

        using System;
        using System.Collections.Generic;

        /// <summary>Defines clock provider contract for dispatch calculation.</summary>
        public interface IDispatchClock
        {
            DateTimeOffset UtcNow { get; }
        }

        /// <summary>Immutable record capturing customer consignment metrics.</summary>
        public sealed record DispatchOrder(Guid OrderId, string CustomerTier, int ItemCount, decimal OrderTotal);

        /// <summary>Calculates dispatch deadlines and executes batch evaluations.</summary>
        public sealed class DispatchCoordinator(IDispatchClock clock, int defaultLeadDays = 2)
        {
            #region Dispatch Operations

            /// <summary>Calculates shipping date for standard or priority orders.</summary>
            [Obsolete("Use CalculateShipDateOffset instead.")]
            public DateTimeOffset CalculateShipDate(bool isPriority)
            {
                var now = clock.UtcNow;
                return isPriority ? now : now.AddDays(defaultLeadDays);
            }

            /// <summary>
            /// Evaluates high-volume order batches against freight constraints and cutoff deadlines.
            /// Contains nested statements to showcase Roslyn AST statement-level fallback splitting.
            /// </summary>
            public int ProcessBatch(IReadOnlyList<DispatchOrder> orders, TimeOnly cutoff)
            {
                int processedCount = 0;
                DateTimeOffset timestamp = clock.UtcNow;

                #if DEBUG
                Console.WriteLine($"[DEBUG] Starting dispatch batch at {timestamp} with {orders.Count} orders.");
                #endif

                try
                {
                    foreach (var order in orders)
                    {
                        if (order.ItemCount > 50)
                        {
                            processedCount += 2;
                        }
                        else if (order.OrderTotal > 1000m)
                        {
                            processedCount += 1;
                        }
                        else
                        {
                            processedCount += 1;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Batch error: {ex.Message}");
                    throw;
                }

                return processedCount;
            }

            #endregion
        }
        """;
}
