using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.Tokenizers;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class MarkdownTableChunkerTests
{
    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

    private static TableChunkerOptions Options(int maxTokens = 64, int reservedHeaderTokens = 0, bool emitNonTable = true) =>
        new(Tokenizer)
        {
            MaxTokensPerChunk = maxTokens,
            ReservedHeaderTokens = reservedHeaderTokens,
            EmitNonTableContent = emitNonTable
        };

    [Fact]
    public async Task ProcessAsyncSplitsLargeTableWithRepeatedHeadersAndMetadata()
    {
        const string Header = "| Employee | Role | Department |\n| :--- | :--- | :--- |\n";
        string[] rows =
        [
            "| Alice Smith | Lead Engineer | Platform Infrastructure |\n",
            "| Bob Jones | Product Manager | Core Experience |\n",
            "| Charlie Brown | Designer | Design Systems |\n",
            "| Diana Prince | VP Engineering | Leadership |\n"
        ];
        string markdown = Header + string.Concat(rows);

        var doc = new IngestionDocument("doc-table");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph(markdown));
        doc.Sections.Add(section);

        int rowLimit = Tokenizer.CountTokens(Header + rows[0]);
        var chunker = new MarkdownTableChunker(Options(maxTokens: rowLimit));

        List<IngestionChunk<string>> chunks = [];
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Count.Should().BeGreaterThan(1);
        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            chunk.Content.Should().StartWith(Header);
            chunk.Metadata["IsTablePart"].Should().Be(true);
            chunk.Metadata["TablePartIndex"].Should().Be(i);
            chunk.Metadata["IsTableContinuation"].Should().Be(i > 0);
            chunk.Metadata.Should().ContainKey("TableRowRange");
        }
    }

    [Fact]
    public async Task ProcessAsyncHandlesDocumentTableElements()
    {
        const string Markdown = "| Metric | Value |\n| --- | --- |\n| Recall | 0.92 |\n";
        var doc = new IngestionDocument("doc-elem");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentTable(Markdown, new IngestionDocumentElement?[2, 2]));
        doc.Sections.Add(section);

        var chunker = new MarkdownTableChunker(Options(maxTokens: 100));

        List<IngestionChunk<string>> chunks = [];
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Should().HaveCount(1);
        chunks[0].Content.TrimEnd().Should().Be(Markdown.TrimEnd());
        chunks[0].Metadata["IsTablePart"].Should().Be(true);
        chunks[0].Metadata["IsTableContinuation"].Should().Be(false);
    }

    [Fact]
    public async Task ProcessAsyncEmitsSurroundingProseWhenEnabled()
    {
        const string ProseBefore = "This is introductory context before the table.";
        const string TableMd = "| A | B |\n| --- | --- |\n| 1 | 2 |\n";
        const string ProseAfter = "This is concluding remarks after the table.";
        string fullContent = $"{ProseBefore}\n\n{TableMd}\n\n{ProseAfter}";

        var doc = new IngestionDocument("doc-mixed");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph(fullContent));
        doc.Sections.Add(section);

        var chunker = new MarkdownTableChunker(Options(maxTokens: 200, emitNonTable: true));

        List<IngestionChunk<string>> chunks = [];
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Should().HaveCount(3);
        chunks[0].Content.Should().Be(ProseBefore);
        chunks[1].Content.TrimEnd().Should().Be(TableMd.TrimEnd());
        chunks[1].Metadata["IsTablePart"].Should().Be(true);
        chunks[2].Content.Should().Be(ProseAfter);
    }

    [Fact]
    public async Task ProcessAsyncOmitsSurroundingProseWhenDisabled()
    {
        const string ProseBefore = "Ignored introductory context.";
        const string TableMd = "| A | B |\n| --- | --- |\n| 1 | 2 |\n";
        string fullContent = $"{ProseBefore}\n\n{TableMd}";

        var doc = new IngestionDocument("doc-table-only");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph(fullContent));
        doc.Sections.Add(section);

        var chunker = new MarkdownTableChunker(Options(maxTokens: 200, emitNonTable: false));

        List<IngestionChunk<string>> chunks = [];
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Should().HaveCount(1);
        chunks[0].Content.TrimEnd().Should().Be(TableMd.TrimEnd());
        chunks[0].Metadata["IsTablePart"].Should().Be(true);
    }

    [Fact]
    public async Task ProcessAsyncPropagatesBreadcrumbContextFromHeaders()
    {
        var doc = new IngestionDocument("doc-breadcrumbs");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentHeader("# Architecture") { Level = 1 });
        section.Elements.Add(new IngestionDocumentHeader("## Data Ingestion") { Level = 2 });
        section.Elements.Add(new IngestionDocumentParagraph("| Package | Version |\n| --- | --- |\n| Core | 1.0 |\n"));
        doc.Sections.Add(section);

        var chunker = new MarkdownTableChunker(Options(maxTokens: 200));

        List<IngestionChunk<string>> chunks = [];
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Should().HaveCount(1);
        chunks[0].Context.Should().Be("Architecture > Data Ingestion");
    }

    [Fact]
    public void ServiceCollectionExtensionRegistersMarkdownTableChunker()
    {
        var services = new ServiceCollection();
        services.AddMarkdownTableChunker(Options());
        using var provider = services.BuildServiceProvider();

        var chunker = provider.GetRequiredService<IngestionChunker<string>>();
        chunker.Should().BeOfType<MarkdownTableChunker>();
    }

    [Fact]
    public void OptionsValidationThrowsOnInvalidSettings()
    {
        Action nullOptions = () => _ = new MarkdownTableChunker(null!);
        nullOptions.Should().Throw<ArgumentNullException>();

        var optionsNegativeReserved = new TableChunkerOptions(Tokenizer)
        {
            MaxTokensPerChunk = 64,
            ReservedHeaderTokens = -5
        };
        Action negativeReserved = () => _ = new MarkdownTableChunker(optionsNegativeReserved);
        negativeReserved.Should().Throw<ArgumentOutOfRangeException>();

        var optionsZeroEffective = new TableChunkerOptions(Tokenizer)
        {
            MaxTokensPerChunk = 64,
            ReservedHeaderTokens = 64
        };
        Action zeroEffective = () => _ = new MarkdownTableChunker(optionsZeroEffective);
        zeroEffective.Should().Throw<ArgumentOutOfRangeException>();
    }
}
