using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static PinkRooster.Extensions.DataIngestion.Chunkers.Tests.ContextualChunkProcessorTests;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

/// <summary>Regression tests that run the code changed by earlier review fixes (F-02, F-05, F-11, REC-01, REC-05).</summary>
public sealed class RegressionTests
{
    [Fact]
    public void FindPrefixLooksPastAnUnderReportedBoundaryInsideLeadingWhitespace()
    {
        var budget = new TokenBudget(new CharacterTokenizer(underReportPrefix: true), 10);

        int prefix = TextBoundaries.FindPrefix(budget, "      abcdefghij", 10, TestContext.Current.CancellationToken);

        prefix.Should().Be(10);
    }

    [Fact]
    public void PartitionFallbackFindsASplitThatTheGreedyPassMisses()
    {
        var chunker = new ProtectedChunker(new TokenBudget(new CharacterTokenizer(underReportPrefix: false), 100));

        // Greedy takes three characters and then cannot place the single remaining one; only 2 + 2 fits.
        var ranges = chunker.Partition("aaaa", 0, 4,
            first => first + 3 <= 4 ? first + 3 : first,
            (first, last) => last - first is 2 or 3,
            TestContext.Current.CancellationToken);

        ranges.Should().Equal(0..2, 2..4);
    }

    public static TheoryData<string, Action<IServiceCollection?>> NullArguments => new()
    {
        { "recursive services", _ => DataIngestionChunkerServiceCollectionExtensions.AddRecursiveTokenChunker(null!, RecursiveTokenChunkerTests.Options(10)) },
        { "recursive options", s => s!.AddRecursiveTokenChunker((RecursiveChunkerOptions)null!) },
        { "recursive factory", s => s!.AddRecursiveTokenChunker((Func<IServiceProvider, RecursiveChunkerOptions>)null!) },
        { "table services", _ => DataIngestionChunkerServiceCollectionExtensions.AddMarkdownTableChunker(null!, new TableChunkerOptions(TokenizerFixture.Instance)) },
        { "table options", s => s!.AddMarkdownTableChunker((TableChunkerOptions)null!) },
        { "table factory", s => s!.AddMarkdownTableChunker((Func<IServiceProvider, TableChunkerOptions>)null!) },
        { "hierarchical services", _ => DataIngestionChunkerServiceCollectionExtensions.AddHierarchicalTokenChunker(null!, new HierarchicalChunkerOptions(TokenizerFixture.Instance)) },
        { "hierarchical options", s => s!.AddHierarchicalTokenChunker((HierarchicalChunkerOptions)null!) },
        { "hierarchical factory", s => s!.AddHierarchicalTokenChunker((Func<IServiceProvider, HierarchicalChunkerOptions>)null!) },
        { "contextual options", s => s!.AddContextualChunkProcessor((ContextualChunkProcessorOptions)null!) },
        { "contextual factory", s => s!.AddContextualChunkProcessor((Func<IServiceProvider, ContextualChunkProcessorOptions>)null!) },
    };

    [Theory]
    [MemberData(nameof(NullArguments))]
    public void RegistrationsRejectNullArguments(string registration, Action<IServiceCollection?> register)
    {
        Action act = () => register(new ServiceCollection());

        act.Should().Throw<ArgumentNullException>(registration);
    }

    [Fact]
    public void FactoryRegistrationsResolveFromTheContainer()
    {
        using var client = Reply("header");
        using var provider = new ServiceCollection()
            .AddSingleton(client)
            .AddRecursiveTokenChunker(_ => RecursiveTokenChunkerTests.Options(50))
            .AddMarkdownTableChunker(_ => new TableChunkerOptions(TokenizerFixture.Instance) { MaxTokensPerChunk = 50 })
            .AddHierarchicalTokenChunker(_ => new HierarchicalChunkerOptions(TokenizerFixture.Instance))
            .AddContextualChunkProcessor(sp => Options(sp.GetRequiredService<ContextualChatClient>()))
            .BuildServiceProvider();

        provider.GetServices<IngestionChunker<string>>().Select(chunker => chunker.GetType()).Should()
            .Equal(typeof(RecursiveTokenChunker), typeof(MarkdownTableChunker), typeof(HierarchicalTokenChunker));
        provider.GetRequiredService<IngestionChunkProcessor<string>>().Should().BeOfType<ContextualChunkProcessor>();
    }
}
