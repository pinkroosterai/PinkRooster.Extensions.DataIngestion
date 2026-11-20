using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Xunit;
using static PinkRooster.Extensions.DataIngestion.Chunkers.Tests.RecursiveTokenChunkerTests;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class TableEdgeCaseTests
{
    // Characters outside the common vocabulary cost several tokens each in o200k_base.
    private static readonly char[] RareCandidates = ['⾟', '꧁', '༺', 'ᚠ', '⸺', 'ꓐ'];

    private static TableChunkerOptions TableOptions(int maxTokens) => new(TokenizerFixture.Instance)
    {
        MaxTokensPerChunk = maxTokens,
        OverlapTokens = 0,
    };

    private static async Task<List<IngestionChunk<string>>> ReadTableAsync(MarkdownTableChunker chunker, IngestionDocument document)
    {
        List<IngestionChunk<string>> chunks = [];
        await foreach (var chunk in chunker.ProcessAsync(document, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static char RareCharacter()
    {
        char rare = RareCandidates.FirstOrDefault(c => Count(c.ToString()) >= 2);
        rare.Should().NotBe('\0', "the fixture needs a character that costs more than one token");
        return rare;
    }

    [Fact]
    public async Task AHeaderWiderThanTheBudgetIsSlicedLosslesslyWithinBudget()
    {
        const string Markdown = "| Alpha column | Beta column | Gamma column |\n| --- | --- | --- |\n| a | b | c |";
        const int Limit = 4;
        Count("| Alpha column | Beta column | Gamma column |\n| --- | --- | --- |\n").Should().BeGreaterThan(Limit);

        var chunks = await ReadTableAsync(new MarkdownTableChunker(TableOptions(Limit)),
            Document(new IngestionDocumentParagraph(Markdown)));

        chunks.Count.Should().BeGreaterThan(1);
        string.Concat(chunks.Select(c => c.Content)).Should().Be(Markdown);
        AssertBudget(chunks, Limit);
        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].Metadata[ChunkMetadataKeys.TablePartIndex].Should().Be(i);
            chunks[i].Metadata[ChunkMetadataKeys.IsTableContinuation].Should().Be(i > 0);
        }
    }

    [Fact]
    public async Task ACharacterCostlierThanTheBudgetIsStillEmittedWithoutLosingText()
    {
        char rare = RareCharacter();
        string markdown = $"| {rare}{rare} |\n| --- |\n| x |";

        var chunks = await ReadTableAsync(new MarkdownTableChunker(TableOptions(1)),
            Document(new IngestionDocumentParagraph(markdown)));

        string.Concat(chunks.Select(c => c.Content)).Should().Be(markdown);
    }

    [Fact]
    public async Task ProseCostlierThanTheBudgetBeforeATableIsSlicedWithoutLosingText()
    {
        char rare = RareCharacter();
        string prose = new(rare, 3);
        const string Table = "| A |\n| --- |\n| b |";

        var chunks = await ReadTableAsync(new MarkdownTableChunker(TableOptions(1)),
            Document(new IngestionDocumentParagraph(prose + "\n\n" + Table)));

        string.Concat(chunks.Where(c => !c.Metadata.ContainsKey(ChunkMetadataKeys.IsTablePart)).Select(c => c.Content))
            .Should().Be(prose);
        string.Concat(chunks.Where(c => c.Metadata.ContainsKey(ChunkMetadataKeys.IsTablePart)).Select(c => c.Content))
            .Should().Be(Table);
    }

    [Fact]
    public async Task ADocumentTableWithoutADelimiterRowKeepsItsText()
    {
        const string Markdown = "| a | b |\n| c | d |";
        var table = new IngestionDocumentTable(Markdown, new IngestionDocumentElement?[2, 2]);

        var chunks = await ReadTableAsync(new MarkdownTableChunker(TableOptions(64)), Document(table));

        chunks.Should().ContainSingle();
        chunks[0].Content.Trim().Should().Be(Markdown);
        AssertBudget(chunks, 64);
    }

    [Fact]
    public async Task LineBreaksInsideCellsBecomeBreakTagsWhenTablesSplitByRow()
    {
        const string Header = "| A |\n| --- |\n";
        const string BrokenRow = "| line one<br />line two |";
        const string ShortRow = "| short |";
        string markdown = Header + BrokenRow + "\n" + ShortRow;
        var cells = new IngestionDocumentElement?[3, 1];
        cells[0, 0] = new IngestionDocumentParagraph("A");
        cells[1, 0] = new IngestionDocumentParagraph("line one\nline two");
        cells[2, 0] = new IngestionDocumentParagraph("short");
        int limit = Count(Header + BrokenRow);
        Count(markdown).Should().BeGreaterThan(limit);

        var chunks = await ReadAsync(NewChunker(limit), Document(new IngestionDocumentTable(markdown, cells)));

        chunks.Select(c => c.Content).Should().Contain(c => c.Contains("line one<br />line two", StringComparison.Ordinal));
        AssertBudget(chunks, limit);
    }

    // A document table whose Markdown is not a pipe table (here an HTML rendering) is split from its cells.
    private static IngestionDocumentTable HtmlTable(int rowCount, Func<int, string> second)
    {
        var cells = new IngestionDocumentElement?[rowCount + 1, 2];
        cells[0, 0] = new IngestionDocumentParagraph("Id");
        cells[0, 1] = new IngestionDocumentParagraph("Note");
        var html = new System.Text.StringBuilder("<table><tr><th>Id</th><th>Note</th></tr>");
        for (int i = 1; i <= rowCount; i++)
        {
            cells[i, 0] = new IngestionDocumentParagraph($"{i}");
            cells[i, 1] = new IngestionDocumentParagraph(second(i));
            html.Append($"<tr><td>{i}</td><td>{second(i)}</td></tr>");
        }

        return new IngestionDocumentTable(html.Append("</table>").ToString(), cells);
    }

    [Fact]
    public async Task CellsSplitByRowWithRepeatedHeadersEscapedPipesBreakTagsAndRowRanges()
    {
        const int Limit = 40;
        var table = HtmlTable(12, i => i switch
        {
            3 => "left|right",
            5 => "first\r\nsecond",
            _ => $"note {i}",
        });

        var chunks = await ReadAsync(NewChunker(Limit), Document(table));

        chunks.Count.Should().BeGreaterThan(1);
        AssertBudget(chunks, Limit);
        chunks.Should().AllSatisfy(c => c.Content.Should().StartWith("| Id | Note |\n| --- | --- |"));
        string all = string.Concat(chunks.Select(c => c.Content));
        all.Should().Contain("left\\|right").And.Contain("first<br />second");
        int expectedFirst = 1;
        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].Metadata[ChunkMetadataKeys.TablePartIndex].Should().Be(i);
            string[] range = ((string)chunks[i].Metadata[ChunkMetadataKeys.TableRowRange]).Split('-');
            int.Parse(range[0], System.Globalization.CultureInfo.InvariantCulture).Should().Be(expectedFirst);
            expectedFirst = int.Parse(range[1], System.Globalization.CultureInfo.InvariantCulture) + 1;
        }

        expectedFirst.Should().Be(13);
    }

    [Fact]
    public async Task CellsWithARowTooLargeForAnyWindowFallBackToLosslessSlices()
    {
        string longNote = string.Join(" ", Enumerable.Repeat("oversized", 40));
        var table = HtmlTable(3, i => i == 2 ? longNote : $"note {i}");
        const int Limit = 30;

        var chunks = await ReadAsync(NewChunker(Limit), Document(table));

        string.Concat(chunks.Select(c => c.Content)).Should().Be(table.GetMarkdown());
        AssertBudget(chunks, Limit);
        chunks.Select(c => c.Metadata[ChunkMetadataKeys.TableRowRange]).Should().AllBeEquivalentTo("0-0");
    }

    [Fact]
    public async Task ATableWithoutCellsAndWithoutPipeMarkdownIsSlicedLosslessly()
    {
        var source = HtmlTable(12, i => $"note {i}");
        var table = new IngestionDocumentTable(source.GetMarkdown(), new IngestionDocumentElement?[13, 2]);
        const int Limit = 30;

        var chunks = await ReadAsync(NewChunker(Limit), Document(table));

        chunks.Count.Should().BeGreaterThan(1);
        string.Concat(chunks.Select(c => c.Content)).Should().Be(table.GetMarkdown());
        AssertBudget(chunks, Limit);
    }

    [Fact]
    public async Task AFittingTableWithoutPipeMarkdownIsEmittedWhole()
    {
        var table = HtmlTable(2, i => $"note {i}");

        var chunks = await ReadAsync(NewChunker(500), Document(table));

        chunks.Should().ContainSingle();
        chunks[0].Content.Should().Be(table.GetMarkdown());
        chunks[0].Metadata[ChunkMetadataKeys.TableRowRange].Should().Be("1-2");
    }

    [Fact]
    public async Task AHeaderThatExactlyFillsTheBudgetStillSlicesItsRows()
    {
        const string Header = "| Name | Value |\n| --- | --- |\n";
        const string Markdown = Header + "| alpha | one |\n| beta | two |";
        int limit = Count(Header);

        var chunks = await ReadTableAsync(new MarkdownTableChunker(TableOptions(limit)),
            Document(new IngestionDocumentParagraph(Markdown)));

        chunks.Should().NotBeEmpty();
        AssertBudget(chunks.Where(c => !c.Content.StartsWith(Header, StringComparison.Ordinal)), limit);
        string.Concat(chunks.Select(c => c.Content)).Should().Contain("alpha").And.Contain("two");
    }
}
