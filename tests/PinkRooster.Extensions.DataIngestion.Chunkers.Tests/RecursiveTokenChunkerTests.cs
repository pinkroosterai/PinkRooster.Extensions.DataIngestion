using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class RecursiveTokenChunkerTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task KeepsOriginalLineEndings(string newline)
    {
        string text = $"one{newline}{newline}two{newline}three";
        var chunks = await ReadAsync(NewChunker(2), Document(new IngestionDocumentParagraph(text)));

        string.Concat(chunks.Select(chunk => chunk.Content)).Should().Be(text);
        chunks.Count.Should().BeGreaterThan(1);
        AssertBudget(chunks, 2);
    }

    [Theory]
    [InlineData(SeparatorPlacement.None, "alpha", "betagamma")]
    [InlineData(SeparatorPlacement.Leading, "alpha|beta", "|gamma")]
    [InlineData(SeparatorPlacement.Trailing, "alpha|", "beta|", "gamma")]
    public async Task UsesConfiguredSeparatorPlacement(SeparatorPlacement placement, params string[] expected)
    {
        var options = Options(2);
        options.Separators = ["|"];
        options.KeepSeparator = placement;
        var chunks = await ReadAsync(new(options), Document(new IngestionDocumentParagraph("alpha|beta|gamma")));

        string.Concat(chunks.Select(c => c.Content)).Should().Be(string.Concat(expected));
        if (placement == SeparatorPlacement.Leading)
        {
            chunks.Skip(1).All(c => c.Content.StartsWith('|')).Should().BeTrue();
        }
        else if (placement == SeparatorPlacement.Trailing)
        {
            chunks.SkipLast(1).All(c => c.Content.EndsWith('|')).Should().BeTrue();
        }

        AssertBudget(chunks, 2);
    }

    [Fact]
    public async Task AvoidsFalseSentenceBreaksInAbbreviationsAndVersions()
    {
        const string FirstSentence = "Dr. Lee uses v1.2.3, e.g. here. ";
        const string SecondSentence = "Another sentence follows.";
        int limit = Count(FirstSentence);
        var chunks = await ReadAsync(NewChunker(limit),
            Document(new IngestionDocumentParagraph(FirstSentence + SecondSentence)));

        chunks.Select(c => c.Content).Should().Equal(FirstSentence, SecondSentence);
        AssertBudget(chunks, limit);
    }

    [Fact]
    public async Task CustomSeparatorsWithNewlinePreservesCrlfPairWithoutSplitting()
    {
        var options = new RecursiveChunkerOptions(TokenizerFixture.Instance)
        {
            MaxTokensPerChunk = 5,
            Separators = ["\n", " "]
        };
        var chunker = new RecursiveTokenChunker(options);
        const string Text = "first line\r\nsecond line";
        var chunks = await ReadAsync(chunker, Document(new IngestionDocumentParagraph(Text)));

        chunks.Should().NotBeEmpty();
        chunks.Should().AllSatisfy(c => c.Content.Should().NotEndWith("\r").And.NotStartWith("\n"));
        string.Concat(chunks.Select(c => c.Content)).Should().Be(Text);
    }

    [Fact]
    public async Task CustomSeparatorsWithSentenceSeparatorPreservesAbbreviations()
    {
        const string FirstSentence = "Dr. Lee visited. ";
        const string SecondSentence = "Next sentence.";
        int limit = Count(FirstSentence);
        var options = new RecursiveChunkerOptions(TokenizerFixture.Instance)
        {
            MaxTokensPerChunk = limit,
            Separators = [". ", " "]
        };
        var chunker = new RecursiveTokenChunker(options);
        var chunks = await ReadAsync(chunker, Document(new IngestionDocumentParagraph(FirstSentence + SecondSentence)));

        chunks.Select(c => c.Content).Should().Equal(FirstSentence, SecondSentence);
    }

    [Fact]
    public async Task TracksNestedAndFlatHeadingContextAndResumesParent()
    {
        var child = new IngestionDocumentSection();
        child.Elements.Add(Header("## Child", 2));
        child.Elements.Add(new IngestionDocumentParagraph("child body"));
        var document = Document(Header("# Parent", 1), new IngestionDocumentParagraph("parent body"), child,
            new IngestionDocumentParagraph("parent again"), Header("Temporary", null),
            new IngestionDocumentParagraph("temporary body"), Header("# Next", 1),
            new IngestionDocumentParagraph("next body"));
        var nextSection = new IngestionDocumentSection();
        nextSection.Elements.Add(new IngestionDocumentParagraph("no heading"));
        document.Sections.Add(nextSection);
        var chunker = NewChunker(30, overlap: 2);

        var chunks = await ReadAsync(chunker, document);

        chunks.Select(c => c.Content).Should().Equal(
            "parent body", "child body", "parent again", "temporary body", "next body", "no heading");
        chunks.Select(c => c.Context).Should().Equal(
            "Parent", "Parent > Child", "Parent", "Parent > Temporary", "Next", null);
        chunks.All(c => ReferenceEquals(c.Document, document)).Should().BeTrue();
        (await ReadAsync(chunker, document)).Select(c => (c.Content, c.Context))
            .Should().Equal(chunks.Select(c => (c.Content, c.Context)));
    }

    [Fact]
    public async Task SkipsEmptyHeadingOnlyAndWhitespaceInputs()
    {
        var chunker = NewChunker(10);
        (await ReadAsync(chunker, new IngestionDocument("empty"))).Should().BeEmpty();
        (await ReadAsync(chunker, Document(Header("# Empty", 1),
            new IngestionDocumentParagraph("  ")))).Should().BeEmpty();
    }

    [Fact]
    public async Task CountsJoiningWhitespaceAndReservedHeadroom()
    {
        var options = Options(3);
        options.ReservedHeaderTokens = 1;
        var chunks = await ReadAsync(new(options), Document(
            new IngestionDocumentParagraph("hello"), new IngestionDocumentParagraph("world")));

        (Count("hello") + Count("world")).Should().Be(2);
        Count("hello\nworld").Should().BeGreaterThan(2);
        chunks.Select(c => c.Content).Should().Equal("hello", "world");
        AssertBudget(chunks, 2);
    }

    [Fact]
    public async Task KeepsOverlapWhenThereIsRoomForNewText()
    {
        var options = Options(3, overlap: 1);
        options.KeepSeparator = SeparatorPlacement.Leading;
        var chunks = await ReadAsync(new(options), Document(new IngestionDocumentParagraph("one two three four")));

        chunks.Select(c => c.Content).Should().Equal("one two three", " three four");
        AssertBudget(chunks, 3);
    }

    [Fact]
    public async Task DropsOverlapWhenItWouldPreventProgress()
    {
        var chunks = await ReadAsync(NewChunker(2, overlap: 1), Document(
            new IngestionDocumentParagraph("one two"), new IngestionDocumentParagraph("three")));

        chunks.Select(c => c.Content).Should().Equal("one two", "three");
        AssertBudget(chunks, 2);
    }

    [Theory]
    [InlineData("漢字漢字漢字")]
    [InlineData("🙂🙂🙂🙂🙂")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz")]
    public async Task PreservesLongTextAtUnicodeScalarBoundaries(string text)
    {
        var chunks = await ReadAsync(NewChunker(2), Document(new IngestionDocumentParagraph(text)));

        chunks.Count.Should().BeGreaterThan(1);
        string.Concat(chunks.Select(c => c.Content)).Should().Be(text);
        chunks.All(c => !char.IsLowSurrogate(c.Content[0]) && !char.IsHighSurrogate(c.Content[^1])).Should().BeTrue();
        AssertBudget(chunks, 2);
    }

    [Fact]
    public async Task ReportsBudgetWhenNoValidScalarCanFit()
    {
        const string Scalar = "𐐷";
        Count(Scalar).Should().BeGreaterThan(1);
        Func<Task> run = () => ReadAsync(NewChunker(1), Document(new IngestionDocumentParagraph(Scalar)));

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*IngestionDocumentParagraph*1*");
    }

    [Fact]
    public void RejectsInvalidOptions()
    {
        var invalidReserve = Options(5);
        invalidReserve.ReservedHeaderTokens = -1;
        Action negative = () => _ = new RecursiveTokenChunker(invalidReserve);
        negative.Should().Throw<ArgumentOutOfRangeException>();
        invalidReserve.ReservedHeaderTokens = 5;
        negative.Should().Throw<ArgumentOutOfRangeException>();

        var invalidOverlap = Options(5, overlap: 2);
        invalidOverlap.ReservedHeaderTokens = 3;
        Action overlap = () => _ = new RecursiveTokenChunker(invalidOverlap);
        overlap.Should().Throw<ArgumentOutOfRangeException>();

        var invalidSeparators = Options(5);
        invalidSeparators.Separators = ["", "|"];
        Action separators = () => _ = new RecursiveTokenChunker(invalidSeparators);
        separators.Should().Throw<ArgumentException>();
        invalidSeparators.Separators = [null!];
        separators.Should().Throw<ArgumentException>();

        var invalidPlacement = Options(5);
        invalidPlacement.KeepSeparator = (SeparatorPlacement)42;
        Action placement = () => _ = new RecursiveTokenChunker(invalidPlacement);
        placement.Should().Throw<ArgumentOutOfRangeException>();

        Action missing = () => _ = new RecursiveTokenChunker(null!);
        missing.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ObservesCancellationBeforeAndAfterFirstYield()
    {
        var chunker = NewChunker(1);
        var document = Document(new IngestionDocumentParagraph("one two three four"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Func<Task> before = () => CollectAsync(chunker, document, cancelled.Token);
        await before.Should().ThrowAsync<OperationCanceledException>();

        using var pending = new CancellationTokenSource();
        await using var enumerator = chunker.ProcessAsync(document, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(pending.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        await pending.CancelAsync();
        Func<Task> after = async () => { await enumerator.MoveNextAsync(); };
        await after.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ProtectedBlockWithTrailingWhitespaceDoesNotThrowNoFit()
    {
        string tableWithTrailing = """
            | Column A | Column B |
            | --- | --- |
            | Value 1 | Value 2 |
            | Value 3 | Value 4 |
            

            """;
        var table = new IngestionDocumentTable(tableWithTrailing, new IngestionDocumentElement?[3, 2]);
        var document = Document(table);
        var chunker = NewChunker(15);
        var chunks = await ReadAsync(chunker, document);
        chunks.Should().NotBeEmpty();
        AssertBudget(chunks, 15);
    }

    [Fact]
    public async Task BlankHeadersAreIgnoredInBreadcrumbs()
    {
        var h1 = Header("Parent Title", 1);
        var blank = Header("   ", 2);
        var p = new IngestionDocumentParagraph("Paragraph content under parent.");
        var document = Document(h1, blank, p);
        var chunker = NewChunker(50);
        var chunks = await ReadAsync(chunker, document);
        chunks.Should().ContainSingle();
        chunks[0].Context.Should().Be("Parent Title");
    }

    internal static RecursiveChunkerOptions Options(int limit, int overlap = 0) => new(TokenizerFixture.Instance)
    {
        OverlapTokens = overlap,
        MaxTokensPerChunk = limit,
    };

    internal static RecursiveTokenChunker NewChunker(int limit, int overlap = 0) => new(Options(limit, overlap));

    internal static IngestionDocument Document(params IngestionDocumentElement[] elements)
    {
        var section = new IngestionDocumentSection();
        foreach (var element in elements)
        {
            section.Elements.Add(element);
        }

        var document = new IngestionDocument("fixture");
        document.Sections.Add(section);
        return document;
    }

    internal static Task<List<IngestionChunk<string>>> ReadAsync(
        RecursiveTokenChunker chunker, IngestionDocument document) =>
        CollectAsync(chunker, document, TestContext.Current.CancellationToken);

    private static async Task<List<IngestionChunk<string>>> CollectAsync(
        RecursiveTokenChunker chunker, IngestionDocument document, CancellationToken cancellationToken)
    {
        List<IngestionChunk<string>> chunks = [];
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, TestContext.Current.CancellationToken);
        await foreach (var chunk in chunker.ProcessAsync(document, linked.Token))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    internal static int Count(string text) => TokenizerFixture.Instance.CountTokens(text, considerNormalization: false);

    internal static void AssertBudget(IEnumerable<IngestionChunk<string>> chunks, int limit)
    {
        foreach (var chunk in chunks)
        {
            chunk.Content.Should().NotBeNullOrWhiteSpace();
            Count(chunk.Content).Should().BeLessThanOrEqualTo(limit);
        }
    }

    private static IngestionDocumentHeader Header(string markdown, int? level) => new(markdown) { Level = level };
}
