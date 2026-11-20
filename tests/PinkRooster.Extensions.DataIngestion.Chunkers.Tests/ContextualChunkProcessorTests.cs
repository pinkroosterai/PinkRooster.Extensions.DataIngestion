using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class ContextualChunkProcessorTests
{
    [Fact]
    public async Task CopiesBodyDocumentContextAndMetadata()
    {
        using var client = Reply("Dispatch timing");
        var original = Chunk("Ships the same day.");
        original.Metadata["number"] = 42;
        original.Metadata[ChunkMetadataKeys.ContextualHeader] = "upstream value";

        var result = (await ReadAsync(new(Options(client)), original)).Single();

        result.Should().NotBeSameAs(original);
        result.Document.Should().BeSameAs(original.Document);
        result.Context.Should().Be(original.Context);
        result.Content.Should().Be("Dispatch timing\n\nShips the same day.");
        result.Metadata.Should().NotBeSameAs(original.Metadata);
        result.Metadata["number"].Should().Be(42);
        result.Metadata[ChunkMetadataKeys.ContextualHeader].Should().Be("Dispatch timing");
        original.Metadata[ChunkMetadataKeys.ContextualHeader].Should().Be("upstream value");
        original.Content.Should().Be("Ships the same day.");
    }

    [Fact]
    public async Task ExactBudgetIncludesJoinAndPreservesWholeScalars()
    {
        const string Header = "🙂🙂 extra text";
        const string Body = "world";
        int cap = Count("🙂\n\nworld");
        (Count("🙂") + Count(Body)).Should().NotBe(cap);
        using var client = Reply(Header);
        var options = Options(client);
        options.EmbeddingTokenizer = TokenizerFixture.Instance;
        options.MaxEnrichedTokens = cap;

        var result = (await ReadAsync(new(options), Chunk(Body))).Single();

        result.Content.Should().Be("🙂\n\nworld");
        result.Metadata[ChunkMetadataKeys.ContextualHeader].Should().Be("🙂");
        Count(result.Content).Should().Be(cap);
    }

    [Fact]
    public async Task OmitsHeaderWhenNoJoinedContentFits()
    {
        using var client = Reply("Context");
        var original = Chunk("word");
        var options = Options(client);
        options.EmbeddingTokenizer = TokenizerFixture.Instance;
        options.MaxEnrichedTokens = Count(original.Content);

        (await ReadAsync(new(options), original)).Should().ContainSingle().Which.Should().BeSameAs(original);
    }

    [Fact]
    public async Task OversizedBodyFailsBeforeProvider()
    {
        using var client = new ContextualChatClient((_, _, _) => throw new NotSupportedException("Unexpected chat call"));
        var options = Options(client);
        options.EmbeddingTokenizer = TokenizerFixture.Instance;
        options.MaxEnrichedTokens = 1;
        Func<Task> run = () => ReadAsync(new(options), Chunk("An input above the cap"));

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*original chunk*");
    }

    [Fact]
    public void RejectsInvalidOptions()
    {
        using var client = Reply("x");
        Action missingClient = () => _ = new ContextualChunkProcessorOptions(null!, TokenizerFixture.Instance);
        Action missingTokenizer = () => _ = new ContextualChunkProcessorOptions(client, null!);
        Action missingOptions = () => _ = new ContextualChunkProcessor(null!);
        missingClient.Should().Throw<ArgumentNullException>();
        missingTokenizer.Should().Throw<ArgumentNullException>();
        missingOptions.Should().Throw<ArgumentNullException>();

        var options = Options(client);
        Action invalid = () => _ = new ContextualChunkProcessor(options);
        options.BucketOverlapTokens = -1;
        invalid.Should().Throw<ArgumentOutOfRangeException>();
        options.BucketOverlapTokens = options.MaxDocumentContextTokens;
        invalid.Should().Throw<ArgumentOutOfRangeException>();
        options.BucketOverlapTokens = 0;
        options.MaxDocumentContextTokens = 0;
        invalid.Should().Throw<ArgumentOutOfRangeException>();
        options.MaxDocumentContextTokens = 100;
        options.MaxDegreeOfParallelism = 0;
        invalid.Should().Throw<ArgumentOutOfRangeException>();
        options.MaxDegreeOfParallelism = 1;
        options.FailureMode = (ContextualFailureMode)42;
        invalid.Should().Throw<ArgumentOutOfRangeException>();
        options.FailureMode = ContextualFailureMode.Throw;
        options.MaxEnrichedTokens = 0;
        invalid.Should().Throw<ArgumentOutOfRangeException>();
        options.MaxEnrichedTokens = 1;
        invalid.Should().Throw<ArgumentException>().WithParameterName("options");
    }

    internal static ContextualChunkProcessorOptions Options(IChatClient client) => new(client, TokenizerFixture.Instance);

    internal static IngestionChunk<string> Chunk(string body) => new(body,
        RecursiveTokenChunkerTests.Document(new IngestionDocumentParagraph(body)), "# Orders");

    internal static ContextualChatClient Reply(string header) => new((_, _, _) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, header))));

    internal static int Count(string text) =>
        TokenizerFixture.Instance.CountTokens(text, considerNormalization: false);

    internal static async Task<List<IngestionChunk<string>>> ReadAsync(
        ContextualChunkProcessor processor, params IngestionChunk<string>[] chunks)
    {
        List<IngestionChunk<string>> results = [];
        await foreach (var result in processor.ProcessAsync(Enumerate(chunks), TestContext.Current.CancellationToken))
        {
            results.Add(result);
        }

        return results;
    }

    internal static async IAsyncEnumerable<IngestionChunk<string>> Enumerate(params IngestionChunk<string>[] chunks)
    {
        await Task.CompletedTask;
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }
    }
}
