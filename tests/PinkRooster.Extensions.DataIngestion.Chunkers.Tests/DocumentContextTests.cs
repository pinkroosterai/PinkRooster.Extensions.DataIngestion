using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Xunit;
using static PinkRooster.Extensions.DataIngestion.Chunkers.Tests.ContextualChunkProcessorTests;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class DocumentContextTests
{
    [Theory]
    [InlineData(14)]
    [InlineData(18)]
    public void FixedWindowsCoverTheTailAndRespectTokenLimits(int limit)
    {
        const string Body = "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima " +
            "mike november oscar papa quebec romeo sierra tango 😀";
        var original = Chunk(Body);
        var context = new DocumentContext(original.Document, new(TokenizerFixture.Instance, limit), 3,
            TestContext.Current.CancellationToken);

        string first = Find(context, new("alpha", original.Document));
        string last = Find(context, new("😀", original.Document));
        first.Should().StartWith("alpha");
        last.Should().EndWith("😀");
        first.Should().NotBe(last);
        Count(first).Should().BeLessThanOrEqualTo(limit);
        Count(last).Should().BeLessThanOrEqualTo(limit);
        char.IsLowSurrogate(last[0]).Should().BeFalse();
    }

    [Fact]
    public void AContextNamingOnlyTheInnermostHeadingStillSelectsItsSection()
    {
        // The anchor appears in both sections; only the heading tells them apart.
        var document = RecursiveTokenChunkerTests.Document(
            Header("# Parent"), Paragraph("shared marker in parent."), Paragraph(new string('x', 160)),
            new IngestionDocumentHeader("## Child") { Level = 2 }, Paragraph("shared marker in child."),
            Paragraph(new string('y', 160)));
        var context = new DocumentContext(document, new(TokenizerFixture.Instance, 14), 0,
            TestContext.Current.CancellationToken);

        Find(context, new("shared marker", document)).Should().Contain("in parent");
        Find(context, new("shared marker", document, "Child")).Should().NotContain("in parent").And.Contain("Child");
        Find(context, new("shared marker", document, "## Child")).Should().NotContain("in parent").And.Contain("Child");
    }

    [Fact]
    public void ChunksInTheSameWindowShareOneContextString()
    {
        const string Body = "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima " +
            "mike november oscar papa quebec romeo sierra tango";
        var original = Chunk(Body);
        var context = new DocumentContext(original.Document, new(TokenizerFixture.Instance, 14), 0,
            TestContext.Current.CancellationToken);

        string first = Find(context, new("alpha", original.Document));
        string again = Find(context, new("bravo", original.Document));
        string later = Find(context, new("tango", original.Document));

        again.Should().BeSameAs(first);
        later.Should().NotBe(first);
    }

    [Fact]
    public void AtOrBelowBudgetUsesTheWholeSourceWithoutAnAnchor()
    {
        const string Body = "The full source.";
        var original = Chunk(Body);
        foreach (int limit in new[] { Count(Body), Count(Body) + 1 })
        {
            var context = new DocumentContext(original.Document, new(TokenizerFixture.Instance, limit), 0,
                TestContext.Current.CancellationToken);
            Find(context, new("No match", original.Document)).Should().Be(Body);
        }
    }

    [Fact]
    public void RepeatedHeadingsUseNormalizedAnchorsAndBreakExactTiesBySourceOrder()
    {
        var document = RecursiveTokenChunkerTests.Document(
            Header("# Repeated"), Paragraph("alpha evidence goes here. exact duplicate marker."),
            Paragraph(new string('x', 160)),
            Header("# Repeated"), Paragraph("zulu evidence goes here. exact duplicate marker."));
        var context = new DocumentContext(document, new(TokenizerFixture.Instance, 14), 4,
            TestContext.Current.CancellationToken);

        Find(context, new("ZULU   EVIDENCE GOES HERE.", document, "# Repeated")).Should().Contain("zulu evidence");
        Find(context, new("exact duplicate marker", document, "# Repeated")).Should().NotContain("zulu evidence");
        context.Find(new("unmatchable caller text", document), TestContext.Current.CancellationToken)
            .Should().BeNull();
    }

    [Fact]
    public void EvictsSourceContextOnInstanceChangesEvenWhenIdentifiersMatch()
    {
        var one = Chunk("first chapter");
        var two = Chunk("second chapter");
        var token = TestContext.Current.CancellationToken;
        var cache = new DocumentContextCache(new(TokenizerFixture.Instance, 15), 0);

        one.Document.Identifier.Should().Be(two.Document.Identifier);
        var original = cache.Get(one.Document, token);
        cache.Get(one.Document, token).Should().BeSameAs(original);
        Find(cache.Get(two.Document, token), two).Should().Be("second chapter");
        var rebuilt = cache.Get(one.Document, token);
        rebuilt.Should().NotBeSameAs(original);
        Find(rebuilt, one).Should().Be("first chapter");
    }

    [Fact]
    public async Task UnresolvedAnchorUsesSelectedFailureMode()
    {
        var document = Chunk(new string('x', 200)).Document;
        var missing = new IngestionChunk<string>("a missing anchor", document);
        using var client = new ContextualChatClient((_, _, _) => throw new NotSupportedException("Unexpected chat call"));
        var options = Options(client);
        options.MaxDocumentContextTokens = 10;
        options.BucketOverlapTokens = 0;

        (await ReadAsync(new(options), missing)).Single().Should().BeSameAs(missing);
        options.FailureMode = ContextualFailureMode.Throw;
        Func<Task> strict = () => ReadAsync(new(options), missing);
        await strict.Should().ThrowAsync<InvalidOperationException>().WithMessage("*could not be located*");
    }

    [Fact]
    public async Task AcceptsRecursiveAndUpstreamHeaderChunkStreams()
    {
        var document = RecursiveTokenChunkerTests.Document(
            Header("# Dispatch"), Paragraph("Morning parcels leave from the north station."),
            Header("# Tracking"), Paragraph("Tracking updates appear once the parcel departs."));
        var token = TestContext.Current.CancellationToken;
        using var client = Reply("Local instructions");
        var options = Options(client);
        options.MaxDocumentContextTokens = 18;
        options.BucketOverlapTokens = 3;
        var processor = new ContextualChunkProcessor(options);
        IngestionChunker<string>[] chunkers =
        [
            RecursiveTokenChunkerTests.NewChunker(15),
            new HeaderChunker(new IngestionChunkerOptions(TokenizerFixture.Instance)
                { OverlapTokens = 0, MaxTokensPerChunk = 25 }),
        ];

        foreach (var chunker in chunkers)
        {
            int count = 0;
            await foreach (var chunk in processor.ProcessAsync(chunker.ProcessAsync(document, token), token))
            {
                chunk.Document.Should().BeSameAs(document);
                chunk.Content.Should().StartWith("Local instructions\n\n");
                count++;
            }

            count.Should().Be(2);
        }
    }

    private static string Find(DocumentContext context, IngestionChunk<string> chunk) =>
        context.Find(chunk, TestContext.Current.CancellationToken)
        ?? throw new InvalidOperationException("Expected a bucket.");

    private static IngestionDocumentHeader Header(string value) => new(value) { Level = 1 };
    private static IngestionDocumentParagraph Paragraph(string value) => new(value);
}
