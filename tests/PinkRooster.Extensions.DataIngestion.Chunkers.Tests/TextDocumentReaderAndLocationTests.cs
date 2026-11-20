using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class TextDocumentReaderAndLocationTests
{
    private static readonly RecursiveChunkerOptions DefaultOptions = new(TokenizerFixture.Instance)
    {
        MaxTokensPerChunk = 64,
        OverlapTokens = 16,
        PreserveAtomicElements = true,
        RepeatTableHeaders = true,
    };

    [Fact]
    public async Task ReaderExtractsHeadingsAndBlankLineBlocksWithVerbatimSlices()
    {
        const string Markdown = """
            # Architecture Overview

            This is the first paragraph.
            It spans two lines.

            ## Component Details

            ```csharp
            #not_a_heading
            public void Run()
            {
            }
            ```

            Final text block.
            """;

        TextDocumentReader reader = new();
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(Markdown));
        IngestionDocument doc = await reader.ReadAsync(
            stream, "doc.md", "text/markdown", TestContext.Current.CancellationToken);

        doc.Identifier.Should().Be("doc.md");
        doc.Sections.Should().HaveCount(1);
        var elements = doc.Sections[0].Elements;
        elements.Should().HaveCount(5);

        // Element 0: # Architecture Overview
        elements[0].Should().BeOfType<IngestionDocumentHeader>();
        var h1 = (IngestionDocumentHeader)elements[0];
        h1.Level.Should().Be(1);
        h1.GetMarkdown().Should().Be("# Architecture Overview");
        h1.Metadata[ChunkMetadataKeys.StartLine].Should().Be(1);
        h1.Metadata[ChunkMetadataKeys.EndLine].Should().Be(1);

        // Element 1: paragraph 1 (lines 3-4)
        elements[1].Should().BeOfType<IngestionDocumentParagraph>();
        var p1 = (IngestionDocumentParagraph)elements[1];
        p1.GetMarkdown().Should().Be("This is the first paragraph.\nIt spans two lines.\n");
        p1.Metadata[ChunkMetadataKeys.StartLine].Should().Be(3);
        p1.Metadata[ChunkMetadataKeys.EndLine].Should().Be(4);

        // Element 2: ## Component Details (line 6)
        elements[2].Should().BeOfType<IngestionDocumentHeader>();
        var h2 = (IngestionDocumentHeader)elements[2];
        h2.Level.Should().Be(2);
        h2.Metadata[ChunkMetadataKeys.StartLine].Should().Be(6);

        // Element 3: fenced code block (lines 8-13)
        elements[3].Should().BeOfType<IngestionDocumentParagraph>();
        var code = (IngestionDocumentParagraph)elements[3];
        code.GetMarkdown().Should().Contain("#not_a_heading");
        code.Metadata[ChunkMetadataKeys.StartLine].Should().Be(8);
        code.Metadata[ChunkMetadataKeys.EndLine].Should().Be(13);

        // Element 4: final block (line 15)
        elements[4].Should().BeOfType<IngestionDocumentParagraph>();
        elements[4].Metadata[ChunkMetadataKeys.StartLine].Should().Be(15);
        elements[4].Metadata[ChunkMetadataKeys.EndLine].Should().Be(15);
    }

    [Fact]
    public async Task ReaderRespectsExtractMarkdownHeadingsFalse()
    {
        const string Markdown = """
            # Title

            Paragraph
            """;

        TextDocumentReader reader = new(new TextDocumentReaderOptions { ExtractMarkdownHeadings = false });
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(Markdown));
        IngestionDocument doc = await reader.ReadAsync(
            stream, "doc.txt", "text/plain", TestContext.Current.CancellationToken);

        doc.Sections[0].Elements.Should().HaveCount(2);
        doc.Sections[0].Elements[0].Should().BeOfType<IngestionDocumentParagraph>();
        doc.Sections[0].Elements[0].GetMarkdown().Should().Be("# Title\n");
    }

    [Fact]
    public async Task ChunkerEmitsLineLocationsOnMarkdownInput()
    {
        const string Markdown = """
            # Document Title

            Introductory paragraph explaining the system design and architecture in detail.
            Additional sentences to build up some tokens in this introductory section.

            ## Section A

            Section A content block describing operations, configurations, and prerequisites.
            Another sentence detailing the workflow.

            ```csharp
            public class Worker
            {
                public void Execute() => Console.WriteLine("Running");
            }
            ```

            | Step | Action | Result |
            |---|---|---|
            | 1 | Init | Ready |
            | 2 | Start | Running |
            | 3 | Stop | Finished |

            Concluding remarks for the document.
            """;

        TextDocumentReader reader = new();
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(Markdown));
        IngestionDocument doc = await reader.ReadAsync(
            stream, "docs/guide.md", "text/markdown", TestContext.Current.CancellationToken);

        RecursiveTokenChunker chunker = new(DefaultOptions);
        List<IngestionChunk<string>> chunks = [];
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Should().NotBeEmpty();
        foreach (var chunk in chunks)
        {
            AssertChunkLinesContainContent(Markdown, chunk);
            chunk.Metadata[ChunkMetadataKeys.FilePath].Should().Be("docs/guide.md");
        }

        // Verify breadcrumb exists on chunks under sections
        chunks.Any(c => c.Context == "Document Title").Should().BeTrue();
        chunks.Any(c => c.Context == "Document Title > Section A").Should().BeTrue();
    }

    [Fact]
    public async Task ChunkerEmitsLineLocationsOnXmlInput()
    {
        const string Xml = """
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Microsoft.Extensions.DataIngestion" Version="1.0.0" />
                <PackageReference Include="Microsoft.ML.Tokenizers" Version="1.0.0" />
              </ItemGroup>

            </Project>
            """;

        TextDocumentReader reader = new();
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(Xml));
        IngestionDocument doc = await reader.ReadAsync(
            stream, "src/MyProject.csproj", "application/xml", TestContext.Current.CancellationToken);

        RecursiveTokenChunker chunker = new(new RecursiveChunkerOptions(TokenizerFixture.Instance)
        {
            MaxTokensPerChunk = 32,
            OverlapTokens = 8,
        });

        List<IngestionChunk<string>> chunks = [];
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Should().NotBeEmpty();
        foreach (var chunk in chunks)
        {
            AssertChunkLinesContainContent(Xml, chunk);
            chunk.Metadata[ChunkMetadataKeys.FilePath].Should().Be("src/MyProject.csproj");
        }
    }

    [Fact]
    public async Task ChunkerEmitsLineLocationsOnPlainTextWithCrlfLineEndings()
    {
        const string PlainText = "First line of text.\r\nSecond line of text with CRLF endings.\r\n\r\nThird block after blank line.\r\nFourth line here.\r\nFifth line here.\r\n";

        TextDocumentReader reader = new();
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(PlainText));
        IngestionDocument doc = await reader.ReadAsync(
            stream, "notes.txt", "text/plain", TestContext.Current.CancellationToken);

        RecursiveTokenChunker chunker = new(new RecursiveChunkerOptions(TokenizerFixture.Instance)
        {
            MaxTokensPerChunk = 16,
            OverlapTokens = 4,
        });

        List<IngestionChunk<string>> chunks = [];
        await foreach (var chunk in chunker.ProcessAsync(doc, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Should().NotBeEmpty();
        foreach (var chunk in chunks)
        {
            AssertChunkLinesContainContent(PlainText, chunk);
            chunk.Metadata[ChunkMetadataKeys.FilePath].Should().Be("notes.txt");
        }
    }

    private static void AssertChunkLinesContainContent(string fullText, IngestionChunk<string> chunk)
    {
        chunk.Metadata.Should().ContainKey(ChunkMetadataKeys.StartLine);
        chunk.Metadata.Should().ContainKey(ChunkMetadataKeys.EndLine);
        chunk.Metadata.Should().ContainKey(ChunkMetadataKeys.LineRange);
        chunk.Metadata.Should().ContainKey(ChunkMetadataKeys.FilePath);

        int startLine = (int)chunk.Metadata[ChunkMetadataKeys.StartLine]!;
        int endLine = (int)chunk.Metadata[ChunkMetadataKeys.EndLine]!;
        string lineRange = (string)chunk.Metadata[ChunkMetadataKeys.LineRange]!;

        lineRange.Should().Be($"{startLine}-{endLine}");
        startLine.Should().BeGreaterThanOrEqualTo(1);
        endLine.Should().BeGreaterThanOrEqualTo(startLine);

        string[] lines = fullText.Replace("\r\n", "\n").Split('\n');
        endLine.Should().BeLessThanOrEqualTo(lines.Length);

        string spannedLinesText = string.Join("\n", lines[(startLine - 1)..endLine]);

        string normalizedChunk = chunk.Content.Replace("\r\n", "\n").Trim();
        string normalizedSpanned = spannedLinesText.Trim();
        normalizedSpanned.Should().Contain(normalizedChunk);
    }
}
