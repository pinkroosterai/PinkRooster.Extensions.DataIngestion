using Microsoft.Extensions.DataIngestion;
using PinkRooster.Extensions.DataIngestion.Samples.Scenarios;

namespace PinkRooster.Extensions.DataIngestion.Samples.Formatting;

/// <summary>
/// Provides ANSI and Unicode console card formatting for chunk inspection and scenario summaries.
/// </summary>
public static class ConsoleFormatter
{
    private const string HorizontalLine = "────────────────────────────────────────────────────────────────────────────────";
    private const string DoubleLine = "================================================================================";

    /// <summary>
    /// Prints the application banner.
    /// </summary>
    public static void PrintAppBanner()
    {
        Console.WriteLine(DoubleLine);
        Console.WriteLine(" PinkRooster.Extensions.DataIngestion - RAG Chunker Showcase (.NET 10)");
        Console.WriteLine(" Structure-Aware Recursive Chunking, Contextual Enrichment & Roslyn AST Slicing");
        Console.WriteLine(DoubleLine);
    }

    /// <summary>
    /// Prints the header for an individual scenario.
    /// </summary>
    /// <param name="name">Scenario name.</param>
    /// <param name="description">Scenario description.</param>
    public static void PrintScenarioHeader(string name, string description)
    {
        Console.WriteLine();
        Console.WriteLine($"┌─ SCENARIO: {name.ToUpperInvariant()} {HorizontalLine[..(66 - Math.Min(name.Length, 40))]}┐");
        Console.WriteLine($"│ Description: {description}");
        Console.WriteLine($"└{HorizontalLine[..78]}┘");
    }

    /// <summary>
    /// Prints a formatted card representing an individual chunk.
    /// </summary>
    /// <param name="scenario">Scenario name or chunker category.</param>
    /// <param name="index">1-based chunk index.</param>
    /// <param name="chunk">The ingestion chunk.</param>
    /// <param name="tokenCount">Calculated token count.</param>
    /// <param name="maxTokens">Configured token ceiling.</param>
    public static void PrintChunk(string scenario, int index, IngestionChunk<string> chunk, int tokenCount, int maxTokens)
    {
        string title = $"[{scenario}] Chunk #{index} ({tokenCount} tokens / max {maxTokens})";
        int fill = Math.Max(0, 76 - title.Length);
        Console.WriteLine($"\n┌─ {title} {HorizontalLine[..fill]}┐");

        if (!string.IsNullOrEmpty(chunk.Context))
        {
            Console.WriteLine($"│ Context: {chunk.Context}");
        }

        if (chunk.HasMetadata && chunk.Metadata.Count > 0)
        {
            List<string> metaParts = [];
            foreach (var kvp in chunk.Metadata)
            {
                if (kvp.Value is IEnumerable<string> items)
                {
                    metaParts.Add($"{kvp.Key}=[{string.Join(", ", items)}]");
                }
                else
                {
                    string str = kvp.Value?.ToString() ?? "null";
                    if (str.Length > 60)
                    {
                        str = str[..57] + "...";
                    }
                    metaParts.Add($"{kvp.Key}=\"{str}\"");
                }
            }
            Console.WriteLine($"│ Metadata: {string.Join(" | ", metaParts)}");
        }

        Console.WriteLine($"├{HorizontalLine[..78]}┤");

        string[] lines = chunk.Content.TrimEnd().Split('\n');
        foreach (string line in lines)
        {
            Console.WriteLine($"│ {line.TrimEnd('\r')}");
        }

        Console.WriteLine($"└{HorizontalLine[..78]}┘");
    }

    /// <summary>
    /// Prints a scenario completion summary.
    /// </summary>
    /// <param name="result">The scenario result.</param>
    /// <param name="elapsed">Execution elapsed time.</param>
    public static void PrintScenarioSummary(ScenarioResult result, TimeSpan elapsed)
    {
        double avgTokens = result.ChunkCount > 0 ? (double)result.TotalTokens / result.ChunkCount : 0;
        Console.WriteLine($"\n>> [{result.ScenarioName}] Completed in {elapsed.TotalMilliseconds:F1}ms: " +
                          $"{result.ChunkCount} chunks generated, {result.TotalTokens} total tokens (avg {avgTokens:F1} tokens/chunk).");
    }

    /// <summary>
    /// Prints an overall execution summary across all scenarios.
    /// </summary>
    /// <param name="results">List of all scenario results.</param>
    /// <param name="totalElapsed">Total elapsed time.</param>
    public static void PrintOverallSummary(IReadOnlyList<ScenarioResult> results, TimeSpan totalElapsed)
    {
        int totalChunks = results.Sum(r => r.ChunkCount);
        int totalTokens = results.Sum(r => r.TotalTokens);

        Console.WriteLine();
        Console.WriteLine(DoubleLine);
        Console.WriteLine(" Showcase Summary");
        Console.WriteLine(DoubleLine);
        Console.WriteLine($"{"Scenario",-30} | {"Chunks",8} | {"Tokens",8} | {"Avg Tokens/Chunk",18}");
        Console.WriteLine(HorizontalLine);

        foreach (var r in results)
        {
            double avg = r.ChunkCount > 0 ? (double)r.TotalTokens / r.ChunkCount : 0;
            Console.WriteLine($"{r.ScenarioName,-30} | {r.ChunkCount,8} | {r.TotalTokens,8} | {avg,18:F1}");
        }

        Console.WriteLine(HorizontalLine);
        double overallAvg = totalChunks > 0 ? (double)totalTokens / totalChunks : 0;
        Console.WriteLine($"{"TOTAL",-30} | {totalChunks,8} | {totalTokens,8} | {overallAvg,18:F1}");
        Console.WriteLine($"Total execution time: {totalElapsed.TotalMilliseconds:F1}ms");
        Console.WriteLine(DoubleLine);
    }
}
