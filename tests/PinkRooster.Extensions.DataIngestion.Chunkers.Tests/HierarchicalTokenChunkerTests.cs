using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.Tokenizers;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class HierarchicalTokenChunkerTests
{
    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

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

        var chunks = new List<IngestionChunk<string>>();
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Should().NotBeEmpty();

        var parents = chunks.Where(c => (string)c.Metadata["ChunkRole"] == "Parent").ToList();
        var children = chunks.Where(c => (string)c.Metadata["ChunkRole"] == "Child").ToList();

        parents.Should().NotBeEmpty();
        children.Should().NotBeEmpty();

        int parentIdx = 0;
        int i = 0;
        while (i < chunks.Count)
        {
            var parent = chunks[i];
            parent.Metadata["ChunkRole"].Should().Be("Parent");
            parent.Metadata["ChunkId"].Should().Be($"doc-1#p{parentIdx}");
            int childCount = (int)parent.Metadata["ChildCount"];
            childCount.Should().BeGreaterThan(0);
            int parentTokens = (int)parent.Metadata["ParentTokenCount"];
            parentTokens.Should().BeGreaterThan(0);

            i++;
            for (int c = 0; c < childCount; c++)
            {
                var child = chunks[i];
                child.Metadata["ChunkRole"].Should().Be("Child");
                child.Metadata["ChunkId"].Should().Be($"doc-1#p{parentIdx}_c{c}");
                child.Metadata["ParentId"].Should().Be($"doc-1#p{parentIdx}");
                child.Metadata["ChildIndex"].Should().Be(c);
                child.Metadata["ChildCount"].Should().Be(childCount);
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

        var chunks = new List<IngestionChunk<string>>();
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Should().NotBeEmpty();
        chunks.Should().AllSatisfy(c =>
        {
            c.Metadata["ChunkRole"].Should().Be("Child");
            c.Metadata.Should().ContainKey("ParentId");
            c.Metadata.Should().ContainKey("ChildIndex");
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

        var chunks = new List<IngestionChunk<string>>();
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Should().NotBeEmpty();
        chunks.Should().AllSatisfy(c =>
        {
            c.Metadata["ChunkRole"].Should().Be("Parent");
            c.Metadata.Should().ContainKey("ChunkId");
            c.Metadata.Should().ContainKey("ChildCount");
            c.Metadata.Should().NotContainKey("ParentId");
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

        var chunks = new List<IngestionChunk<string>>();
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

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

        var chunks = new List<IngestionChunk<string>>();
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Should().HaveCount(2);

        var parent = chunks[0];
        parent.Metadata["ChunkRole"].Should().Be("Parent");
        parent.Metadata["ChunkId"].Should().Be("doc-tiny#p0");
        parent.Metadata["ChildCount"].Should().Be(1);

        var child = chunks[1];
        child.Metadata["ChunkRole"].Should().Be("Child");
        child.Metadata["ChunkId"].Should().Be("doc-tiny#p0_c0");
        child.Metadata["ParentId"].Should().Be("doc-tiny#p0");
        child.Metadata["ChildIndex"].Should().Be(0);
        child.Metadata["ChildCount"].Should().Be(1);
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

        var chunks = new List<IngestionChunk<string>>();
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

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

        var chunks = new List<IngestionChunk<string>>();
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Should().HaveCount(2);
        chunks[0].Context.Should().BeNull();
        chunks[1].Context.Should().BeNull();
        chunks[0].Metadata["ChunkRole"].Should().Be("Parent");
        chunks[1].Metadata["ChunkRole"].Should().Be("Child");
        chunks[1].Metadata["ParentId"].Should().Be("doc-no-headings#p0");
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

        var chunks = new List<IngestionChunk<string>>();
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

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

        var chunks = new List<IngestionChunk<string>>();
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        var children = chunks.Where(c => (string)c.Metadata["ChunkRole"] == "Child").ToList();
        children.Count.Should().BeGreaterThan(1);
        children.Should().AllSatisfy(c =>
        {
            c.Metadata["ParentId"].Should().Be("doc-oversized#p0");
        });
    }
}
