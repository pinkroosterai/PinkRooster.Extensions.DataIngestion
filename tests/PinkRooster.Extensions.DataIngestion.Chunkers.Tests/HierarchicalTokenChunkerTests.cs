using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.Tokenizers;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class HierarchicalTokenChunkerTests
{
    private static readonly Tokenizer Tokenizer = TokenizerFixture.Instance;

    private static IngestionDocument CreateSampleDocument(string identifier, int paragraphCount = 5)
    {
        var doc = new IngestionDocument(identifier);
        var section = new IngestionDocumentSection();

        section.Elements.Add(new IngestionDocumentHeader("Architecture Overview") { Level = 1 });

        for (int i = 0; i < paragraphCount; i++)
        {
            section.Elements.Add(new IngestionDocumentParagraph(
                $"Paragraph {i + 1}: Enterprise distributed pipelines require deterministic small-to-big retrieval capabilities. " +
                "Small chunks provide high precision for dense vector search, while large parent chunks ensure comprehensive narrative " +
                "context during subsequent large language model generation passes."));
        }

        doc.Sections.Add(section);
        return doc;
    }

    [Fact]
    public async Task HierarchicalTokenChunkerEmitsInterleavedParentsAndChildrenWithLinkedMetadata()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer)
        {
            ParentMaxTokens = 120,
            ParentOverlapTokens = 0,
            MaxTokensPerChunk = 40,
            OverlapTokens = 10,
            EmissionMode = HierarchicalEmissionMode.Interleaved,
        };

        var chunker = new HierarchicalTokenChunker(options);
        var doc = CreateSampleDocument("doc-1", paragraphCount: 6);

        var chunks = await chunker.CollectAsync(doc);

        chunks.Should().NotBeEmpty();

        var parents = chunks.Where(c => (string)c.Metadata[ChunkMetadataKeys.ChunkRole] == ChunkMetadataKeys.ParentRole).ToList();
        var children = chunks.Where(c => (string)c.Metadata[ChunkMetadataKeys.ChunkRole] == ChunkMetadataKeys.ChildRole).ToList();

        parents.Should().NotBeEmpty();
        children.Should().NotBeEmpty();

        int parentIdx = 0;
        int i = 0;
        while (i < chunks.Count)
        {
            var parent = chunks[i];
            parent.Metadata[ChunkMetadataKeys.ChunkRole].Should().Be(ChunkMetadataKeys.ParentRole);
            parent.Metadata[ChunkMetadataKeys.ChunkId].Should().Be($"doc-1#p{parentIdx}");
            int childCount = (int)parent.Metadata[ChunkMetadataKeys.ChildCount];
            childCount.Should().BeGreaterThan(0);
            int parentTokens = (int)parent.Metadata[ChunkMetadataKeys.ParentTokenCount];
            parentTokens.Should().BeGreaterThan(0);

            i++;
            for (int c = 0; c < childCount; c++)
            {
                var child = chunks[i];
                child.Metadata[ChunkMetadataKeys.ChunkRole].Should().Be(ChunkMetadataKeys.ChildRole);
                child.Metadata[ChunkMetadataKeys.ChunkId].Should().Be($"doc-1#p{parentIdx}_c{c}");
                child.Metadata[ChunkMetadataKeys.ParentId].Should().Be($"doc-1#p{parentIdx}");
                child.Metadata[ChunkMetadataKeys.ChildIndex].Should().Be(c);
                child.Metadata[ChunkMetadataKeys.ChildCount].Should().Be(childCount);
                child.Context.Should().Be(parent.Context);
                i++;
            }

            parentIdx++;
        }
    }

    [Fact]
    public async Task HierarchicalTokenChunkerEmitsChildrenOnlyMode()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer)
        {
            ParentMaxTokens = 100,
            MaxTokensPerChunk = 40,
            EmissionMode = HierarchicalEmissionMode.ChildrenOnly,
        };

        var chunker = new HierarchicalTokenChunker(options);
        var doc = CreateSampleDocument("doc-2", paragraphCount: 3);

        var chunks = await chunker.CollectAsync(doc);

        chunks.Should().NotBeEmpty();
        chunks.Should().AllSatisfy(c =>
        {
            c.Metadata[ChunkMetadataKeys.ChunkRole].Should().Be(ChunkMetadataKeys.ChildRole);
            c.Metadata.Should().ContainKey(ChunkMetadataKeys.ParentId);
            c.Metadata.Should().ContainKey(ChunkMetadataKeys.ChildIndex);
        });
    }

    [Fact]
    public async Task HierarchicalTokenChunkerEmitsParentsOnlyMode()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer)
        {
            ParentMaxTokens = 100,
            MaxTokensPerChunk = 40,
            EmissionMode = HierarchicalEmissionMode.ParentsOnly,
        };

        var chunker = new HierarchicalTokenChunker(options);
        var doc = CreateSampleDocument("doc-3", paragraphCount: 3);

        var chunks = await chunker.CollectAsync(doc);

        chunks.Should().NotBeEmpty();
        chunks.Should().AllSatisfy(c =>
        {
            c.Metadata[ChunkMetadataKeys.ChunkRole].Should().Be(ChunkMetadataKeys.ParentRole);
            c.Metadata.Should().ContainKey(ChunkMetadataKeys.ChunkId);
            c.Metadata.Should().ContainKey(ChunkMetadataKeys.ChildCount);
            c.Metadata.Should().NotContainKey(ChunkMetadataKeys.ParentId);
        });
    }

    [Fact]
    public async Task HierarchicalTokenChunkerPropagatesContextBreadcrumbs()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer)
        {
            ParentMaxTokens = 120,
            MaxTokensPerChunk = 50,
            EmissionMode = HierarchicalEmissionMode.Interleaved,
        };

        var chunker = new HierarchicalTokenChunker(options);
        var doc = CreateSampleDocument("doc-breadcrumbs", paragraphCount: 2);

        var chunks = await chunker.CollectAsync(doc);

        chunks.Should().NotBeEmpty();
        chunks.Should().AllSatisfy(c =>
        {
            c.Context.Should().Be("Architecture Overview");
        });
    }

    [Fact]
    public void AddHierarchicalTokenChunkerRegistersServiceCorrectly()
    {
        var services = new ServiceCollection();
        var options = new HierarchicalChunkerOptions(Tokenizer);

        services.AddHierarchicalTokenChunker(options);
        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<IngestionChunker<string>>();
        resolved.Should().NotBeNull();
        resolved.Should().BeOfType<HierarchicalTokenChunker>();
    }

    [Fact]
    public async Task HierarchicalTokenChunkerHandlesDocumentSmallerThanChildBudget()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer)
        {
            ParentMaxTokens = 200,
            MaxTokensPerChunk = 100,
            OverlapTokens = 0,
            EmissionMode = HierarchicalEmissionMode.Interleaved,
        };

        var chunker = new HierarchicalTokenChunker(options);
        var doc = new IngestionDocument("doc-tiny");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph("Brief sentence."));
        doc.Sections.Add(section);

        var chunks = await chunker.CollectAsync(doc);

        chunks.Should().HaveCount(2);

        var parent = chunks[0];
        parent.Metadata[ChunkMetadataKeys.ChunkRole].Should().Be(ChunkMetadataKeys.ParentRole);
        parent.Metadata[ChunkMetadataKeys.ChunkId].Should().Be("doc-tiny#p0");
        parent.Metadata[ChunkMetadataKeys.ChildCount].Should().Be(1);

        var child = chunks[1];
        child.Metadata[ChunkMetadataKeys.ChunkRole].Should().Be(ChunkMetadataKeys.ChildRole);
        child.Metadata[ChunkMetadataKeys.ChunkId].Should().Be("doc-tiny#p0_c0");
        child.Metadata[ChunkMetadataKeys.ParentId].Should().Be("doc-tiny#p0");
        child.Metadata[ChunkMetadataKeys.ChildIndex].Should().Be(0);
        child.Metadata[ChunkMetadataKeys.ChildCount].Should().Be(1);
    }

    [Fact]
    public async Task HierarchicalTokenChunkerHandlesZeroChildOverlap()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer)
        {
            ParentMaxTokens = 80,
            OverlapTokens = 0,
            MaxTokensPerChunk = 30,
            EmissionMode = HierarchicalEmissionMode.Interleaved,
        };

        var chunker = new HierarchicalTokenChunker(options);
        var doc = CreateSampleDocument("doc-zero-overlap", paragraphCount: 4);

        var chunks = await chunker.CollectAsync(doc);

        chunks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task HierarchicalTokenChunkerHandlesDocumentWithoutHeadings()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer)
        {
            ParentMaxTokens = 100,
            MaxTokensPerChunk = 40,
            EmissionMode = HierarchicalEmissionMode.Interleaved,
        };

        var chunker = new HierarchicalTokenChunker(options);
        var doc = new IngestionDocument("doc-no-headings");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph("Plain paragraph without any heading hierarchy. Still cleanly processed."));
        doc.Sections.Add(section);

        var chunks = await chunker.CollectAsync(doc);

        chunks.Should().HaveCount(2);
        chunks[0].Context.Should().BeNull();
        chunks[1].Context.Should().BeNull();
        chunks[0].Metadata[ChunkMetadataKeys.ChunkRole].Should().Be(ChunkMetadataKeys.ParentRole);
        chunks[1].Metadata[ChunkMetadataKeys.ChunkRole].Should().Be(ChunkMetadataKeys.ChildRole);
        chunks[1].Metadata[ChunkMetadataKeys.ParentId].Should().Be("doc-no-headings#p0");
    }

    [Fact]
    public async Task HierarchicalTokenChunkerHandlesEmptyAndWhitespaceElementsGracefully()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer)
        {
            ParentMaxTokens = 100,
            OverlapTokens = 0,
            MaxTokensPerChunk = 40,
        };

        var chunker = new HierarchicalTokenChunker(options);
        var doc = new IngestionDocument("doc-empty");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph("   "));
        section.Elements.Add(new IngestionDocumentParagraph("\t\n  \r\n"));
        section.Elements.Add(new IngestionDocumentParagraph("Valid content here."));
        doc.Sections.Add(section);

        var chunks = await chunker.CollectAsync(doc);

        chunks.Should().NotBeEmpty();
        chunks.Should().AllSatisfy(c => string.IsNullOrWhiteSpace(c.Content).Should().BeFalse());
    }

    [Fact]
    public async Task HierarchicalTokenChunkerHandlesOversizedTokenSpanGracefully()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer)
        {
            ParentMaxTokens = 100,
            OverlapTokens = 0,
            MaxTokensPerChunk = 15,
            EmissionMode = HierarchicalEmissionMode.Interleaved,
        };

        var chunker = new HierarchicalTokenChunker(options);
        var doc = new IngestionDocument("doc-oversized");
        var section = new IngestionDocumentSection();
        // 200 consecutive characters without spaces triggers span slicing
        section.Elements.Add(new IngestionDocumentParagraph(new string('x', 200)));
        doc.Sections.Add(section);

        var chunks = await chunker.CollectAsync(doc);

        var children = chunks.Where(c => (string)c.Metadata[ChunkMetadataKeys.ChunkRole] == ChunkMetadataKeys.ChildRole).ToList();
        children.Count.Should().BeGreaterThan(1);
        children.Should().AllSatisfy(c =>
        {
            c.Metadata[ChunkMetadataKeys.ParentId].Should().Be("doc-oversized#p0");
        });
    }

    [Fact]
    public async Task ObservesCancellationBeforeAndAfterFirstYield()
    {
        var chunker = new HierarchicalTokenChunker(new HierarchicalChunkerOptions(Tokenizer)
        {
            ParentMaxTokens = 120,
            MaxTokensPerChunk = 40,
            OverlapTokens = 0,
        });
        var document = CreateSampleDocument("doc-cancel", paragraphCount: 4);

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

    [Fact]
    public async Task ParentsAndChildrenStayWithinTheirBudgetsAndReportTheExactParentTokenCount()
    {
        const int ParentLimit = 120;
        const int ChildLimit = 40;
        var chunker = new HierarchicalTokenChunker(new HierarchicalChunkerOptions(Tokenizer)
        {
            ParentMaxTokens = ParentLimit,
            MaxTokensPerChunk = ChildLimit,
            OverlapTokens = 10,
        });

        var chunks = await chunker.CollectAsync(CreateSampleDocument("doc-budget", paragraphCount: 8));

        chunks.Should().NotBeEmpty();
        foreach (var chunk in chunks)
        {
            int tokens = Tokenizer.CountTokens(chunk.Content, considerNormalization: false);
            if ((string)chunk.Metadata[ChunkMetadataKeys.ChunkRole] == ChunkMetadataKeys.ParentRole)
            {
                tokens.Should().BeLessThanOrEqualTo(ParentLimit);
                chunk.Metadata[ChunkMetadataKeys.ParentTokenCount].Should().Be(tokens);
            }
            else
            {
                tokens.Should().BeLessThanOrEqualTo(ChildLimit);
            }
        }
    }

    [Fact]
    public async Task OptionChangesAfterConstructionDoNotAffectTheChunker()
    {
        var options = new HierarchicalChunkerOptions(Tokenizer)
        {
            ParentMaxTokens = 120,
            MaxTokensPerChunk = 40,
            OverlapTokens = 0,
            EmissionMode = HierarchicalEmissionMode.ParentsOnly,
        };
        var chunker = new HierarchicalTokenChunker(options);
        options.EmissionMode = HierarchicalEmissionMode.ChildrenOnly;

        var chunks = await chunker.CollectAsync(CreateSampleDocument("doc-snapshot", paragraphCount: 3));

        chunks.Should().NotBeEmpty().And.AllSatisfy(c =>
            c.Metadata[ChunkMetadataKeys.ChunkRole].Should().Be(ChunkMetadataKeys.ParentRole));
    }

    [Fact]
    public async Task ChildrenOfATablePartCarryTheParentsTablePositionAndRows()
    {
        const string Header = "| Id | Name | Description |\n| --- | --- | --- |\n";
        string rows = string.Concat(Enumerable.Range(1, 60).Select(i =>
            $"| {i} | item {i} | a short description of row number {i} |\n"));
        var doc = new IngestionDocument("doc-table");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph(Header + rows));
        doc.Sections.Add(section);
        var chunker = new HierarchicalTokenChunker(new HierarchicalChunkerOptions(Tokenizer)
        {
            ParentMaxTokens = 400,
            MaxTokensPerChunk = 80,
            OverlapTokens = 0,
        });

        var chunks = await chunker.CollectAsync(doc);

        var parents = chunks.Where(c => (string)c.Metadata[ChunkMetadataKeys.ChunkRole] == ChunkMetadataKeys.ParentRole).ToList();
        parents.Count.Should().BeGreaterThan(1);
        foreach (var parent in parents)
        {
            parent.Metadata[ChunkMetadataKeys.IsTablePart].Should().Be(true);
            var (parentFirst, parentLast) = Range((string)parent.Metadata[ChunkMetadataKeys.TableRowRange]);
            var children = chunks.Where(c => Equals(c.Metadata.TryGetValue(ChunkMetadataKeys.ParentId, out var id) ? id : null,
                parent.Metadata[ChunkMetadataKeys.ChunkId])).ToList();
            children.Should().NotBeEmpty();
            foreach (var child in children)
            {
                child.Metadata[ChunkMetadataKeys.IsTablePart].Should().Be(true);
                child.Metadata[ChunkMetadataKeys.TablePartIndex].Should().Be(parent.Metadata[ChunkMetadataKeys.TablePartIndex]);
                if ((bool)parent.Metadata[ChunkMetadataKeys.IsTableContinuation])
                {
                    child.Metadata[ChunkMetadataKeys.IsTableContinuation].Should().Be(true);
                }

                var (childFirst, childLast) = Range((string)child.Metadata[ChunkMetadataKeys.TableRowRange]);
                childFirst.Should().BeInRange(parentFirst, parentLast);
                childLast.Should().BeInRange(parentFirst, parentLast);
            }
        }

        static (int First, int Last) Range(string range)
        {
            string[] parts = range.Split('-');
            return (int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
