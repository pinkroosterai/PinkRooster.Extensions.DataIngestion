using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Xunit;
using static PinkRooster.Extensions.DataIngestion.Chunkers.Tests.RecursiveTokenChunkerTests;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class ProtectedStructureTests
{
    [Fact]
    public async Task EmitsFittingAndHeaderOnlyTablesIntact()
    {
        const string Markdown = "| A | B |\r\n| --- | --- |";
        var table = Table(Markdown, ["A", "B"]);
        var chunks = await ReadAsync(NewChunker(Count(Markdown)), Document(table));

        chunks.Select(c => c.Content).Should().Equal(Markdown);
    }

    [Fact]
    public async Task SplitsTablesByRowAndPreservesEscapedPipesAndEmptyCells()
    {
        const string Header = "| A | B |\n| --- | --- |\n";
        string[] rows = ["| left | x\\|y |", "| next |  |", "| final | `u\\|v` |"];
        string markdown = Header + string.Join('\n', rows);
        int limit = rows.Max(row => Count(Header + row));
        var table = Table(markdown, ["A", "B"], ["left", "x|y"], ["next", null], ["final", "`u\\|v`"]);

        var chunks = await ReadAsync(NewChunker(limit), Document(table));

        chunks.Select(c => c.Content).Should().Equal(rows.Select(row => Header + row));
        AssertBudget(chunks, limit);
    }

    [Fact]
    public async Task ARowThatFitsOnlyInAnEmptyChunkFollowsEarlierRows()
    {
        const string Header = "| A |\n| --- |\n";
        const string First = "| one |";
        const string Last = "| two three four |";
        int limit = Count(Header + Last);
        var chunks = await ReadAsync(NewChunker(limit),
            Document(Table(Header + First + "\n" + Last, ["A"], ["one"], ["two three four"])));

        chunks.Select(c => c.Content).Should().Equal(Header + First, Header + Last);
        AssertBudget(chunks, limit);
    }

    [Fact]
    public async Task ATooLargeLaterRowFallsBackWithoutDuplicatingAlreadyFittingRows()
    {
        const string Header = "| A |\n| --- |\n";
        string longCell = string.Join(" ", Enumerable.Repeat("oversized", 30));
        string markdown = Header + "| short |\n| " + longCell + " |";
        int limit = Count(Header + "| short |");
        var table = Table(markdown, ["A"], ["short"], [longCell]);

        var chunks = await ReadAsync(NewChunker(limit), Document(table));

        chunks.Count.Should().BeGreaterThan(1);
        string.Concat(chunks.Select(c => c.Content)).Should().Be(markdown);
        AssertBudget(chunks, limit);
    }

    [Fact]
    public async Task HeaderOnlyOversizedTableAlsoUsesLosslessFallback()
    {
        const string Markdown = "| one two three |\n| --- |";
        var chunks = await ReadAsync(NewChunker(4), Document(Table(Markdown, ["one two three"])));

        chunks.Count.Should().BeGreaterThan(1);
        string.Concat(chunks.Select(c => c.Content)).Should().Be(Markdown);
        AssertBudget(chunks, 4);
    }

    [Theory]
    [InlineData("```")]
    [InlineData("~~~")]
    public async Task FencesKeepTheInfoStringAndPreferWholeLines(string marker)
    {
        string opening = marker + "csharp extra\n";
        string[] lines = ["one two three\n", "four five six\n", "seven eight nine\n"];
        string fence = opening + string.Concat(lines) + marker;
        int limit = lines.Max(line => Count(opening + line + marker));

        var chunks = await ReadAsync(NewChunker(limit), Document(new IngestionDocumentParagraph(fence)));

        chunks.Select(c => c.Content).Should().Equal(lines.Select(line => opening + line + marker));
        AssertBudget(chunks, limit);
    }

    [Fact]
    public async Task SplitsLongFenceLineWithoutDroppingCode()
    {
        const string Opening = "```text\n";
        const string Closing = "```";
        string body = new('a', 160);
        int limit = Count(Opening + "aaaaaaaa\n" + Closing);
        var chunks = await ReadAsync(NewChunker(limit), Document(new IngestionDocumentParagraph(Opening + body + "\n" + Closing)));

        chunks.Count.Should().BeGreaterThan(1);
        chunks.All(c => c.Content.StartsWith(Opening, StringComparison.Ordinal) &&
            c.Content.EndsWith("\n" + Closing, StringComparison.Ordinal)).Should().BeTrue();
        string.Concat(chunks.Select(c => c.Content[Opening.Length..^(Closing.Length + 1)])).Should().Be(body);
        AssertBudget(chunks, limit);
    }

    [Fact]
    public async Task FallsBackLosslesslyWhenOnlyTheLaterFenceContentCannotFitWithWrappers()
    {
        const string Opening = "```text\n";
        const string Markdown = Opening + "a\n𐐷\n```";
        int limit = Count(Opening + "a\n```");
        Count(Opening + "𐐷\n```").Should().BeGreaterThan(limit);

        var chunks = await ReadAsync(NewChunker(limit), Document(new IngestionDocumentParagraph(Markdown)));

        chunks.Count.Should().BeGreaterThan(1);
        string.Concat(chunks.Select(c => c.Content)).Should().Be(Markdown);
        AssertBudget(chunks, limit);
    }

    [Fact]
    public async Task UnattachableWhitespaceFailsBeforeEmittingAnyPartOfTheTable()
    {
        var table = Table("|" + new string(' ', 500) + "|", ["A"]);
        using var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var document = Document(new IngestionDocumentParagraph("before"), table);
        await using var reader = NewChunker(1).ProcessAsync(document, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(tokenSource.Token);
        (await reader.MoveNextAsync()).Should().BeTrue();
        reader.Current.Content.Should().Be("before");

        Func<Task> next = async () => { await reader.MoveNextAsync(); };
        await next.Should().ThrowAsync<InvalidOperationException>().WithMessage("*IngestionDocumentTable*1*");
    }

    [Fact]
    public async Task FittingFenceInMixedProseIsIntactAndResetsOverlap()
    {
        const string Fence = "```text\r\none\r\n```\r\n";
        const string Text = "before here\r\n" + Fence + "after here";
        int limit = Count(Text) + 1;
        var chunks = await ReadAsync(NewChunker(limit, overlap: 1), Document(new IngestionDocumentParagraph(Text)));

        chunks.Select(c => c.Content).Should().Equal("before here\r\n", Fence, "after here");
        AssertBudget(chunks, limit);
    }

    [Fact]
    public async Task UnterminatedFenceAndDisabledProtectionUseProseSplitting()
    {
        const string Unclosed = "before\n\n```text\nsome text";
        const string Closed = Unclosed + "\n```";
        var disabled = Options(4);
        disabled.PreserveAtomicElements = false;
        var unclosed = await ReadAsync(NewChunker(4), Document(new IngestionDocumentParagraph(Unclosed)));
        var unprotected = await ReadAsync(new(disabled), Document(new IngestionDocumentParagraph(Closed)));

        string.Concat(unclosed.Select(c => c.Content)).Should().Be(Unclosed);
        string.Concat(unprotected.Select(c => c.Content)).Should().Be(Closed);
        AssertBudget(unclosed, 4);
        AssertBudget(unprotected, 4);
    }

    private static IngestionDocumentTable Table(string markdown, params string?[][] rows)
    {
        var cells = new IngestionDocumentElement?[rows.Length, rows[0].Length];
        for (int row = 0; row < rows.Length; row++)
        {
            for (int col = 0; col < rows[row].Length; col++)
            {
                if (rows[row][col] is { } cell)
                {
                    cells[row, col] = new IngestionDocumentParagraph(cell);
                }
            }
        }

        return new IngestionDocumentTable(markdown, cells);
    }
}
