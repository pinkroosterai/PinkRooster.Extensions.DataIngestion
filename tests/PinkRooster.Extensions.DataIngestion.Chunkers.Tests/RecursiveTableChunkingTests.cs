using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class RecursiveTableChunkingTests
{
    private static readonly Tokenizer Tokenizer = TokenizerFixture.Instance;

    private static RecursiveChunkerOptions Options(int maxTokens = 64, bool repeatHeaders = true, bool preserveAtomic = true) =>
        new(Tokenizer)
        {
            MaxTokensPerChunk = maxTokens,
            ReservedHeaderTokens = 0,
            OverlapTokens = 0,
            PreserveAtomicElements = preserveAtomic,
            RepeatTableHeaders = repeatHeaders
        };

    [Fact]
    public async Task RecursiveChunkerSplitsRawMarkdownTableInParagraphWithRepeatedHeaders()
    {
        const string Intro = "Introduction to the pricing structure for enterprise accounts.\n\n";
        const string Header = "| Tier | Price | Monthly Active Users |\n| :--- | :--- | ---: |\n";
        string[] rows =
        [
            "| Starter | $49 | Up to 1,000 |\n",
            "| Growth | $199 | Up to 10,000 |\n",
            "| Scale | $499 | Up to 50,000 |\n",
            "| Enterprise | Custom | Unlimited Dedicated |\n"
        ];
        const string Outro = "\n\nFor custom SLA agreements, please contact the sales engineering team.";
        string fullParagraph = Intro + Header + string.Concat(rows) + Outro;

        var doc = new IngestionDocument("doc-prose-table");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph(fullParagraph));
        doc.Sections.Add(section);

        int limit = Tokenizer.CountTokens(Header + rows[0]) + 2;
        var chunker = new RecursiveTokenChunker(Options(maxTokens: limit));

        var chunks = await chunker.CollectAsync(doc);

        chunks.Count.Should().BeGreaterThan(2);

        // Intro prose comes first
        chunks[0].Content.Trim().Should().Be(Intro.Trim());

        // Table chunks follow, each with header and metadata
        var tableChunks = chunks.Where(c => c.Metadata.TryGetValue(ChunkMetadataKeys.IsTablePart, out var isPart) && (bool)isPart).ToList();
        tableChunks.Count.Should().BeGreaterThan(1);
        for (int i = 0; i < tableChunks.Count; i++)
        {
            var tc = tableChunks[i];
            tc.Content.Should().StartWith(Header);
            tc.Metadata[ChunkMetadataKeys.TablePartIndex].Should().Be(i);
            tc.Metadata[ChunkMetadataKeys.IsTableContinuation].Should().Be(i > 0);
            Tokenizer.CountTokens(tc.Content).Should().BeLessThanOrEqualTo(limit);
        }

        // Outro prose comes last
        chunks[^1].Content.Trim().Should().Be(Outro.Trim());
    }

    [Fact]
    public async Task RecursiveChunkerTreatsTableInsideCodeFenceAsCodeFenceNotTable()
    {
        const string FenceWithTable = "```markdown\n| Not | A | Real | Table |\n| --- | --- | --- | --- |\n| 1 | 2 | 3 | 4 |\n```\n";
        const string Text = "Leading text.\n\n" + FenceWithTable + "\nTrailing text.";

        var doc = new IngestionDocument("doc-fence-table");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph(Text));
        doc.Sections.Add(section);

        var chunker = new RecursiveTokenChunker(Options(maxTokens: 100));

        var chunks = await chunker.CollectAsync(doc);

        chunks.Should().HaveCount(3);
        chunks[0].Content.Trim().Should().Be("Leading text.");
        chunks[1].Content.Trim().Should().Be(FenceWithTable.Trim());
        // Code fence must not be tagged with table metadata
        chunks[1].Metadata.Should().NotContainKey(ChunkMetadataKeys.IsTablePart);
        chunks[2].Content.Trim().Should().Be("Trailing text.");
    }

    [Fact]
    public async Task RecursiveChunkerKeepsAFittingTableWholeWhenHeadersDoNotRepeat()
    {
        const string Header = "| A | B |\n| --- | --- |\n";
        string[] rows = ["| 1 | 2 |\n", "| 3 | 4 |\n", "| 5 | 6 |\n"];
        string table = Header + string.Concat(rows);

        var doc = new IngestionDocument("doc-no-repeat");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph(table));
        doc.Sections.Add(section);

        var chunker = new RecursiveTokenChunker(Options(maxTokens: 100, repeatHeaders: false));

        var chunks = await chunker.CollectAsync(doc);

        // RepeatTableHeaders only decides how oversized tables split; a fitting table stays one protected part.
        chunks.Should().ContainSingle();
        chunks[0].Content.Should().Be(table.TrimEnd('\n'));
        chunks[0].Metadata[ChunkMetadataKeys.IsTablePart].Should().Be(true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepeatTableHeadersDecidesHowAnOversizedDocumentTableSplits(bool repeatHeaders)
    {
        const string Header = "| Id | Value |\n| --- | --- |";
        string[] rows = [.. Enumerable.Range(1, 12).Select(i => $"| {i} | value number {i} |")];
        string markdown = Header + "\n" + string.Join("\n", rows);
        var cells = new IngestionDocumentElement?[rows.Length + 1, 2];
        cells[0, 0] = new IngestionDocumentParagraph("Id");
        cells[0, 1] = new IngestionDocumentParagraph("Value");
        for (int i = 0; i < rows.Length; i++)
        {
            cells[i + 1, 0] = new IngestionDocumentParagraph($"{i + 1}");
            cells[i + 1, 1] = new IngestionDocumentParagraph($"value number {i + 1}");
        }

        var doc = new IngestionDocument("doc-repeat");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentTable(markdown, cells));
        doc.Sections.Add(section);
        var chunker = new RecursiveTokenChunker(Options(maxTokens: 40, repeatHeaders: repeatHeaders));

        var chunks = await chunker.CollectAsync(doc);

        chunks.Count.Should().BeGreaterThan(1);
        chunks.Should().AllSatisfy(c => c.Metadata[ChunkMetadataKeys.IsTablePart].Should().Be(true));
        if (repeatHeaders)
        {
            chunks.Should().AllSatisfy(c => c.Content.Should().StartWith(Header));
        }
        else
        {
            chunks.Count(c => c.Content.Contains("| Id | Value |", StringComparison.Ordinal)).Should().Be(1);
            string.Concat(chunks.Select(c => c.Content)).Should().Be(markdown);
        }
    }
}
