using System.Diagnostics;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Chunkers;
using PinkRooster.Extensions.DataIngestion.Samples.Formatting;
using PinkRooster.Extensions.DataIngestion.Samples.Scenarios;

namespace PinkRooster.Extensions.DataIngestion.Samples;

/// <summary>
/// Entry point for the PinkRooster.Extensions.DataIngestion sample console application.
/// Demonstrates structure-aware recursive chunking, contextual enrichment, and Roslyn AST code chunking.
/// </summary>
public static class Program
{
    /// <summary>
    /// Command-line entry point. Accepts an optional scenario filter: [all | recursive | contextual | roslyn].
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Exit code (0 for success, non-zero for error).</returns>
    public static async Task<int> Main(string[] args)
    {
        string scenario = args.Length > 0 ? args[0].ToLowerInvariant().Trim() : "all";

        if (scenario is not ("all" or "recursive" or "contextual" or "roslyn"))
        {
            Console.Error.WriteLine($"Error: Unknown scenario '{scenario}'.");
            Console.WriteLine("Usage: dotnet run --project samples/PinkRooster.Extensions.DataIngestion.Samples -- [all | recursive | contextual | roslyn]");
            return 1;
        }

        int totalChunks = await RunAsync(scenario).ConfigureAwait(false);
        return totalChunks > 0 ? 0 : 1;
    }

    /// <summary>
    /// Executes the sample pipeline for the specified scenario or all scenarios.
    /// </summary>
    /// <param name="scenarioName">The target scenario to execute: "all", "recursive", "contextual", or "roslyn".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The total number of chunks generated across the executed scenarios.</returns>
    public static async Task<int> RunAsync(string scenarioName = "all", CancellationToken cancellationToken = default)
    {
        ConsoleFormatter.PrintAppBanner();

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

        // Suppress unused variable warning while preserving the quickstart synchronization snippet
        _ = recursiveChunker;

        var scenarios = new Dictionary<string, ISampleScenario>(StringComparer.OrdinalIgnoreCase)
        {
            ["recursive"] = new RecursiveChunkerScenario(tokenizer),
            ["contextual"] = new ContextualEnrichmentScenario(tokenizer),
            ["roslyn"] = new RoslynCodeChunkerScenario(tokenizer),
        };

        List<ISampleScenario> toRun = [];
        if (scenarioName.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            toRun.AddRange(scenarios.Values);
        }
        else if (scenarios.TryGetValue(scenarioName, out var matched))
        {
            toRun.Add(matched);
        }
        else
        {
            throw new ArgumentException($"Unknown scenario '{scenarioName}'. Valid scenarios are: all, recursive, contextual, roslyn.", nameof(scenarioName));
        }

        var results = new List<ScenarioResult>();
        var overallStopwatch = Stopwatch.StartNew();

        foreach (var scenario in toRun)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scenarioStopwatch = Stopwatch.StartNew();
            var result = await scenario.RunAsync(cancellationToken).ConfigureAwait(false);
            scenarioStopwatch.Stop();
            results.Add(result);
        }

        overallStopwatch.Stop();

        if (results.Count > 1)
        {
            ConsoleFormatter.PrintOverallSummary(results, overallStopwatch.Elapsed);
        }

        return results.Sum(r => r.ChunkCount);
    }

    /// <summary>
    /// Executes a single named scenario and returns its detailed chunk result for verification.
    /// </summary>
    /// <param name="scenarioName">Scenario name: "recursive", "contextual", or "roslyn".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scenario execution result containing all generated chunks.</returns>
    public static async Task<ScenarioResult> RunScenarioAsync(string scenarioName, CancellationToken cancellationToken = default)
    {
        var tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
        ISampleScenario scenario = scenarioName.ToLowerInvariant().Trim() switch
        {
            "recursive" => new RecursiveChunkerScenario(tokenizer),
            "contextual" => new ContextualEnrichmentScenario(tokenizer),
            "roslyn" => new RoslynCodeChunkerScenario(tokenizer),
            _ => throw new ArgumentException($"Unknown scenario '{scenarioName}'.", nameof(scenarioName)),
        };

        return await scenario.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
