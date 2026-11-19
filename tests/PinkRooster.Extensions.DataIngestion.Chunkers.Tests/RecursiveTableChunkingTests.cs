using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class RecursiveTableChunkingTests
{
    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

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

        List<IngestionChunk<string>> chunks = [];
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Count.Should().BeGreaterThan(2);

        // Intro prose comes first
        chunks[0].Content.Trim().Should().Be(Intro.Trim());

        // Table chunks follow, each with header and metadata
        var tableChunks = chunks.Where(c => c.Metadata.TryGetValue("IsTablePart", out var isPart) && (bool)isPart).ToList();
        tableChunks.Count.Should().BeGreaterThan(1);
        for (int i = 0; i < tableChunks.Count; i++)
        {
            var tc = tableChunks[i];
            tc.Content.Should().StartWith(Header);
            tc.Metadata["TablePartIndex"].Should().Be(i);
            tc.Metadata["IsTableContinuation"].Should().Be(i > 0);
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

        List<IngestionChunk<string>> chunks = [];
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Should().HaveCount(3);
        chunks[0].Content.Trim().Should().Be("Leading text.");
        chunks[1].Content.Trim().Should().Be(FenceWithTable.Trim());
        // Code fence must not be tagged with table metadata
        chunks[1].Metadata.Should().NotContainKey("IsTablePart");
        chunks[2].Content.Trim().Should().Be("Trailing text.");
    }

    [Fact]
    public async Task RecursiveChunkerRespectsRepeatTableHeadersFalse()
    {
        const string Header = "| A | B |\n| --- | --- |\n";
        string[] rows = ["| 1 | 2 |\n", "| 3 | 4 |\n", "| 5 | 6 |\n"];
        string table = Header + string.Concat(rows);

        var doc = new IngestionDocument("doc-no-repeat");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph(table));
        doc.Sections.Add(section);

        var chunker = new RecursiveTokenChunker(Options(maxTokens: 100, repeatHeaders: false));

        List<IngestionChunk<string>> chunks = [];
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        // When repeat headers is false, table is processed without table continuation metadata
        chunks.All(c => !c.Metadata.ContainsKey("IsTablePart")).Should().BeTrue();
    }
}
