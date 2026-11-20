using AwesomeAssertions;
using Xunit;
using static PinkRooster.Extensions.DataIngestion.Chunkers.Tests.ContextualChunkProcessorTests;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

/// <summary>Invalid option values are reported against the constructor's actual parameter, <c>options</c>.</summary>
public sealed class ArgumentContractTests
{
    [Fact]
    public void RecursiveChunkerReportsInvalidOptionsAgainstTheOptionsParameter()
    {
        Action reserved = () => _ = new RecursiveTokenChunker(new RecursiveChunkerOptions(TokenizerFixture.Instance)
            { MaxTokensPerChunk = 10, OverlapTokens = 0, ReservedHeaderTokens = 10 });
        Action separator = () => _ = new RecursiveTokenChunker(new RecursiveChunkerOptions(TokenizerFixture.Instance)
            { MaxTokensPerChunk = 10, OverlapTokens = 0, KeepSeparator = (SeparatorPlacement)42 });

        reserved.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("options");
        separator.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("options");
    }

    [Fact]
    public void TableChunkerReportsInvalidOptionsAgainstTheOptionsParameter()
    {
        Action reserved = () => _ = new MarkdownTableChunker(new TableChunkerOptions(TokenizerFixture.Instance)
            { MaxTokensPerChunk = 10, ReservedHeaderTokens = 10 });

        reserved.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("options");
    }

    [Fact]
    public void ContextualProcessorReportsInvalidOptionsAgainstTheOptionsParameter()
    {
        using var client = Reply("unused");
        var failureMode = Options(client);
        failureMode.FailureMode = (ContextualFailureMode)42;
        var cap = Options(client);
        cap.MaxEnrichedTokens = 0;
        cap.EmbeddingTokenizer = TokenizerFixture.Instance;
        var tokenizer = Options(client);
        tokenizer.MaxEnrichedTokens = 100;
        tokenizer.EmbeddingTokenizer = null;

        Action undefinedMode = () => _ = new ContextualChunkProcessor(failureMode);
        Action nonPositiveCap = () => _ = new ContextualChunkProcessor(cap);
        Action missingTokenizer = () => _ = new ContextualChunkProcessor(tokenizer);

        undefinedMode.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("options");
        nonPositiveCap.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("options");
        missingTokenizer.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("options");
    }
}
