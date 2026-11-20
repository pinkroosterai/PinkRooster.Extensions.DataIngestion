using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.Tokenizers;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class MarkdownTableChunkerTests
{
    private static readonly Tokenizer Tokenizer = TokenizerFixture.Instance;

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

        var chunks = await chunker.CollectAsync(doc);

        chunks.Count.Should().BeGreaterThan(1);
        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            chunk.Content.Should().StartWith(Header);
            chunk.Metadata[ChunkMetadataKeys.IsTablePart].Should().Be(true);
            chunk.Metadata[ChunkMetadataKeys.TablePartIndex].Should().Be(i);
            chunk.Metadata[ChunkMetadataKeys.IsTableContinuation].Should().Be(i > 0);
            chunk.Metadata.Should().ContainKey(ChunkMetadataKeys.TableRowRange);
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

        var chunks = await chunker.CollectAsync(doc);

        chunks.Should().HaveCount(1);
        chunks[0].Content.TrimEnd().Should().Be(Markdown.TrimEnd());
        chunks[0].Metadata[ChunkMetadataKeys.IsTablePart].Should().Be(true);
        chunks[0].Metadata[ChunkMetadataKeys.IsTableContinuation].Should().Be(false);
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

        var chunks = await chunker.CollectAsync(doc);

        chunks.Should().HaveCount(3);
        chunks[0].Content.Should().Be(ProseBefore);
        chunks[1].Content.TrimEnd().Should().Be(TableMd.TrimEnd());
        chunks[1].Metadata[ChunkMetadataKeys.IsTablePart].Should().Be(true);
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

        var chunks = await chunker.CollectAsync(doc);

        chunks.Should().HaveCount(1);
        chunks[0].Content.TrimEnd().Should().Be(TableMd.TrimEnd());
        chunks[0].Metadata[ChunkMetadataKeys.IsTablePart].Should().Be(true);
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

        var chunks = await chunker.CollectAsync(doc);

        chunks.Should().HaveCount(1);
        chunks[0].Context.Should().Be("Architecture > Data Ingestion");
    }

    [Fact]
    public async Task TableChunkBreadcrumbResolvesInDocumentContext()
    {
        var doc = new IngestionDocument("doc-table-context");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentHeader("# Overview") { Level = 1 });
        section.Elements.Add(new IngestionDocumentHeader("## Datasets") { Level = 2 });
        section.Elements.Add(new IngestionDocumentParagraph("| Name | Rows |\n| --- | --- |\n| Customers | 1000 |\n"));
        doc.Sections.Add(section);

        var chunker = new MarkdownTableChunker(Options(maxTokens: 200));
        var chunks = await chunker.CollectAsync(doc);

        chunks.Should().ContainSingle();
        chunks[0].Context.Should().Be("Overview > Datasets");

        var budget = new TokenBudget(Tokenizer, 200);
        var documentContext = new DocumentContext(doc, budget, 0, TestContext.Current.CancellationToken);
        var resolved = documentContext.Find(chunks[0], TestContext.Current.CancellationToken);
        resolved.Should().NotBeNull();
        resolved.Should().Contain("Customers");
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

    private static (string Markdown, IngestionDocumentElement?[,] Cells, string[] Rows) LargeTable(int rowCount)
    {
        const string Header = "| Id | Name |\n| --- | --- |";
        string[] rows = [.. Enumerable.Range(1, rowCount).Select(i => $"| {i} | entry name {i} |")];
        var cells = new IngestionDocumentElement?[rowCount + 1, 2];
        cells[0, 0] = new IngestionDocumentParagraph("Id");
        cells[0, 1] = new IngestionDocumentParagraph("Name");
        for (int i = 0; i < rowCount; i++)
        {
            cells[i + 1, 0] = new IngestionDocumentParagraph($"{i + 1}");
            cells[i + 1, 1] = new IngestionDocumentParagraph($"entry name {i + 1}");
        }

        return (Header + "\n" + string.Join("\n", rows), cells, rows);
    }

    private static IngestionDocument Single(IngestionDocumentElement element)
    {
        var doc = new IngestionDocument("doc-large");
        var section = new IngestionDocumentSection();
        section.Elements.Add(element);
        doc.Sections.Add(section);
        return doc;
    }

    [Fact]
    public async Task ObservesCancellationBeforeAndAfterFirstYield()
    {
        var (markdown, _, _) = LargeTable(30);
        var chunker = new MarkdownTableChunker(Options(maxTokens: 40));
        var document = Single(new IngestionDocumentParagraph(markdown));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Func<Task> before = async () =>
        {
            await foreach (var _ in chunker.ProcessAsync(document, cancelled.Token))
            {
            }
        };
        await before.Should().ThrowAsync<OperationCanceledException>();

        using var pending = new CancellationTokenSource();
        await using var enumerator = chunker.ProcessAsync(document, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(pending.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        await pending.CancelAsync();
        Func<Task> after = async () => { await enumerator.MoveNextAsync(); };
        await after.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EveryRowAppearsExactlyOnceWithinBudgetAndTheSameKeysForBothTableKinds(bool documentTable)
    {
        const int Limit = 40;
        var (markdown, cells, rows) = LargeTable(30);
        IngestionDocumentElement element = documentTable
            ? new IngestionDocumentTable(markdown, cells)
            : new IngestionDocumentParagraph(markdown);

        var chunks = await ReadAllAsync(new MarkdownTableChunker(Options(maxTokens: Limit)), Single(element));
        var recursive = await ReadAllAsync(new RecursiveTokenChunker(new RecursiveChunkerOptions(Tokenizer)
        {
            MaxTokensPerChunk = Limit,
            OverlapTokens = 0,
        }), Single(element));

        foreach (var output in new[] { chunks, recursive })
        {
            output.Count.Should().BeGreaterThan(1);
            foreach (string row in rows)
            {
                output.Count(c => c.Content.Split('\n').Contains(row)).Should().Be(1, $"row '{row}' must appear once");
            }

            output.Should().AllSatisfy(c =>
            {
                Tokenizer.CountTokens(c.Content, considerNormalization: false).Should().BeLessThanOrEqualTo(Limit);
                c.Metadata.Keys.Should().BeEquivalentTo(
                [
                    ChunkMetadataKeys.IsTablePart, ChunkMetadataKeys.TablePartIndex,
                    ChunkMetadataKeys.IsTableContinuation, ChunkMetadataKeys.TableRowRange,
                ]);
            });
        }

        static async Task<List<IngestionChunk<string>>> ReadAllAsync(IngestionChunker<string> chunker, IngestionDocument document)
        {
            List<IngestionChunk<string>> result = [];
            await foreach (var chunk in chunker.ProcessAsync(document, TestContext.Current.CancellationToken))
            {
                result.Add(chunk);
            }

            return result;
        }
    }

    [Fact]
    public async Task RecursiveAndTableChunksOfTheSameDocumentCarryIdenticalBreadcrumbs()
    {
        const string Table = "| A | B |\n| --- | --- |\n| 1 | 2 |";
        var doc = new IngestionDocument("doc-breadcrumbs");
        var outer = new IngestionDocumentSection();
        outer.Elements.Add(new IngestionDocumentHeader("# Guide") { Level = 1 });
        var nested = new IngestionDocumentSection();
        nested.Elements.Add(new IngestionDocumentHeader("## Setup") { Level = 2 });
        nested.Elements.Add(new IngestionDocumentHeader("###") { Level = 3 });
        nested.Elements.Add(new IngestionDocumentParagraph(Table));
        outer.Elements.Add(nested);
        doc.Sections.Add(outer);

        List<string?> contexts = [];
        IngestionChunker<string>[] chunkers =
        [
            new MarkdownTableChunker(Options(maxTokens: 100)),
            new RecursiveTokenChunker(new RecursiveChunkerOptions(Tokenizer) { MaxTokensPerChunk = 100, OverlapTokens = 0 }),
        ];
        foreach (var chunker in chunkers)
        {
            await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
            {
                contexts.Add(chunk.Context);
            }
        }

        contexts.Should().HaveCount(2).And.OnlyContain(context => context == "Guide > Setup");
    }
}
