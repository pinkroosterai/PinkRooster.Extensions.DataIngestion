using AwesomeAssertions;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Samples;
using PinkRooster.Extensions.DataIngestion.Chunkers;

namespace PinkRooster.Extensions.DataIngestion.Samples.Tests;

public sealed class SampleTests
{
    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

    [Fact]
    public async Task RunAsync_Default_ExecutesAllScenariosSuccessfully()
    {
        var token = TestContext.Current.CancellationToken;
        int totalChunks = await Program.RunAsync("all", token);
        totalChunks.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RunAsync_Recursive_PreservesAtomicFencesAndRepeatsTableHeaders()
    {
        var token = TestContext.Current.CancellationToken;
        var result = await Program.RunScenarioAsync("recursive", token);

        result.ChunkCount.Should().BeGreaterThan(0);
        result.TotalTokens.Should().BeGreaterThan(0);

        // 1. Invariant: Every chunk must adhere to MaxTokensPerChunk (64)
        foreach (var chunk in result.Chunks)
        {
            int tokens = Tokenizer.CountTokens(chunk.Content, considerNormalization: false);
            tokens.Should().BeLessThanOrEqualTo(64);
        }

        // 2. Hierarchical breadcrumb context verification
        result.Chunks.Should().Contain(c =>
            c.Context != null && c.Context.Contains("Logistics & Fulfillment Dispatch Guide"));

        // 3. Atomic fenced code block verification
        result.Chunks.Should().Contain(c =>
            c.Content.Contains("```json") && c.Content.Contains("order.dispatched"));

        // 4. Multi-row table splitting with repeated column header verification
        var tableChunks = result.Chunks.Where(c => c.Content.Contains("| Tier | Cutoff |")).ToList();
        tableChunks.Count.Should().BeGreaterThanOrEqualTo(2);
        foreach (var tableChunk in tableChunks)
        {
            tableChunk.Content.Should().Contain("| Tier | Cutoff | Lead Time | Carrier Network |");
            tableChunk.Content.Should().Contain("---");
        }
    }

    [Fact]
    public async Task RunAsync_Contextual_AppliesSituationalHeadersAndFiltersTables()
    {
        var token = TestContext.Current.CancellationToken;
        var result = await Program.RunScenarioAsync("contextual", token);

        result.ChunkCount.Should().BeGreaterThan(0);

        // 1. Invariant: Every chunk must adhere to MaxEnrichedTokens (96)
        foreach (var chunk in result.Chunks)
        {
            int tokens = Tokenizer.CountTokens(chunk.Content, considerNormalization: false);
            tokens.Should().BeLessThanOrEqualTo(96);
        }

        // 2. Enriched chunks contain contextual_header metadata
        var enrichedChunks = result.Chunks.Where(c => c.Metadata.ContainsKey(ChunkMetadataKeys.ContextualHeader)).ToList();
        enrichedChunks.Should().NotBeEmpty();

        // 3. Filtered chunks (tables) bypass enrichment
        var filteredTableChunks = result.Chunks.Where(c => c.Content.StartsWith('|')).ToList();
        filteredTableChunks.Should().NotBeEmpty();
        foreach (var tableChunk in filteredTableChunks)
        {
            tableChunk.Metadata.ContainsKey(ChunkMetadataKeys.ContextualHeader).Should().BeFalse();
        }
    }

    [Fact]
    public async Task RunAsync_Roslyn_PreservesAstTriviaPrimaryConstructorsAndConditionalDirectives()
    {
        var token = TestContext.Current.CancellationToken;
        var result = await Program.RunScenarioAsync("roslyn", token);

        result.ChunkCount.Should().BeGreaterThan(0);

        // 1. Invariant: Every chunk must adhere to MaxTokensPerChunk (64)
        foreach (var chunk in result.Chunks)
        {
            int tokens = Tokenizer.CountTokens(chunk.Content, considerNormalization: false);
            tokens.Should().BeLessThanOrEqualTo(64);
        }

        // 2. Primary constructor context propagation
        result.Chunks.Should().Contain(c =>
            c.Context != null && c.Context.Contains("DispatchCoordinator(IDispatchClock clock, int defaultLeadDays = 2)"));

        // 3. Conditional compilation symbols metadata
        var conditionalChunks = result.Chunks.Where(c => c.Metadata.ContainsKey(ChunkMetadataKeys.ConditionalSymbols)).ToList();
        conditionalChunks.Should().NotBeEmpty();
        var symbols = (string[])conditionalChunks[0].Metadata[ChunkMetadataKeys.ConditionalSymbols]!;
        symbols.Should().Contain("DEBUG");

        // 4. Trivia and attributes preservation
        result.Chunks.Should().Contain(c =>
            c.Content.Contains("/// <summary>Calculates shipping date") && c.Content.Contains("[Obsolete("));

        // 5. Embedded Markdown C# code extraction
        result.Chunks.Should().Contain(c =>
            c.Context != null && c.Context.Contains("DispatchRunner"));
    }

    [Fact]
    public async Task RunAsync_Hierarchical_YieldsLinkedParentsAndChildren()
    {
        var token = TestContext.Current.CancellationToken;
        var result = await Program.RunScenarioAsync("hierarchical", token);

        result.ChunkCount.Should().BeGreaterThan(0);
        result.TotalTokens.Should().BeGreaterThan(0);

        var parents = result.Chunks.Where(c => (string)c.Metadata[ChunkMetadataKeys.ChunkRole] == ChunkMetadataKeys.ParentRole).ToList();
        var children = result.Chunks.Where(c => (string)c.Metadata[ChunkMetadataKeys.ChunkRole] == ChunkMetadataKeys.ChildRole).ToList();

        parents.Should().NotBeEmpty();
        children.Should().NotBeEmpty();

        foreach (var child in children)
        {
            child.Metadata.Should().ContainKey(ChunkMetadataKeys.ParentId);
            child.Metadata.Should().ContainKey(ChunkMetadataKeys.ChildIndex);
            parents.Should().Contain(p => (string)p.Metadata[ChunkMetadataKeys.ChunkId] == (string)child.Metadata[ChunkMetadataKeys.ParentId]);
        }
    }

    [Fact]
    public async Task Main_WithValidAndInvalidArguments_ReturnsExpectedExitCodes()
    {
        int allCode = await Program.Main([]);
        allCode.Should().Be(0);

        int recursiveCode = await Program.Main(["recursive"]);
        recursiveCode.Should().Be(0);

        int invalidCode = await Program.Main(["non_existent_scenario"]);
        invalidCode.Should().Be(1);
    }
}
