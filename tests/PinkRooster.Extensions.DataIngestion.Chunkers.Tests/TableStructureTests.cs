using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class TableStructureTests
{
    private static readonly Tokenizer Tokenizer = TokenizerFixture.Instance;

    private static int Count(string text) => Tokenizer.CountTokens(text);

    private static TokenBudget Budget(int limit) => new(Tokenizer, limit);

    [Fact]
    public void MarkdownTableFindIdentifiesSingleTable()
    {
        const string Markdown = "| Name | Age |\n| :--- | ---: |\n| Alice | 30 |\n| Bob | 25 |\n";
        List<MarkdownTable> tables = [.. MarkdownTable.Find(Markdown, TestContext.Current.CancellationToken)];

        tables.Should().HaveCount(1);
        tables[0].ColumnCount.Should().Be(2);
        tables[0].RowCount.Should().Be(2);
        tables[0].Start.Should().Be(0);
        tables[0].End.Should().Be(Markdown.Length);
    }

    [Fact]
    public void MarkdownTableFindIgnoresTablesInsideCodeFences()
    {
        const string Markdown = "```markdown\n| Header 1 | Header 2 |\n| --- | --- |\n| Cell 1 | Cell 2 |\n```\n\n" +
                               "Outside text\n\n| Real | Table |\n| --- | --- |\n| Data | Val |\n";
        List<MarkdownTable> tables = [.. MarkdownTable.Find(Markdown, TestContext.Current.CancellationToken)];

        tables.Should().HaveCount(1);
        Markdown[tables[0].Start..tables[0].End].Should().StartWith("| Real | Table |");
    }

    [Fact]
    public void MarkdownTableFindRejectsInvalidDelimiterRows()
    {
        const string NotATable = "| Header 1 | Header 2 |\n| not a valid delimiter |\n| Cell 1 | Cell 2 |\n";
        List<MarkdownTable> tables = [.. MarkdownTable.Find(NotATable, TestContext.Current.CancellationToken)];

        tables.Should().BeEmpty();
    }

    [Fact]
    public void TableSplitterEmitsFittingTableIntactWithMetadata()
    {
        const string Markdown = "| Col 1 | Col 2 |\n| --- | --- |\n| Val 1 | Val 2 |\n";
        var table = MarkdownTable.Find(Markdown, TestContext.Current.CancellationToken).Single();
        var splitter = new TableSplitter(Budget(Count(Markdown) + 10));

        var chunks = splitter.Split(Markdown, table, TestContext.Current.CancellationToken).ToList();

        chunks.Should().HaveCount(1);
        chunks[0].Content.TrimEnd().Should().Be(Markdown.TrimEnd());
        chunks[0].PartIndex.Should().Be(0);
        chunks[0].IsContinuation.Should().BeFalse();
        chunks[0].StartRow.Should().Be(1);
        chunks[0].EndRow.Should().Be(1);

        var doc = new IngestionDocument("doc1");
        var ingestionChunk = TableSplitter.CreateChunk(chunks[0], doc);
        ingestionChunk.Metadata[ChunkMetadataKeys.IsTablePart].Should().Be(true);
        ingestionChunk.Metadata[ChunkMetadataKeys.IsTableContinuation].Should().Be(false);
        ingestionChunk.Metadata[ChunkMetadataKeys.TablePartIndex].Should().Be(0);
        ingestionChunk.Metadata[ChunkMetadataKeys.TableRowRange].Should().Be("1-1");
    }

    [Fact]
    public void TableSplitterRepeatsHeaderOnContinuationChunks()
    {
        const string Header = "| Col A | Col B |\n| :--- | ---: |\n";
        string[] rows =
        [
            "| Row 1 A | Row 1 B |\n",
            "| Row 2 A | Row 2 B |\n",
            "| Row 3 A | Row 3 B |\n",
            "| Row 4 A | Row 4 B |\n"
        ];
        string markdown = Header + string.Concat(rows);

        var table = MarkdownTable.Find(markdown, TestContext.Current.CancellationToken).Single();
        int rowLimit = Count(Header + rows[0]);
        var splitter = new TableSplitter(Budget(rowLimit));

        var chunks = splitter.Split(markdown, table, TestContext.Current.CancellationToken).ToList();

        chunks.Count.Should().BeGreaterThan(1);
        foreach (var chunk in chunks)
        {
            chunk.Content.Should().StartWith(Header);
            Count(chunk.Content).Should().BeLessThanOrEqualTo(rowLimit);
        }

        chunks[0].IsContinuation.Should().BeFalse();
        chunks[0].PartIndex.Should().Be(0);
        chunks[1].IsContinuation.Should().BeTrue();
        chunks[1].PartIndex.Should().Be(1);
    }

    [Fact]
    public void TableSplitterPreservesEscapedPipesAndEmptyCells()
    {
        const string Markdown = "| Name | Value |\n| --- | --- |\n| escaped | a\\|b |\n| empty |  |\n";
        var table = MarkdownTable.Find(Markdown, TestContext.Current.CancellationToken).Single();
        var splitter = new TableSplitter(Budget(50));

        var chunks = splitter.Split(Markdown, table, TestContext.Current.CancellationToken).ToList();

        chunks.Should().HaveCount(1);
        chunks[0].Content.Should().Contain("a\\|b");
        chunks[0].Content.Should().Contain("| empty |  |");
    }

    [Fact]
    public void TableSplitterHandlesOversizedSingleRowGracefully()
    {
        const string Header = "| Header |\n| --- |\n";
        string longRow = "| " + string.Join(" ", Enumerable.Repeat("hugecell", 30)) + " |\n";
        string markdown = Header + "| normal |\n" + longRow + "| normal 2 |\n";

        var table = MarkdownTable.Find(markdown, TestContext.Current.CancellationToken).Single();
        int limit = Count(Header + "| normal 2 |\n");
        var splitter = new TableSplitter(Budget(limit));

        var chunks = splitter.Split(markdown, table, TestContext.Current.CancellationToken).ToList();

        chunks.Count.Should().BeGreaterThan(2);
        // All chunks must strictly fit within budget limit
        foreach (var chunk in chunks)
        {
            Count(chunk.Content).Should().BeLessThanOrEqualTo(limit);
        }

        // The first chunk has the normal row with header
        chunks[0].Content.Should().StartWith(Header);
        chunks[0].Content.Should().Contain("| normal |");

        // The subsequent normal row still receives the table header
        var lastChunk = chunks[^1];
        lastChunk.Content.Should().StartWith(Header);
        lastChunk.Content.Should().Contain("| normal 2 |");
    }

    [Fact]
    public void TableSplitterHandlesHeaderOnlyTable()
    {
        const string Markdown = "| Only | Headers |\n| :--- | :--- |\n";
        var table = MarkdownTable.Find(Markdown, TestContext.Current.CancellationToken).Single();
        var splitter = new TableSplitter(Budget(50));

        var chunks = splitter.Split(Markdown, table, TestContext.Current.CancellationToken).ToList();

        chunks.Should().HaveCount(1);
        chunks[0].Content.Should().Be(Markdown.TrimEnd('\n'));
        chunks[0].StartRow.Should().Be(0);
        chunks[0].EndRow.Should().Be(0);
    }
}
