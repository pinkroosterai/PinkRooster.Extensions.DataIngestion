using AwesomeAssertions;
using Microsoft.ML.Tokenizers;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class HierarchicalChunkerOptionsTests
{
    private static readonly Tokenizer Tokenizer = TokenizerFixture.Instance;

    [Fact]
    public void HierarchicalChunkerOptionsDefaultsAreCorrect()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer);

        options.ParentMaxTokens.Should().Be(1024);
        options.ParentOverlapTokens.Should().Be(0);
        options.MaxTokensPerChunk.Should().Be(256);
        options.OverlapTokens.Should().Be(32);
        options.EmissionMode.Should().Be(HierarchicalEmissionMode.Interleaved);

        Action construct = () => _ = new HierarchicalTokenChunker(options);
        construct.Should().NotThrow();
    }

    [Fact]
    public void HierarchicalChunkerOptionsThrowsOnNonPositiveParentMaxTokens()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer);

        Action setZero = () => options.ParentMaxTokens = 0;
        setZero.Should().Throw<ArgumentOutOfRangeException>();

        Action setNegative = () => options.ParentMaxTokens = -50;
        setNegative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void HierarchicalChunkerOptionsThrowsOnNegativeParentOverlapTokens()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer);

        Action setNegative = () => options.ParentOverlapTokens = -1;
        setNegative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void HierarchicalChunkerRejectsAParentBudgetBelowTheChildBudget()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer)
        {
            ParentMaxTokens = 200,
            MaxTokensPerChunk = 256,
        };

        Action construct = () => _ = new HierarchicalTokenChunker(options);
        construct.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("options");
    }

    [Fact]
    public void HierarchicalChunkerRejectsAParentOverlapNotBelowTheParentBudget()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer)
        {
            ParentMaxTokens = 500,
            ParentOverlapTokens = 500,
            MaxTokensPerChunk = 200,
        };

        Action construct = () => _ = new HierarchicalTokenChunker(options);
        construct.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("options");
    }

    [Fact]
    public void HierarchicalChunkerRejectsAChildOverlapNotBelowTheChildBudget()
    {
        // The upstream setter may already reject this combination; either way construction must fail.
        Action construct = () => _ = new HierarchicalTokenChunker(new HierarchicalChunkerOptions(Tokenizer)
        {
            MaxTokensPerChunk = 64,
            OverlapTokens = 64,
        });
        construct.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void HierarchicalChunkerRejectsAnUndefinedEmissionMode()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer) { EmissionMode = (HierarchicalEmissionMode)42 };

        Action construct = () => _ = new HierarchicalTokenChunker(options);
        construct.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("options");
    }

    [Fact]
    public void HierarchicalChunkerOptionsAllowsChangingEmissionMode()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer)
        {
            EmissionMode = HierarchicalEmissionMode.ChildrenOnly,
        };

        options.EmissionMode.Should().Be(HierarchicalEmissionMode.ChildrenOnly);

        options.EmissionMode = HierarchicalEmissionMode.ParentsOnly;
        options.EmissionMode.Should().Be(HierarchicalEmissionMode.ParentsOnly);
    }
}
