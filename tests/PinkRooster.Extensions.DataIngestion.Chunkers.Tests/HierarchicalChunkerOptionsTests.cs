using AwesomeAssertions;
using Microsoft.ML.Tokenizers;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class HierarchicalChunkerOptionsTests
{
    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

    [Fact]
    public void HierarchicalChunkerOptionsDefaultsAreCorrect()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer);

        options.ParentMaxTokens.Should().Be(1024);
        options.ParentOverlapTokens.Should().Be(0);
        options.MaxTokensPerChunk.Should().Be(256);
        options.OverlapTokens.Should().Be(32);
        options.EmissionMode.Should().Be(HierarchicalEmissionMode.Interleaved);

        Action validate = () => options.Validate();
        validate.Should().NotThrow();
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
    public void HierarchicalChunkerOptionsValidateThrowsWhenParentLessThanChildBudget()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer)
        {
            ParentMaxTokens = 200,
            MaxTokensPerChunk = 256,
        };

        Action validate = () => options.Validate();
        validate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void HierarchicalChunkerOptionsValidateThrowsWhenParentOverlapEqualsOrExceedsParentMaxTokens()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer)
        {
            ParentMaxTokens = 500,
            ParentOverlapTokens = 500,
            MaxTokensPerChunk = 200,
        };

        Action validate = () => options.Validate();
        validate.Should().Throw<InvalidOperationException>();
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
